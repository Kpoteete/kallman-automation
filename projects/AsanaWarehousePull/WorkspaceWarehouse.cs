using System.Globalization;
using System.Text.Json;

internal static class WorkspaceWarehouse
{
    public static async Task<int> RunAsync(AsanaReadOnlyClient client, Options options, string runId, string runRoot)
    {
        var users = await client.GetAllAsync($"workspaces/{options.WorkspaceGid}/users", Fields.User, "workspace-users");
        var workspaceMemberships = await client.GetAllAsync($"workspaces/{options.WorkspaceGid}/workspace_memberships", Fields.WorkspaceMembership, "workspace-memberships");
        var teams = await client.GetAllAsync($"workspaces/{options.WorkspaceGid}/teams", Fields.Team, "workspace-teams");
        var teamMemberships = new List<JsonElement>();
        foreach (var team in teams)
        {
            var teamGid = Value(team, "gid");
            teamMemberships.AddRange(await client.GetAllAsync($"team_memberships?team={Uri.EscapeDataString(teamGid)}", Fields.TeamMembership, $"team-memberships-{teamGid}"));
        }

        var projects = await client.GetAllAsync(
            $"workspaces/{options.WorkspaceGid}/projects?archived=false",
            Fields.Project,
            "active-projects");

        if (options.IncludeArchived)
        {
            var archived = await client.GetAllAsync(
                $"workspaces/{options.WorkspaceGid}/projects?archived=true",
                Fields.Project,
                "archived-projects");
            projects.AddRange(archived);
        }

        projects = projects
            .GroupBy(p => Value(p, "gid"), StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(p => Value(p, "name"), StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"Workspace probe found {projects.Count} {(options.IncludeArchived ? "active and archived" : "active")} projects, {users.Count} users, and {teams.Count} teams.");
        if (options.Mode == RunMode.Probe)
        {
            Console.WriteLine("Probe completed. No warehouse CSVs were published.");
            return 0;
        }

        using var runLock = AcquireLock(options.OutputDirectory);
        var sections = new List<JsonElement>();
        var statusUpdates = new List<JsonElement>();
        var tasks = new Dictionary<string, TaskRecord>(StringComparer.Ordinal);
        var queued = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(JsonElement Item, string? ParentGid, int Depth)>();
        var projectNumber = 0;

        foreach (var project in projects)
        {
            projectNumber++;
            var projectGid = Value(project, "gid");
            Console.WriteLine($"[{projectNumber}/{projects.Count}] {Value(project, "name")}");
            sections.AddRange(await client.GetAllAsync($"projects/{projectGid}/sections", Fields.Section, $"sections-{projectGid}"));
            statusUpdates.AddRange(await client.GetAllAsync($"status_updates?parent={Uri.EscapeDataString(projectGid)}", Fields.StatusUpdate, $"status-updates-{projectGid}"));
            var projectTasks = await client.GetAllAsync($"projects/{projectGid}/tasks", Fields.Task, $"tasks-{projectGid}");
            foreach (var task in projectTasks)
            {
                var taskGid = Value(task, "gid");
                if (queued.Add(taskGid)) queue.Enqueue((task, null, 0));
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var gid = Value(current.Item, "gid");
            tasks.TryAdd(gid, new TaskRecord(current.Item, current.ParentGid, current.Depth));
            if (JsonValue.Integer(current.Item, "num_subtasks") <= 0) continue;

            var subtasks = await client.GetAllAsync($"tasks/{gid}/subtasks", Fields.Task, $"subtasks-{gid}");
            foreach (var subtask in subtasks)
            {
                var subtaskGid = Value(subtask, "gid");
                if (queued.Add(subtaskGid)) queue.Enqueue((subtask, gid, current.Depth + 1));
            }
        }

        Console.WriteLine("Finding attachment-bearing tasks and pulling metadata only (no file downloads).");
        var attachmentSearch = await client.GetAllAsync($"workspaces/{options.WorkspaceGid}/tasks/search?has_attachment=true", "gid,name", "attachment-task-search");
        var attachmentTasks = attachmentSearch.Where(t => tasks.ContainsKey(Value(t, "gid"))).ToList();
        var attachments = new List<JsonElement>();
        foreach (var task in attachmentTasks)
        {
            var taskGid = Value(task, "gid");
            attachments.AddRange(await client.GetAllAsync($"attachments?parent={Uri.EscapeDataString(taskGid)}", Fields.Attachment, $"attachments-{taskGid}"));
        }

        var me = await client.GetOneAsync("users/me", "gid,name", "current-user");
        var ownerGid = Value(me, "gid");
        var portfolios = await client.GetAllAsync($"portfolios?workspace={options.WorkspaceGid}&owner={Uri.EscapeDataString(ownerGid)}", Fields.Portfolio, "portfolios");
        var portfolioItems = new List<PortfolioChildRecord>();
        var portfolioMemberships = new List<JsonElement>();
        var portfolioStatusUpdates = new List<JsonElement>();
        foreach (var portfolio in portfolios)
        {
            var portfolioGid = Value(portfolio, "gid");
            var portfolioName = Value(portfolio, "name");
            var items = await client.GetAllAsync($"portfolios/{portfolioGid}/items", Fields.PortfolioItem, $"portfolio-items-{portfolioGid}");
            portfolioItems.AddRange(items.Select(item => new PortfolioChildRecord(portfolioGid, portfolioName, item)));
            portfolioMemberships.AddRange(await client.GetAllAsync($"portfolio_memberships?portfolio={portfolioGid}", Fields.PortfolioMembership, $"portfolio-memberships-{portfolioGid}"));
            portfolioStatusUpdates.AddRange(await client.GetAllAsync($"status_updates?parent={portfolioGid}", Fields.StatusUpdate, $"portfolio-status-updates-{portfolioGid}"));
        }

        var goals = await client.GetAllAsync($"goals?workspace={options.WorkspaceGid}", Fields.Goal, "goals");
        var goalRelationships = new List<JsonElement>();
        var goalStatusUpdates = new List<JsonElement>();
        foreach (var goal in goals)
        {
            var goalGid = Value(goal, "gid");
            goalRelationships.AddRange(await client.GetAllAsync($"goal_relationships?supported_goal={goalGid}", Fields.GoalRelationship, $"goal-relationships-{goalGid}"));
            goalStatusUpdates.AddRange(await client.GetAllAsync($"status_updates?parent={goalGid}", Fields.StatusUpdate, $"goal-status-updates-{goalGid}"));
        }

        WorkspaceWarehouseWriter.Write(runRoot, options.WorkspaceGid, projects, sections, statusUpdates, tasks.Values.ToList(), users, workspaceMemberships, teams, teamMemberships, attachments, portfolios, portfolioItems, portfolioMemberships, portfolioStatusUpdates, goals, goalRelationships, goalStatusUpdates);
        var manifest = new
        {
            run_id = runId,
            completed_utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            mode = "full-read-only",
            workspace_gid = options.WorkspaceGid,
            included_archived_projects = options.IncludeArchived,
            project_count = projects.Count,
            archived_project_count = projects.Count(p => string.Equals(Value(p, "archived"), "true", StringComparison.OrdinalIgnoreCase)),
            section_count = sections.Count,
            project_status_update_count = statusUpdates.Count,
            task_count = tasks.Count,
            root_task_count = tasks.Values.Count(x => x.Depth == 0),
            subtask_count = tasks.Values.Count(x => x.Depth > 0),
            user_count = users.Count,
            workspace_membership_count = workspaceMemberships.Count,
            team_count = teams.Count,
            team_membership_count = teamMemberships.Count,
            project_member_count = projects.Sum(p => Array(p, "members").Count()),
            project_follower_count = projects.Sum(p => Array(p, "followers").Count()),
            task_follower_count = tasks.Values.Sum(t => Array(t.Data, "followers").Count()),
            task_dependency_count = tasks.Values.Sum(t => Array(t.Data, "dependencies").Count()),
            task_dependent_count = tasks.Values.Sum(t => Array(t.Data, "dependents").Count()),
            attachment_count = attachments.Count,
            attachment_task_count = attachmentTasks.Count,
            attachment_files_downloaded = 0,
            portfolio_count = portfolios.Count,
            portfolio_item_count = portfolioItems.Count,
            portfolio_membership_count = portfolioMemberships.Count,
            portfolio_status_update_count = portfolioStatusUpdates.Count,
            portfolio_scope = "Portfolios owned by the authenticated PAT user; Asana's portfolios endpoint requires an owner.",
            goal_count = goals.Count,
            goal_relationship_count = goalRelationships.Count,
            goal_status_update_count = goalStatusUpdates.Count,
            http_methods_allowed = new[] { "GET" }
        };
        File.WriteAllText(Path.Combine(runRoot, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions.Indented));
        WarehouseWriter.PublishCurrent(options.OutputDirectory, runRoot);
        File.Copy(Path.Combine(runRoot, "manifest.json"), Path.Combine(options.OutputDirectory, "current", "manifest.json"), true);

        Console.WriteLine($"Full workspace pull completed: {projects.Count} projects, {sections.Count} sections, {tasks.Count} unique tasks.");
        Console.WriteLine($"Published CSVs: {Path.Combine(Path.GetFullPath(options.OutputDirectory), "current")}");
        Console.WriteLine($"Immutable run: {runRoot}");
        return 0;
    }

    private static FileStream AcquireLock(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var lockPath = Path.Combine(output, ".AsanaWarehousePull.lock");
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Another Asana warehouse run appears active. Lock: {lockPath}", ex);
        }
    }

    private static string Value(JsonElement element, string property) => JsonValue.String(element, property) ?? "";
    private static IEnumerable<JsonElement> Array(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(x => x.Clone()) : [];
}

internal sealed record PortfolioChildRecord(string PortfolioGid, string PortfolioName, JsonElement Data);

internal static class WorkspaceWarehouseWriter
{
    public static void Write(string root, string workspaceGid, List<JsonElement> projects, List<JsonElement> sections, List<JsonElement> statusUpdates, List<TaskRecord> tasks, List<JsonElement> users, List<JsonElement> workspaceMemberships, List<JsonElement> teams, List<JsonElement> teamMemberships, List<JsonElement> attachments, List<JsonElement> portfolios, List<PortfolioChildRecord> portfolioItems, List<JsonElement> portfolioMemberships, List<JsonElement> portfolioStatusUpdates, List<JsonElement> goals, List<JsonElement> goalRelationships, List<JsonElement> goalStatusUpdates)
    {
        Csv.Write(Path.Combine(root, "Asana_Projects.csv"),
            ["WorkspaceGid", "ProjectGid", "Name", "ResourceSubtype", "Archived", "CreatedAt", "ModifiedAt", "StartOn", "DueOn", "PrivacySetting", "DefaultView", "CreatedByGid", "CreatedByName", "OwnerGid", "OwnerName", "TeamGid", "TeamName", "CurrentStatusGid", "CurrentStatusTitle", "CurrentStatusSubtype", "Notes", "PermalinkUrl"],
            projects.Select(p => new[] { workspaceGid, S(p,"gid"), S(p,"name"), S(p,"resource_subtype"), S(p,"archived"), S(p,"created_at"), S(p,"modified_at"), S(p,"start_on"), S(p,"due_on"), S(p,"privacy_setting"), S(p,"default_view"), N(p,"created_by","gid"), N(p,"created_by","name"), N(p,"owner","gid"), N(p,"owner","name"), N(p,"team","gid"), N(p,"team","name"), N(p,"current_status_update","gid"), N(p,"current_status_update","title"), N(p,"current_status_update","resource_subtype"), S(p,"notes"), S(p,"permalink_url") }));

        Csv.Write(Path.Combine(root, "Asana_Project_CustomFields.csv"),
            ["ProjectGid", "CustomFieldGid", "Name", "ResourceSubtype"],
            projects.SelectMany(p => Array(p,"custom_field_settings").Select(setting => Object(setting,"custom_field")).Where(f => f.HasValue).Select(f => new[] { S(p,"gid"), S(f!.Value,"gid"), S(f.Value,"name"), S(f.Value,"resource_subtype") })));

        Csv.Write(Path.Combine(root, "Asana_Sections.csv"), ["ProjectGid", "SectionGid", "Name", "CreatedAt"],
            sections.Select(s => new[] { N(s,"project","gid"), S(s,"gid"), S(s,"name"), S(s,"created_at") }));

        Csv.Write(Path.Combine(root, "Asana_Project_Status_Updates.csv"),
            ["StatusUpdateGid", "ProjectGid", "ProjectName", "Title", "ResourceSubtype", "StatusType", "Text", "AuthorGid", "AuthorName", "CreatedByGid", "CreatedByName", "CreatedAt", "ModifiedAt", "NumLikes"],
            statusUpdates.Select(s => new[] { S(s,"gid"), N(s,"parent","gid"), N(s,"parent","name"), S(s,"title"), S(s,"resource_subtype"), S(s,"status_type"), S(s,"text"), N(s,"author","gid"), N(s,"author","name"), N(s,"created_by","gid"), N(s,"created_by","name"), S(s,"created_at"), S(s,"modified_at"), S(s,"num_likes") }));

        Csv.Write(Path.Combine(root, "Asana_Tasks.csv"),
            ["TaskGid", "ParentTaskGid", "Depth", "Name", "ResourceSubtype", "ApprovalStatus", "Completed", "CompletedAt", "CreatedAt", "ModifiedAt", "StartOn", "StartAt", "DueOn", "DueAt", "CreatedByGid", "CreatedByName", "AssigneeGid", "AssigneeName", "AssigneeEmail", "NumSubtasks", "Notes", "PermalinkUrl"],
            tasks.Select(t => new[] { S(t.Data,"gid"), t.ParentGid ?? "", t.Depth.ToString(CultureInfo.InvariantCulture), S(t.Data,"name"), S(t.Data,"resource_subtype"), S(t.Data,"approval_status"), S(t.Data,"completed"), S(t.Data,"completed_at"), S(t.Data,"created_at"), S(t.Data,"modified_at"), S(t.Data,"start_on"), S(t.Data,"start_at"), S(t.Data,"due_on"), S(t.Data,"due_at"), N(t.Data,"created_by","gid"), N(t.Data,"created_by","name"), N(t.Data,"assignee","gid"), N(t.Data,"assignee","name"), N(t.Data,"assignee","email"), S(t.Data,"num_subtasks"), S(t.Data,"notes"), S(t.Data,"permalink_url") }));

        Csv.Write(Path.Combine(root, "Asana_Task_Memberships.csv"), ["TaskGid", "ProjectGid", "ProjectName", "SectionGid", "SectionName"],
            tasks.SelectMany(t => Array(t.Data,"memberships").Select(m => new[] { S(t.Data,"gid"), N(m,"project","gid"), N(m,"project","name"), N(m,"section","gid"), N(m,"section","name") })));

        Csv.Write(Path.Combine(root, "Asana_Task_CustomFields.csv"), ["TaskGid", "CustomFieldGid", "Name", "ResourceSubtype", "DisplayValue", "TextValue", "NumberValue", "EnumGid", "EnumValue", "MultiEnumValues", "Date", "DateTime"],
            tasks.SelectMany(t => Array(t.Data,"custom_fields").Select(f => new[] { S(t.Data,"gid"), S(f,"gid"), S(f,"name"), S(f,"resource_subtype"), S(f,"display_value"), S(f,"text_value"), S(f,"number_value"), N(f,"enum_value","gid"), N(f,"enum_value","name"), string.Join(" | ", Array(f,"multi_enum_values").Select(v => S(v,"name"))), N(f,"date_value","date"), N(f,"date_value","date_time") })));

        Csv.Write(Path.Combine(root, "Asana_Task_Tags.csv"), ["TaskGid", "TagGid", "TagName"],
            tasks.SelectMany(t => Array(t.Data,"tags").Select(tag => new[] { S(t.Data,"gid"), S(tag,"gid"), S(tag,"name") })));

        Csv.Write(Path.Combine(root, "Asana_Users.csv"), ["WorkspaceGid", "UserGid", "Name", "Email", "PhotoUrl"],
            users.Select(u => new[] { workspaceGid, S(u,"gid"), S(u,"name"), S(u,"email"), N(u,"photo","image_128x128") }));

        Csv.Write(Path.Combine(root, "Asana_Workspace_Memberships.csv"), ["WorkspaceMembershipGid", "WorkspaceGid", "WorkspaceName", "UserGid", "UserName", "UserEmail", "IsActive", "IsAdmin", "IsGuest"],
            workspaceMemberships.Select(m => new[] { S(m,"gid"), N(m,"workspace","gid"), N(m,"workspace","name"), N(m,"user","gid"), N(m,"user","name"), N(m,"user","email"), S(m,"is_active"), S(m,"is_admin"), S(m,"is_guest") }));

        Csv.Write(Path.Combine(root, "Asana_Teams.csv"), ["WorkspaceGid", "TeamGid", "Name", "Visibility", "Description", "PermalinkUrl"],
            teams.Select(t => new[] { workspaceGid, S(t,"gid"), S(t,"name"), S(t,"visibility"), S(t,"description"), S(t,"permalink_url") }));

        Csv.Write(Path.Combine(root, "Asana_Team_Memberships.csv"), ["TeamMembershipGid", "TeamGid", "TeamName", "UserGid", "UserName", "IsAdmin", "IsGuest", "IsLimitedAccess"],
            teamMemberships.Select(m => new[] { S(m,"gid"), N(m,"team","gid"), N(m,"team","name"), N(m,"user","gid"), N(m,"user","name"), S(m,"is_admin"), S(m,"is_guest"), S(m,"is_limited_access") }));

        Csv.Write(Path.Combine(root, "Asana_Project_Members.csv"), ["ProjectGid", "UserGid", "UserName"],
            projects.SelectMany(p => Array(p,"members").Select(u => new[] { S(p,"gid"), S(u,"gid"), S(u,"name") })));

        Csv.Write(Path.Combine(root, "Asana_Project_Followers.csv"), ["ProjectGid", "UserGid", "UserName"],
            projects.SelectMany(p => Array(p,"followers").Select(u => new[] { S(p,"gid"), S(u,"gid"), S(u,"name") })));

        Csv.Write(Path.Combine(root, "Asana_Task_Followers.csv"), ["TaskGid", "UserGid", "UserName"],
            tasks.SelectMany(t => Array(t.Data,"followers").Select(u => new[] { S(t.Data,"gid"), S(u,"gid"), S(u,"name") })));

        var taskNames = tasks.ToDictionary(t => S(t.Data,"gid"), t => S(t.Data,"name"), StringComparer.Ordinal);
        Csv.Write(Path.Combine(root, "Asana_Task_Dependencies.csv"), ["TaskGid", "TaskName", "DependsOnTaskGid", "DependsOnTaskName"],
            tasks.SelectMany(t => Array(t.Data,"dependencies").Select(d => new[] { S(t.Data,"gid"), S(t.Data,"name"), S(d,"gid"), TaskName(taskNames, S(d,"gid")) })));

        Csv.Write(Path.Combine(root, "Asana_Task_Dependents.csv"), ["TaskGid", "TaskName", "DependentTaskGid", "DependentTaskName"],
            tasks.SelectMany(t => Array(t.Data,"dependents").Select(d => new[] { S(t.Data,"gid"), S(t.Data,"name"), S(d,"gid"), TaskName(taskNames, S(d,"gid")) })));

        Csv.Write(Path.Combine(root, "Asana_Task_Attachments.csv"), ["AttachmentGid", "TaskGid", "TaskName", "Name", "ResourceSubtype", "Host", "CreatedAt", "SizeBytes", "PermanentUrl", "ViewUrl", "ConnectedToApp"],
            attachments.Select(a => new[] { S(a,"gid"), N(a,"parent","gid"), N(a,"parent","name"), S(a,"name"), S(a,"resource_subtype"), S(a,"host"), S(a,"created_at"), S(a,"size"), S(a,"permanent_url"), S(a,"view_url"), S(a,"connected_to_app") }));

        Csv.Write(Path.Combine(root, "Asana_Portfolios.csv"), ["WorkspaceGid", "PortfolioGid", "Name", "OwnerGid", "OwnerName", "CreatedAt", "ModifiedAt", "PrivacySetting", "CurrentStatusGid", "CurrentStatusTitle", "CurrentStatusSubtype", "PermalinkUrl"],
            portfolios.Select(p => new[] { workspaceGid, S(p,"gid"), S(p,"name"), N(p,"owner","gid"), N(p,"owner","name"), S(p,"created_at"), S(p,"modified_at"), S(p,"privacy_setting"), N(p,"current_status_update","gid"), N(p,"current_status_update","title"), N(p,"current_status_update","resource_subtype"), S(p,"permalink_url") }));

        Csv.Write(Path.Combine(root, "Asana_Portfolio_Items.csv"), ["PortfolioGid", "PortfolioName", "ItemGid", "ItemName", "ItemResourceType", "ItemResourceSubtype"],
            portfolioItems.Select(i => new[] { i.PortfolioGid, i.PortfolioName, S(i.Data,"gid"), S(i.Data,"name"), S(i.Data,"resource_type"), S(i.Data,"resource_subtype") }));

        Csv.Write(Path.Combine(root, "Asana_Portfolio_Memberships.csv"), ["PortfolioMembershipGid", "PortfolioGid", "PortfolioName", "UserGid", "UserName"],
            portfolioMemberships.Select(m => new[] { S(m,"gid"), N(m,"portfolio","gid"), N(m,"portfolio","name"), N(m,"user","gid"), N(m,"user","name") }));

        Csv.Write(Path.Combine(root, "Asana_Portfolio_CustomFields.csv"), ["PortfolioGid", "CustomFieldSettingGid", "CustomFieldGid", "Name", "ResourceSubtype", "IsImportant"],
            portfolios.SelectMany(p => Array(p,"custom_field_settings").Select(s => new[] { S(p,"gid"), S(s,"gid"), N(s,"custom_field","gid"), N(s,"custom_field","name"), N(s,"custom_field","resource_subtype"), S(s,"is_important") })));

        Csv.Write(Path.Combine(root, "Asana_Portfolio_Status_Updates.csv"), ["StatusUpdateGid", "PortfolioGid", "PortfolioName", "Title", "ResourceSubtype", "StatusType", "Text", "AuthorGid", "AuthorName", "CreatedAt", "ModifiedAt", "NumLikes"],
            portfolioStatusUpdates.Select(s => new[] { S(s,"gid"), N(s,"parent","gid"), N(s,"parent","name"), S(s,"title"), S(s,"resource_subtype"), S(s,"status_type"), S(s,"text"), N(s,"author","gid"), N(s,"author","name"), S(s,"created_at"), S(s,"modified_at"), S(s,"num_likes") }));

        Csv.Write(Path.Combine(root, "Asana_Goals.csv"), ["WorkspaceGid", "GoalGid", "Name", "Status", "IsWorkspaceLevel", "StartOn", "DueOn", "OwnerGid", "OwnerName", "TeamGid", "TeamName", "PrivacySetting", "DefaultAccessLevel", "CurrentStatusGid", "CurrentStatusTitle", "CurrentStatusSubtype", "TimePeriodGid", "TimePeriodName", "TimePeriod", "TimePeriodStartOn", "TimePeriodEndOn", "MetricGid", "MetricSubtype", "MetricPrecision", "MetricCurrentValue", "MetricTargetValue", "MetricUnit", "Notes"],
            goals.Select(g => new[] { workspaceGid, S(g,"gid"), S(g,"name"), S(g,"status"), S(g,"is_workspace_level"), S(g,"start_on"), S(g,"due_on"), N(g,"owner","gid"), N(g,"owner","name"), N(g,"team","gid"), N(g,"team","name"), S(g,"privacy_setting"), S(g,"default_access_level"), N(g,"current_status_update","gid"), N(g,"current_status_update","title"), N(g,"current_status_update","resource_subtype"), N(g,"time_period","gid"), N(g,"time_period","display_name"), N(g,"time_period","period"), N(g,"time_period","start_on"), N(g,"time_period","end_on"), N(g,"metric","gid"), N(g,"metric","resource_subtype"), N(g,"metric","precision"), N(g,"metric","current_number_value"), N(g,"metric","target_number_value"), N(g,"metric","unit"), S(g,"notes") }));

        Csv.Write(Path.Combine(root, "Asana_Goal_Followers.csv"), ["GoalGid", "UserGid", "UserName"],
            goals.SelectMany(g => Array(g,"followers").Select(u => new[] { S(g,"gid"), S(u,"gid"), S(u,"name") })));

        Csv.Write(Path.Combine(root, "Asana_Goal_CustomFields.csv"), ["GoalGid", "CustomFieldGid", "Name", "ResourceSubtype", "DisplayValue", "TextValue", "NumberValue", "EnumGid", "EnumValue", "MultiEnumValues", "Date", "DateTime"],
            goals.SelectMany(g => Array(g,"custom_fields").Select(f => new[] { S(g,"gid"), S(f,"gid"), S(f,"name"), S(f,"resource_subtype"), S(f,"display_value"), S(f,"text_value"), S(f,"number_value"), N(f,"enum_value","gid"), N(f,"enum_value","name"), string.Join(" | ", Array(f,"multi_enum_values").Select(v => S(v,"name"))), N(f,"date_value","date"), N(f,"date_value","date_time") })));

        Csv.Write(Path.Combine(root, "Asana_Goal_Relationships.csv"), ["GoalRelationshipGid", "RelationshipSubtype", "SupportedGoalGid", "SupportedGoalName", "SupportingResourceGid", "SupportingResourceName", "SupportingResourceType", "SupportingResourceSubtype", "ContributionWeight"],
            goalRelationships.Select(r => new[] { S(r,"gid"), S(r,"resource_subtype"), N(r,"supported_goal","gid"), N(r,"supported_goal","name"), N(r,"supporting_resource","gid"), N(r,"supporting_resource","name"), N(r,"supporting_resource","resource_type"), N(r,"supporting_resource","resource_subtype"), S(r,"contribution_weight") }));

        Csv.Write(Path.Combine(root, "Asana_Goal_Status_Updates.csv"), ["StatusUpdateGid", "GoalGid", "GoalName", "Title", "ResourceSubtype", "StatusType", "Text", "AuthorGid", "AuthorName", "CreatedAt", "ModifiedAt", "NumLikes"],
            goalStatusUpdates.Select(s => new[] { S(s,"gid"), N(s,"parent","gid"), N(s,"parent","name"), S(s,"title"), S(s,"resource_subtype"), S(s,"status_type"), S(s,"text"), N(s,"author","gid"), N(s,"author","name"), S(s,"created_at"), S(s,"modified_at"), S(s,"num_likes") }));
    }

    private static string S(JsonElement element, string property) => JsonValue.String(element, property) ?? "";
    private static string N(JsonElement element, string parent, string property) => Object(element, parent) is { } nested ? S(nested, property) : "";
    private static JsonElement? Object(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;
    private static IEnumerable<JsonElement> Array(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(x => x.Clone()) : [];
    private static string TaskName(Dictionary<string, string> taskNames, string gid) => taskNames.TryGetValue(gid, out var name) ? name : "";
}
