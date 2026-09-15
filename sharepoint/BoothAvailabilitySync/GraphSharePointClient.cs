using Azure.Core;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BoothAvailabilitySync;

public sealed class GraphSharePointClient : IDisposable
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];
    private readonly TokenCredential _credential;
    private readonly HttpClient _http;
    private readonly AppLogger _log;

    public GraphSharePointClient(TokenCredential credential, AppLogger log)
    {
        _credential = credential;
        _log = log;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
        };
    }

    public async Task<string> ResolveSiteIdAsync(string hostname, string sitePath, CancellationToken cancellationToken)
    {
        var relativePath = sitePath.TrimStart('/');
        var uri = $"sites/{hostname}:/{relativePath}?$select=id,displayName,webUrl";
        using var doc = await GetJsonAsync(uri, cancellationToken);
        return doc.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("Microsoft Graph returned a site without an ID.");
    }

    public async Task<SharePointListInfo> ResolveListAsync(string siteId, string listName, CancellationToken cancellationToken)
    {
        var uri = $"sites/{Uri.EscapeDataString(siteId)}/lists/{Uri.EscapeDataString(listName)}?$select=id,displayName";
        using var doc = await GetJsonAsync(uri, cancellationToken);
        var root = doc.RootElement;
        return new SharePointListInfo(
            root.GetProperty("id").GetString() ?? throw new InvalidOperationException("List ID missing."),
            root.TryGetProperty("displayName", out var displayName) ? displayName.GetString() ?? listName : listName);
    }

    public async Task<IReadOnlyList<SharePointColumn>> GetColumnsAsync(
        string siteId,
        string listId,
        CancellationToken cancellationToken)
    {
        var uri = $"sites/{Uri.EscapeDataString(siteId)}/lists/{Uri.EscapeDataString(listId)}/columns?$select=id,name,displayName,readOnly&$top=999";
        var elements = await GetAllValuesAsync(uri, cancellationToken);
        var columns = new List<SharePointColumn>();

        foreach (var element in elements)
        {
            columns.Add(new SharePointColumn(
                element.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                element.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                element.TryGetProperty("displayName", out var display) ? display.GetString() ?? "" : "",
                element.TryGetProperty("readOnly", out var readOnly) && readOnly.ValueKind == JsonValueKind.True));
        }

        return columns;
    }

    public async Task<IReadOnlyList<SharePointItem>> GetItemsAsync(
        string siteId,
        string listId,
        CancellationToken cancellationToken)
    {
        var uri = $"sites/{Uri.EscapeDataString(siteId)}/lists/{Uri.EscapeDataString(listId)}/items?$expand=fields&$top=999";
        var elements = await GetAllValuesAsync(uri, cancellationToken);
        var items = new List<SharePointItem>();

        foreach (var element in elements)
        {
            var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (element.TryGetProperty("fields", out var fieldsElement) && fieldsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in fieldsElement.EnumerateObject())
                    fields[property.Name] = property.Value.Clone();
            }

            items.Add(new SharePointItem
            {
                Id = element.GetProperty("id").GetString() ?? "",
                Fields = fields
            });
        }

        return items;
    }

    public async Task UpdateItemFieldsAsync(
        string siteId,
        string listId,
        string itemId,
        IReadOnlyDictionary<string, object?> fields,
        CancellationToken cancellationToken)
    {
        var uri = $"sites/{Uri.EscapeDataString(siteId)}/lists/{Uri.EscapeDataString(listId)}/items/{Uri.EscapeDataString(itemId)}/fields";
        var json = JsonSerializer.Serialize(fields);
        using var request = new HttpRequestMessage(HttpMethod.Patch, uri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await SendWithRetryAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"SharePoint update failed for item {itemId}: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }
    }

    public static ResolvedColumns ResolveRequiredColumns(
        IReadOnlyList<SharePointColumn> columns,
        SharePointSettings settings)
    {
        string Resolve(string displayName)
        {
            var matches = columns
                .Where(c => c.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
                throw new InvalidOperationException($"SharePoint column '{displayName}' was not found.");
            if (matches.Count > 1)
                throw new InvalidOperationException($"More than one SharePoint column has display name '{displayName}'.");
            if (matches[0].ReadOnly)
                throw new InvalidOperationException($"SharePoint column '{displayName}' is read-only.");

            return matches[0].InternalName;
        }

        var eventIdColumn = columns
            .SingleOrDefault(c => c.DisplayName.Equals(settings.EventIdColumn, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"SharePoint column '{settings.EventIdColumn}' was not found.");

        return new ResolvedColumns(
            eventIdColumn.InternalName,
            Resolve(settings.AvailableBoothsColumn),
            Resolve(settings.AvailableAreaColumn),
            Resolve(settings.SoldBoothsColumn),
            Resolve(settings.AreaSoldColumn),
            Resolve(settings.BoothsOnHoldColumn),
            Resolve(settings.LastUpdatedColumn));
    }

    public static string? ReadEventId(SharePointItem item, string internalName)
    {
        if (!item.Fields.TryGetValue(internalName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.String)
            return NormalizeEventId(value.GetString());

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var integer))
                return integer.ToString(CultureInfo.InvariantCulture);

            if (value.TryGetDecimal(out var number))
                return NormalizeEventId(number.ToString(CultureInfo.InvariantCulture));
        }

        return NormalizeEventId(value.ToString());
    }

    public static decimal? ReadDecimal(SharePointItem item, string internalName)
    {
        if (!item.Fields.TryGetValue(internalName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? NormalizeEventId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) &&
            number == decimal.Truncate(number))
        {
            return decimal.Truncate(number).ToString(CultureInfo.InvariantCulture);
        }

        return trimmed;
    }

    private async Task<JsonDocument> GetJsonAsync(string uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendWithRetryAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Graph GET failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");

        return JsonDocument.Parse(body);
    }

    private async Task<List<JsonElement>> GetAllValuesAsync(string initialUri, CancellationToken cancellationToken)
    {
        var results = new List<JsonElement>();
        string? next = initialUri;

        while (!string.IsNullOrWhiteSpace(next))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            using var response = await SendWithRetryAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Graph GET failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("value", out var valueArray))
            {
                foreach (var value in valueArray.EnumerateArray())
                    results.Add(value.Clone());
            }

            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var nextLink)
                ? nextLink.GetString()
                : null;
        }

        return results;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage originalRequest,
        CancellationToken cancellationToken)
    {
        var method = originalRequest.Method;
        var requestUri = originalRequest.RequestUri;
        var contentText = originalRequest.Content is null
            ? null
            : await originalRequest.Content.ReadAsStringAsync(cancellationToken);
        var contentType = originalRequest.Content?.Headers.ContentType?.MediaType ?? "application/json";

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var request = new HttpRequestMessage(method, requestUri);
            if (contentText is not null)
                request.Content = new StringContent(contentText, Encoding.UTF8, contentType);

            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(GraphScopes),
                cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return response;

            if (!ShouldRetry(response.StatusCode) || attempt == 3)
                return response;

            var delay = GetRetryDelay(response, attempt);
            _log.Warn($"Graph returned {(int)response.StatusCode}. Retrying in {delay.TotalSeconds:N0} second(s) (attempt {attempt}/3).");
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected retry loop exit.");
    }

    private static bool ShouldRetry(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests ||
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode >= 500;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var calculated = date - DateTimeOffset.UtcNow;
            if (calculated > TimeSpan.Zero)
                return calculated;
        }

        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
    }

    public void Dispose() => _http.Dispose();
}
