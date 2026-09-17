using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

const string DefaultWorkspaceGid = "1201060253118530";
const string DefaultProjectGid = "1210048713791963";
const string DefaultOutputDirectory = @"C:\Users\kylep\Kallman Worldwide, Inc\Data Warehouse - Documents\Asana";

try
{
    var options = Options.Parse(args, DefaultWorkspaceGid, DefaultProjectGid, DefaultOutputDirectory);
    if (options.ShowHelp)
    {
        Options.PrintHelp();
        return 0;
    }

    var token = Environment.GetEnvironmentVariable("ASANA_PAT");
    if (string.IsNullOrWhiteSpace(token))
        throw new InvalidOperationException("ASANA_PAT is not set. Store the rotated token in that environment variable and reopen the terminal.");

    using var http = new HttpClient { BaseAddress = new Uri("https://app.asana.com/api/1.0/"), Timeout = TimeSpan.FromMinutes(2) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Kallman-AsanaWarehousePull/0.1");

    var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
    var runRoot = Path.GetFullPath(Path.Combine(options.OutputDirectory, "runs", runId));
    Directory.CreateDirectory(runRoot);
    var rawRoot = Path.Combine(runRoot, "raw");
    Directory.CreateDirectory(rawRoot);

    var client = new AsanaReadOnlyClient(http, rawRoot);
    if (options.Mode is RunMode.Full or RunMode.Probe)
    {
        var exitCode = await WorkspaceWarehouse.RunAsync(client, options, runId, runRoot);
        return exitCode;
    }
    Console.WriteLine($"Read-only extraction started for project {options.ProjectGid}.");

    var project = await client.GetOneAsync($"projects/{options.ProjectGid}", Fields.Project, "project");
    var sections = await client.GetAllAsync($"projects/{options.ProjectGid}/sections", Fields.Section, "sections");
    var rootTasks = await client.GetAllAsync($"projects/{options.ProjectGid}/tasks", Fields.Task, "tasks");

    var allTasks = new List<TaskRecord>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var queue = new Queue<(JsonElement Item, string? ParentGid, int Depth)>();
    foreach (var task in rootTasks) queue.Enqueue((task, null, 0));

    while (queue.Count > 0)
    {
        var current = queue.Dequeue();
        var gid = JsonValue.String(current.Item, "gid") ?? throw new InvalidDataException("Task response omitted gid.");
        if (!seen.Add(gid)) continue;
        allTasks.Add(new TaskRecord(current.Item, current.ParentGid, current.Depth));

        if (JsonValue.Integer(current.Item, "num_subtasks") > 0)
        {
            var subtasks = await client.GetAllAsync($"tasks/{gid}/subtasks", Fields.Task, $"subtasks-{gid}");
            foreach (var subtask in subtasks) queue.Enqueue((subtask, gid, current.Depth + 1));
        }
    }

    WarehouseWriter.Write(runRoot, options.WorkspaceGid, project, sections, allTasks);
    WarehouseWriter.PublishCurrent(options.OutputDirectory, runRoot);

    var manifest = new
    {
        run_id = runId,
        started_utc = runId,
        completed_utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        mode = "read-only",
        workspace_gid = options.WorkspaceGid,
        project_gid = options.ProjectGid,
        project_name = JsonValue.String(project, "name"),
        section_count = sections.Count,
        task_count = allTasks.Count,
        root_task_count = allTasks.Count(x => x.Depth == 0),
        subtask_count = allTasks.Count(x => x.Depth > 0),
        http_methods_allowed = new[] { "GET" }
    };
    File.WriteAllText(Path.Combine(runRoot, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions.Indented));
    File.Copy(Path.Combine(runRoot, "manifest.json"), Path.Combine(options.OutputDirectory, "current", "manifest.json"), true);

    Console.WriteLine($"Completed: {sections.Count} sections, {allTasks.Count} tasks ({allTasks.Count(x => x.Depth > 0)} subtasks).");
    Console.WriteLine($"Published CSVs: {Path.Combine(Path.GetFullPath(options.OutputDirectory), "current")}");
    Console.WriteLine($"Immutable run: {runRoot}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

internal enum RunMode { Project, Probe, Full }

internal sealed record Options(string WorkspaceGid, string ProjectGid, string OutputDirectory, RunMode Mode, bool IncludeArchived, bool ShowHelp)
{
    public static Options Parse(string[] args, string defaultWorkspace, string defaultProject, string defaultOutput)
    {
        var workspace = defaultWorkspace;
        var project = defaultProject;
        var output = defaultOutput;
        var mode = RunMode.Full;
        var includeArchived = false;
        var help = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace": workspace = RequireValue(args, ref i); break;
                case "--project": project = RequireValue(args, ref i); break;
                case "--output": output = RequireValue(args, ref i); break;
                case "--mode":
                    var modeValue = RequireValue(args, ref i);
                    if (!Enum.TryParse<RunMode>(modeValue, true, out mode)) throw new ArgumentException("Mode must be project, probe, or full.");
                    break;
                case "--include-archived": includeArchived = true; break;
                case "--help" or "-h": help = true; break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
        if (!workspace.All(char.IsDigit) || !project.All(char.IsDigit)) throw new ArgumentException("Workspace and project GIDs must contain digits only.");
        return new(workspace, project, output, mode, includeArchived, help);
    }

    private static string RequireValue(string[] args, ref int index) => ++index < args.Length ? args[index] : throw new ArgumentException($"Missing value for {args[index - 1]}.");

    public static void PrintHelp() => Console.WriteLine("AsanaWarehousePull [--mode project|probe|full] [--workspace GID] [--project GID] [--include-archived] [--output PATH]\nOnly GET requests are implemented. Token: ASANA_PAT.");
}

internal sealed class AsanaReadOnlyClient(HttpClient http, string rawRoot)
{
    private int requestNumber;

    public async Task<JsonElement> GetOneAsync(string path, string fields, string rawName)
    {
        using var document = await GetDocumentAsync(BuildUri(path, fields, null), rawName);
        return document.RootElement.GetProperty("data").Clone();
    }

    public async Task<List<JsonElement>> GetAllAsync(string path, string fields, string rawName)
    {
        var results = new List<JsonElement>();
        string? offset = null;
        var page = 0;
        do
        {
            page++;
            using var document = await GetDocumentAsync(BuildUri(path, fields, offset), $"{rawName}-page-{page:D4}");
            foreach (var item in document.RootElement.GetProperty("data").EnumerateArray()) results.Add(item.Clone());
            offset = null;
            if (document.RootElement.TryGetProperty("next_page", out var next) && next.ValueKind == JsonValueKind.Object &&
                next.TryGetProperty("offset", out var offsetElement) && offsetElement.ValueKind == JsonValueKind.String)
                offset = offsetElement.GetString();
        } while (!string.IsNullOrEmpty(offset));
        return results;
    }

    private async Task<JsonDocument> GetDocumentAsync(string relativeUri, string rawName)
    {
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var body = await response.Content.ReadAsStringAsync();
            requestNumber++;
            var safeName = string.Concat(rawName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            await File.WriteAllTextAsync(Path.Combine(rawRoot, $"{requestNumber:D5}-{safeName}.json"), body);
            if (response.IsSuccessStatusCode) return JsonDocument.Parse(body);
            if (response.StatusCode == (HttpStatusCode)429 && attempt < 6)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
                Console.WriteLine($"Rate limited; retrying after {delay.TotalSeconds:0} seconds.");
                await Task.Delay(delay);
                continue;
            }
            throw new HttpRequestException($"Asana GET {relativeUri.Split('?')[0]} returned {(int)response.StatusCode} {response.ReasonPhrase}. See raw response {safeName}.");
        }
        throw new HttpRequestException("Asana request retry limit exceeded.");
    }

    private static string BuildUri(string path, string fields, string? offset)
    {
        var query = new List<string> { "limit=100", $"opt_fields={Uri.EscapeDataString(fields)}" };
        if (!string.IsNullOrEmpty(offset)) query.Add($"offset={Uri.EscapeDataString(offset)}");
        return $"{path}{(path.Contains('?') ? '&' : '?')}{string.Join('&', query)}";
    }
}

internal static class Fields
{
    public const string Project = "gid,name,resource_subtype,archived,created_at,modified_at,start_on,due_on,notes,permalink_url,privacy_setting,default_view,created_by.gid,created_by.name,owner.gid,owner.name,team.gid,team.name,workspace.gid,workspace.name,current_status_update.gid,current_status_update.title,current_status_update.resource_subtype,members.gid,members.name,followers.gid,followers.name,custom_field_settings.custom_field.gid,custom_field_settings.custom_field.name,custom_field_settings.custom_field.resource_subtype";
    public const string Section = "gid,name,created_at,project.gid,project.name";
    public const string Task = "gid,name,resource_subtype,approval_status,completed,completed_at,created_at,modified_at,due_on,due_at,start_on,start_at,created_by.gid,created_by.name,assignee.gid,assignee.name,assignee.email,followers.gid,followers.name,dependencies.gid,dependents.gid,notes,permalink_url,num_subtasks,parent.gid,parent.name,memberships.project.gid,memberships.project.name,memberships.section.gid,memberships.section.name,custom_fields.gid,custom_fields.name,custom_fields.resource_subtype,custom_fields.display_value,custom_fields.text_value,custom_fields.number_value,custom_fields.enum_value.gid,custom_fields.enum_value.name,custom_fields.multi_enum_values.gid,custom_fields.multi_enum_values.name,custom_fields.date_value.date,custom_fields.date_value.date_time,tags.gid,tags.name";
    public const string User = "gid,name,email,resource_type,photo.image_128x128";
    public const string WorkspaceMembership = "gid,user.gid,user.name,user.email,workspace.gid,workspace.name,is_active,is_admin,is_guest";
    public const string Team = "gid,name,description,visibility,permalink_url,organization.gid,organization.name";
    public const string TeamMembership = "gid,user.gid,user.name,team.gid,team.name,is_admin,is_guest,is_limited_access";
    public const string StatusUpdate = "gid,title,resource_subtype,text,status_type,created_at,modified_at,num_likes,author.gid,author.name,created_by.gid,created_by.name,parent.gid,parent.name,parent.resource_subtype";
    public const string Attachment = "gid,name,resource_subtype,created_at,permanent_url,view_url,host,parent.gid,parent.name,parent.resource_subtype,size,connected_to_app";
    public const string Portfolio = "gid,name,created_at,modified_at,owner.gid,owner.name,workspace.gid,workspace.name,permalink_url,privacy_setting,current_status_update.gid,current_status_update.title,current_status_update.resource_subtype,custom_field_settings.gid,custom_field_settings.is_important,custom_field_settings.custom_field.gid,custom_field_settings.custom_field.name,custom_field_settings.custom_field.resource_subtype";
    public const string PortfolioMembership = "gid,user.gid,user.name,portfolio.gid,portfolio.name";
    public const string PortfolioItem = "gid,name,resource_type,resource_subtype";
    public const string Goal = "gid,name,resource_type,status,is_workspace_level,start_on,due_on,notes,privacy_setting,default_access_level,owner.gid,owner.name,team.gid,team.name,workspace.gid,workspace.name,followers.gid,followers.name,current_status_update.gid,current_status_update.title,current_status_update.resource_subtype,time_period.gid,time_period.display_name,time_period.period,time_period.start_on,time_period.end_on,metric.gid,metric.resource_subtype,metric.precision,metric.current_number_value,metric.target_number_value,metric.unit,custom_fields.gid,custom_fields.name,custom_fields.resource_subtype,custom_fields.display_value,custom_fields.text_value,custom_fields.number_value,custom_fields.enum_value.gid,custom_fields.enum_value.name,custom_fields.multi_enum_values.gid,custom_fields.multi_enum_values.name,custom_fields.date_value.date,custom_fields.date_value.date_time";
    public const string GoalRelationship = "gid,resource_type,resource_subtype,contribution_weight,supported_goal.gid,supported_goal.name,supported_goal.owner.gid,supported_goal.owner.name,supporting_resource.gid,supporting_resource.name,supporting_resource.resource_type,supporting_resource.resource_subtype";
}

internal sealed record TaskRecord(JsonElement Data, string? ParentGid, int Depth);

internal static class WarehouseWriter
{
    public static void Write(string root, string workspaceGid, JsonElement project, List<JsonElement> sections, List<TaskRecord> tasks)
    {
        Csv.Write(Path.Combine(root, "Asana_Projects.csv"),
            ["WorkspaceGid", "ProjectGid", "Name", "Archived", "CreatedAt", "ModifiedAt", "OwnerGid", "OwnerName", "TeamGid", "TeamName", "Notes", "PermalinkUrl"],
            [[workspaceGid, S(project,"gid"), S(project,"name"), S(project,"archived"), S(project,"created_at"), S(project,"modified_at"), N(project,"owner","gid"), N(project,"owner","name"), N(project,"team","gid"), N(project,"team","name"), S(project,"notes"), S(project,"permalink_url")]]);

        Csv.Write(Path.Combine(root, "Asana_Sections.csv"), ["ProjectGid", "SectionGid", "Name", "CreatedAt"],
            sections.Select(x => new[] { S(project,"gid"), S(x,"gid"), S(x,"name"), S(x,"created_at") }));

        Csv.Write(Path.Combine(root, "Asana_Tasks.csv"),
            ["ProjectGid", "TaskGid", "ParentTaskGid", "Depth", "Name", "ResourceSubtype", "Completed", "CompletedAt", "CreatedAt", "ModifiedAt", "StartOn", "StartAt", "DueOn", "DueAt", "AssigneeGid", "AssigneeName", "AssigneeEmail", "NumSubtasks", "Notes", "PermalinkUrl"],
            tasks.Select(t => new[] { S(project,"gid"), S(t.Data,"gid"), t.ParentGid ?? "", t.Depth.ToString(CultureInfo.InvariantCulture), S(t.Data,"name"), S(t.Data,"resource_subtype"), S(t.Data,"completed"), S(t.Data,"completed_at"), S(t.Data,"created_at"), S(t.Data,"modified_at"), S(t.Data,"start_on"), S(t.Data,"start_at"), S(t.Data,"due_on"), S(t.Data,"due_at"), N(t.Data,"assignee","gid"), N(t.Data,"assignee","name"), N(t.Data,"assignee","email"), S(t.Data,"num_subtasks"), S(t.Data,"notes"), S(t.Data,"permalink_url") }));

        Csv.Write(Path.Combine(root, "Asana_Task_Memberships.csv"), ["TaskGid", "ProjectGid", "ProjectName", "SectionGid", "SectionName"],
            tasks.SelectMany(t => Array(t.Data,"memberships").Select(m => new[] { S(t.Data,"gid"), N(m,"project","gid"), N(m,"project","name"), N(m,"section","gid"), N(m,"section","name") })));

        Csv.Write(Path.Combine(root, "Asana_Task_CustomFields.csv"), ["TaskGid", "CustomFieldGid", "Name", "ResourceSubtype", "DisplayValue", "TextValue", "NumberValue", "EnumGid", "EnumValue", "MultiEnumValues", "Date", "DateTime"],
            tasks.SelectMany(t => Array(t.Data,"custom_fields").Select(f => new[] { S(t.Data,"gid"), S(f,"gid"), S(f,"name"), S(f,"resource_subtype"), S(f,"display_value"), S(f,"text_value"), S(f,"number_value"), N(f,"enum_value","gid"), N(f,"enum_value","name"), string.Join(" | ", Array(f,"multi_enum_values").Select(v => S(v,"name"))), N(f,"date_value","date"), N(f,"date_value","date_time") })));

        Csv.Write(Path.Combine(root, "Asana_Task_Tags.csv"), ["TaskGid", "TagGid", "TagName"],
            tasks.SelectMany(t => Array(t.Data,"tags").Select(tag => new[] { S(t.Data,"gid"), S(tag,"gid"), S(tag,"name") })));
    }

    public static void PublishCurrent(string outputDirectory, string runRoot)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var staging = Path.Combine(output, $".current-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        foreach (var source in Directory.GetFiles(runRoot, "*.csv")) File.Copy(source, Path.Combine(staging, Path.GetFileName(source)));
        var current = Path.Combine(output, "current");
        if (Directory.Exists(current))
        {
            var history = Path.Combine(output, "history");
            Directory.CreateDirectory(history);
            var previous = Path.Combine(history, $"current-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}");
            Directory.Move(current, previous);
        }
        Directory.Move(staging, current);
    }

    private static string S(JsonElement element, string property) => JsonValue.String(element, property) ?? "";
    private static string N(JsonElement element, string parent, string property) => element.TryGetProperty(parent, out var nested) && nested.ValueKind == JsonValueKind.Object ? S(nested, property) : "";
    private static IEnumerable<JsonElement> Array(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(x => x.Clone()) : [];
}

internal static class Csv
{
    public static void Write(string path, string[] headers, IEnumerable<string[]> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join(',', headers.Select(Escape)));
        foreach (var row in rows) writer.WriteLine(string.Join(',', row.Select(Escape)));
    }
    private static string Escape(string? value)
    {
        value ??= "";
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}

internal static class JsonValue
{
    public static string? String(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    public static int Integer(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
