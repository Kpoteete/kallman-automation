using System.Globalization;
using System.Reflection;
using Xunit;

namespace GLDataMillPull.Tests;

public sealed class GLDataMillPullTests
{
    [Fact]
    public void CatalogContainsAllNineGlSectionsShownInApiHelp()
    {
        string[] expected =
        [
            "GLAccountAnalysisCodes",
            "GLAccounts",
            "GLDeferralRevenueDetails",
            "GLDeferralRevenueHeaders",
            "GLDistributions",
            "GLMainAccounts",
            "GLSources",
            "GLSpaceMajor",
            "GLSpaceMinor"
        ];

        foreach (string name in expected)
            Assert.Contains(DatasetCatalog.All, dataset => dataset.Name == name);
    }

    [Fact]
    public void CatalogIncludesProfitAndMarginFacts()
    {
        Assert.Contains(DatasetCatalog.All, dataset => dataset.Name == "JournalEntryDetails");
        Assert.Contains(DatasetCatalog.All, dataset => dataset.Name == "DailyRevenueAndCostAnalysis");
        Assert.Contains(DatasetCatalog.All, dataset => dataset.Name == "FiscalPeriods");
    }

    [Fact]
    public void DatasetSelectionIsCaseInsensitiveAndRejectsUnknownNames()
    {
        IReadOnlyList<DatasetSpec> selected = DatasetCatalog.Select(["glaccounts"]);

        Assert.Single(selected);
        Assert.Equal("GLAccounts", selected[0].Name);
        Assert.Throws<CliException>(() => DatasetCatalog.Select(["not-real"]));
    }

    [Fact]
    public void CsvFormattingIsInvariantAndPreservesNestedData()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1234.56", CsvDatasetWriter.FormatValue(1234.56m));
            Assert.Equal("true", CsvDatasetWriter.FormatValue(true));
            Assert.Equal("", CsvDatasetWriter.FormatValue(null));
            Assert.Contains("\"Code\":\"A\"", CsvDatasetWriter.FormatValue(new Nested("A")));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ExportPropertiesExcludeIndexers()
    {
        PropertyInfo[] properties = CsvDatasetWriter.ExportProperties(typeof(HasIndexer));

        Assert.Contains(properties, property => property.Name == nameof(HasIndexer.Value));
        Assert.DoesNotContain(properties, property => property.GetIndexParameters().Length > 0);
    }

    [Fact]
    public void IncrementalUpsertReplacesExistingAndAppendsNewRows()
    {
        string folder = Path.Combine(Path.GetTempPath(), "GLDataMillPullTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string existing = Path.Combine(folder, "existing.csv");
        string output = Path.Combine(folder, "output.csv");
        File.WriteAllText(
            existing,
            "Organization,GLAccount,SubAccount,Description,_ExtractedAtUtc,_SourceEndpoint\r\n" +
            "10,4000,,Old,2026-01-01T00:00:00Z,GLAccounts\r\n",
            new System.Text.UTF8Encoding(true));
        var spec = new DatasetSpec(
            "TestAccounts",
            "GLAccounts",
            "output.csv",
            true,
            ["Organization", "GLAccount", "SubAccount"],
            "ChangedOn",
            "test");
        object[] changes =
        [
            new TestAccount("10", "4000", "", "Updated"),
            new TestAccount("10", "5000", "01", "New")
        ];

        DatasetResult result = CsvIncrementalWriter.Upsert(
            spec,
            existing,
            output,
            changes,
            DateTimeOffset.Parse("2026-07-29T10:00:00Z", CultureInfo.InvariantCulture));

        string text = File.ReadAllText(output);
        Assert.Equal(2, result.Rows);
        Assert.Contains("10,4000,,Updated", text);
        Assert.Contains("10,5000,01,New", text);
        Assert.DoesNotContain(",Old,", text);
    }

    private sealed record Nested(string Code);
    private sealed record TestAccount(
        string Organization,
        string GLAccount,
        string SubAccount,
        string Description);

    private sealed class HasIndexer
    {
        public string Value { get; init; } = "";
        public string this[int index] => index.ToString(CultureInfo.InvariantCulture);
    }
}
