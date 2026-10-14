using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class UsappMarketNotificationTests
{
    static readonly CategoryLookup[] Categories = [new("Turnkey", 28, ["Turnkey"])];
    static OrderItemInfo Item(string code, decimal? charge, string description = "") =>
        new(2, code, description, "") { UnitCharge = charge };
    static ExhibitorsModel Exhibitor(string? text) => new() { ExhibitorUserFields = new() { UserText14 = text } };
    static int Run(RetryBoundaryTests.Scenario s, bool preview = false)
    {
        var runner = new Runner(s.Options with { Apply = !preview }); runner.UseJournalStore(s.Store);
        return runner.ProcessCandidates(s.Gateway, [new(s.Transport.Order, s.Transport.Exhibitor)], [], Categories);
    }

    [Theory]
    [InlineData("YES")]
    [InlineData("Yes")]
    [InlineData(" yes ")]
    public void ExistingYesSkipsMarketCheck(string current)
    {
        Assert.Null(UsappMarketRules.Correction(Exhibitor(current), null));
    }

    [Theory]
    [InlineData(550)]
    [InlineData(350)]
    public void ApprovedUnitChargesRequireManualYes(int price)
    {
        var correction = UsappMarketRules.Correction(Exhibitor("MISSING"), [Item("USAPP MARKET", price)])!;
        Assert.Contains("set exhibitor TEXT_14 to YES", correction.Reason);
        Assert.Contains("current TEXT_14='MISSING'", correction.Values);
        Assert.Contains($"unit charge={price}", correction.Values);
    }

    [Theory]
    [InlineData("OTHER", 550)]
    [InlineData("USAPP MARKET EXTRA", 550)]
    [InlineData("USAPP MARKET", 0)]
    [InlineData("USAPP MARKET", 349)]
    [InlineData("USAPP MARKET", 351)]
    [InlineData("USAPP MARKET", 549)]
    [InlineData("USAPP MARKET", 551)]
    public void OtherCodesAndChargesRequireManualNo(string code, int price)
    {
        var correction = UsappMarketRules.Correction(Exhibitor("MISSING"), [Item(code, price, "USAPP MARKET")])!;
        Assert.Contains("set exhibitor TEXT_14 to NO", correction.Reason);
    }

    [Fact]
    public void EmptyItemsRequireNoAndCurrentNoNeedsNoAlert()
    {
        Assert.Contains("set exhibitor TEXT_14 to NO", UsappMarketRules.Correction(Exhibitor(""), [])!.Reason);
        Assert.Null(UsappMarketRules.Correction(Exhibitor("NO"), []));
        Assert.Contains("set exhibitor TEXT_14 to YES", UsappMarketRules.Correction(Exhibitor("NO"), [Item(" usapp market ", 350m)])!.Reason);
        Assert.Contains("set exhibitor TEXT_14 to NO", UsappMarketRules.Correction(Exhibitor(""), [Item("USAPP MARKET", 549.999m)])!.Reason);
    }

    [Fact]
    public void MissingInputDoesNotEstablishNo()
    {
        var unknown = UsappMarketRules.Correction(Exhibitor("MISSING"), null)!;
        Assert.Contains("check is unavailable", unknown.Reason);
        Assert.DoesNotContain("set exhibitor TEXT_14 to NO", unknown.Reason);
        Assert.Contains("TEXT_14='UNAVAILABLE'", UsappMarketRules.Correction(new(), [])!.Values);
        Assert.DoesNotContain("set exhibitor TEXT_14 to NO", UsappMarketRules.Correction(Exhibitor("MISSING"), [Item("USAPP MARKET", null)])!.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SendsOneKyleAlertAndNormalFinanceEmailWithoutBlockingOrChangingUdf(bool marketItem)
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Exhibitor.ExhibitorUserFields.UserText14 = "MISSING";
        if (marketItem) s.Transport.Items.Add(new() { OrderLineNumber = 2, ResourceCode = "USAPP MARKET", UnitCharge = 550m });
        Assert.Equal(0, Run(s));
        Assert.Equal("A", s.Transport.Order.OrderStatus); Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus);
        Assert.Equal("MISSING", s.Transport.Exhibitor.ExhibitorUserFields.UserText14);
        var evidence = Assert.Single(s.Store.Load()); Assert.True(evidence.Complete); Assert.Empty(evidence.Plan.Issues);
        Assert.Single(evidence.Plan.ManualCorrections);
        Assert.Single(s.Transport.Emails, x => x.EmailSubject.StartsWith("Ready for invoicing"));
        var alert = Assert.Single(s.Transport.Emails, x => x.EmailSubject.StartsWith("Service Order Entry — attention needed"));
        Assert.Equal("kylep@kallman.com", Assert.Single(alert.SendToAddresses).EmailAddress);
        Assert.Empty(alert.CCAddresses);
        Assert.Contains($"set exhibitor TEXT_14 to {(marketItem ? "YES" : "NO")}", alert.HtmlText);
        Assert.Contains("Order 3", alert.HtmlText); Assert.Contains("event 1, exhibitor 2", alert.HtmlText);
        Assert.Contains("Result: SUCCESS", alert.HtmlText);
        Assert.DoesNotContain("Update USAPP", string.Join("|", s.Transport.Mutations));
        var csv = File.ReadAllText(Assert.Single(Directory.GetFiles(s.Options.RunFolder, "*.csv")));
        Assert.Contains("Manual Corrections (Order Continues)", csv); Assert.Contains("TEXT_14", csv);
    }

    [Fact]
    public void PreviewRetainsAlertButDoesNotWriteOrEmail()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Exhibitor.ExhibitorUserFields.UserText14 = "MISSING";
        Assert.Equal(0, Run(s, preview: true)); Assert.Empty(s.Transport.Mutations); Assert.Empty(s.Transport.Emails);
        var html = File.ReadAllText(Assert.Single(Directory.GetFiles(s.Options.RunFolder, "*.exceptions.html")));
        Assert.Contains("set exhibitor TEXT_14 to NO", html); Assert.Contains("order processing continues", html);
    }

    [Fact]
    public void CurrentNoWithoutMarketNeedsOnlyNormalCompletionEmail()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Exhibitor.ExhibitorUserFields.UserText14 = "NO";
        Assert.Equal(0, Run(s)); Assert.Single(s.Transport.Emails);
        Assert.Empty(Assert.Single(s.Store.Load()).Plan.ManualCorrections);
    }

    [Fact]
    public void IncompleteItemSearchNeverEmailsAFalseNoRecommendation()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Exhibitor.ExhibitorUserFields.UserText14 = "MISSING";
        s.Transport.FailReadPath = "ServiceOrderItems/10";
        Assert.Equal(1, Run(s));
        var alert = Assert.Single(s.Transport.Emails);
        Assert.Contains("check is unavailable", alert.HtmlText);
        Assert.DoesNotContain("set exhibitor TEXT_14 to NO", alert.HtmlText);
    }

    [Fact]
    public void SummaryEscapesCurrentValuesAndIncludesWarningsForSuccessfulOrders()
    {
        var row = new RunRow { Outcome = "SUCCESS", ValidationStatus = "READY" };
        row.ManualCorrections.Add(UsappMarketRules.Correction(Exhibitor("<b>MISSING</b>"), [])!);
        Assert.True(ExceptionSummary.NeedsAttention(row));
        var html = ExceptionSummary.Build([row], "run", "report");
        Assert.Contains("&lt;b&gt;MISSING&lt;/b&gt;", html); Assert.DoesNotContain("<b>MISSING</b>", html);
    }

    [Fact]
    public void MultipleOrdersShareOneKyleSummaryAndReadyEmailOptOutDoesNotDisableIt()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Exhibitor.ExhibitorUserFields.UserText14 = "MISSING";
        var second = Newtonsoft.Json.JsonConvert.DeserializeObject<ServiceOrdersModel>(Newtonsoft.Json.JsonConvert.SerializeObject(s.Transport.Order))!;
        second.OrderNumber = 4; s.Transport.OtherOrders.Add(second);
        var runner = new Runner(s.Options with { MaxUpdates = 2, SendReadyEmail = false, ActivateExhibitor = false });
        runner.UseJournalStore(s.Store);
        Assert.Equal(0, runner.ProcessCandidates(s.Gateway, [new(s.Transport.Order, s.Transport.Exhibitor), new(second, s.Transport.Exhibitor)], [], Categories));
        var email = Assert.Single(s.Transport.Emails);
        Assert.Contains("Order 3", email.HtmlText); Assert.Contains("Order 4", email.HtmlText);
        Assert.Equal("kylep@kallman.com", Assert.Single(email.SendToAddresses).EmailAddress);
        Assert.All(s.Store.Load(), x => Assert.True(x.Complete));
    }
}
