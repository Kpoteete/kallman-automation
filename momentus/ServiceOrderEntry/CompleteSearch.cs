using Newtonsoft.Json.Linq;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Models.Search;

namespace ServiceOrderEntry;

internal enum SearchFailureKind
{
    RequestFailed, IncompletePagination, PaginationLoop, MalformedResponse, ResultCeiling, ConflictingDuplicateId, IdentityUnavailable
}

internal sealed class DecisionSearchException(SearchFailureKind kind, string search, int page, string detail, Exception? cause = null)
    : Exception($"Decision unavailable: {kind}; search={search}; page={page}. {detail}", cause)
{
    public SearchFailureKind Kind { get; } = kind;
    public string Search { get; } = search;
    public int Page { get; } = page;
}

// Nothing escapes this collector until the complete cached search has been validated.
internal static class CompleteSearch
{
    internal static IReadOnlyList<T> Read<T>(Func<Search, SearchResponse<T>> firstPage,
        Func<string, Search, SearchResponse<T>> nextPage, Search search, string description,
        Uri endpoint, Func<T, string> identity) where T : UngerboeckModel
    {
        var rows = new Dictionary<string, (T Row, JToken Snapshot)>(StringComparer.OrdinalIgnoreCase);
        var links = new HashSet<string>(StringComparer.Ordinal);
        var page = 1;
        var received = 0;
        int? expected = null;
        int? pageTotal = null;
        int? pageSize = null;
        string? next = null;
        DecisionSearchException Fail(SearchFailureKind kind, string detail, Exception? cause = null) =>
            new(kind, description, page, detail, cause);
        while (true)
        {
            SearchResponse<T> response;
            try { response = next is null ? firstPage(search) : nextPage(next, search); }
            catch (Exception ex) when (ex is not DecisionSearchException and not JournalStorageException)
            {
                throw Fail(page == 1 ? SearchFailureKind.RequestFailed : SearchFailureKind.IncompletePagination,
                    $"A required page could not be retrieved; no partial results are available. {ex.Message}", ex);
            }
            if (response?.Results is null || response.SearchMetadata is not { } metadata)
                throw Fail(SearchFailureKind.MalformedResponse, "Missing results or search metadata.");
            var batch = response.Results.ToList();
            if (metadata.Page != page || metadata.Page_Size <= 0 || metadata.ResultsTotal < 0 ||
                metadata.PageTotal < 0 || metadata.PageTotal == 0 && metadata.ResultsTotal != 0 ||
                metadata.PageTotal > 0 && metadata.PageTotal < page || batch.Count > metadata.Page_Size)
                throw Fail(SearchFailureKind.MalformedResponse, "Invalid page number, size, total, or page contents.");
            expected ??= metadata.ResultsTotal;
            pageTotal ??= metadata.PageTotal;
            pageSize ??= metadata.Page_Size;
            if (metadata.ResultsTotal != expected || metadata.PageTotal != pageTotal || metadata.Page_Size != pageSize)
                throw Fail(SearchFailureKind.IncompletePagination, "Search totals or page size changed during pagination.");
            if (expected > search.MaxResults || pageTotal > Math.Max(1, search.MaxResults))
                throw Fail(SearchFailureKind.ResultCeiling, $"Reported results exceed the configured ceiling {search.MaxResults}.");
            if (pageTotal != Math.Max(1, (int)Math.Ceiling((double)expected.Value / pageSize.Value)) &&
                !(expected == 0 && pageTotal == 0))
                throw Fail(SearchFailureKind.MalformedResponse, "Page total disagrees with result total and page size.");
            received += batch.Count;
            if (received > search.MaxResults)
                throw Fail(SearchFailureKind.ResultCeiling, $"Returned results exceed the configured ceiling {search.MaxResults}.");
            foreach (var row in batch)
            {
                if (row is null) throw Fail(SearchFailureKind.MalformedResponse, "Null result row.");
                var key = identity(row);
                if (string.IsNullOrWhiteSpace(key))
                    throw Fail(SearchFailureKind.MalformedResponse, "A result has no usable stable identity.");
                var snapshot = JToken.FromObject(row);
                if (rows.TryGetValue(key, out var prior))
                {
                    if (!JToken.DeepEquals(prior.Snapshot, snapshot))
                        throw Fail(SearchFailureKind.ConflictingDuplicateId, $"Conflicting versions of stable ID {key}.");
                }
                else rows.Add(key, (row, snapshot));
            }
            var continuation = metadata.Links?.Next;
            if (continuation is not null && continuation.Length > 0 && string.IsNullOrWhiteSpace(continuation))
                throw Fail(SearchFailureKind.MalformedResponse, "Whitespace continuation link.");
            if (string.IsNullOrEmpty(continuation))
            {
                if (received != expected || page != Math.Max(1, pageTotal.Value))
                    throw Fail(SearchFailureKind.IncompletePagination, $"Premature end: expected {expected} rows/{pageTotal} pages; received {received} rows/{page} pages.");
                return rows.Values.Select(x => x.Row).ToList();
            }
            if (!Uri.TryCreate(endpoint, continuation, out var uri) ||
                uri.Scheme != endpoint.Scheme || uri.Authority != endpoint.Authority ||
                uri.AbsolutePath != endpoint.AbsolutePath || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0 ||
                uri.Query.Length == 0)
                throw Fail(SearchFailureKind.MalformedResponse, "Continuation is not a valid link to this search endpoint.");
            if (!links.Add(uri.AbsoluteUri))
                throw Fail(SearchFailureKind.PaginationLoop, "A continuation link repeated.");
            if (page >= pageTotal || received >= expected || batch.Count != pageSize)
                throw Fail(SearchFailureKind.IncompletePagination, "Continuation disagrees with reported totals or a non-final page is short.");
            next = uri.AbsoluteUri;
            page++;
        }
    }
}
