using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ActivitiesPull.Tests;

public sealed class ActivitiesPullTests
{
    [Fact]
    public void Cli_defaults_to_safe_full_settings()
    {
        var options = CliOptions.Parse(["full"]);

        Assert.Equal(RunMode.Full, options.Mode);
        Assert.Equal(2000, options.MaxRowsPerWindow);
        Assert.Equal(100_000, options.DenseWindowMaxRows);
        Assert.Equal(250, options.PageSize);
        Assert.Equal(48, options.OverlapHours);
    }

    [Fact]
    public void Cli_rejects_reverse_range()
    {
        Assert.Throws<CliException>(() => CliOptions.Parse(
            ["probe", "--start", "2026-07-22", "--end", "2026-07-21"]));
    }

    [Fact]
    public void Activity_key_uses_the_supported_get_identity()
    {
        var row = new string[ActivitySchema.Columns.Length];
        Array.Fill(row, "");
        row[ActivitySchema.IndexOf("OrganizationCode")] = "10";
        row[ActivitySchema.IndexOf("Account")] = "ABC";
        row[ActivitySchema.IndexOf("SequenceNumber")] = "42";

        Assert.Equal("10|ABC|42", ActivitySchema.Key(row));
    }

    [Fact]
    public void Adaptive_puller_splits_large_windows_and_preserves_all_rows()
    {
        var source = new FakeSource();
        var options = CliOptions.Parse(["full", "--max-rows", "2", "--request-delay-ms", "0"]);
        var puller = new AdaptiveActivityPuller(source, options);
        var accepted = new List<int>();

        puller.Pull(ActivityDateField.EnteredOn, new DateTime(2026, 1, 1), new DateTime(2026, 1, 5),
            (_, _, rows) => accepted.Add(rows.Count));

        Assert.Equal(4, accepted.Sum());
        Assert.All(accepted, count => Assert.InRange(count, 0, 2));
        Assert.True(source.Calls > 2);
    }

    private sealed class FakeSource : IActivitySource
    {
        public int Calls { get; private set; }

        public ActivitySearchResult Search(ActivityDateField field, DateTime start, DateTime end, int maxResults)
        {
            Calls++;
            var total = (int)Math.Round((end - start).TotalDays, MidpointRounding.AwayFromZero);
            if (total > maxResults)
                throw new WindowTooLargeException(start, end, maxResults);
            var rows = Enumerable.Range(0, total).Select(i => new ActivitiesModel
            {
                OrganizationCode = "10",
                Account = "A",
                SequenceNumber = i + 1
            }).ToList();
            return new ActivitySearchResult(rows, total);
        }
    }
}
