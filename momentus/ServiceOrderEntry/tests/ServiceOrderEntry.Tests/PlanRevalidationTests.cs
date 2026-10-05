using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class PlanRevalidationTests
{
    static void Change(RetryBoundaryTests.Scenario s, string input)
    {
        switch (input)
        {
            case "hold": s.Transport.Exhibitor.ExhibitorCategory = "103"; break;
            case "approval": s.Transport.Exhibitor.ExhibitorCategory = "102"; break;
            case "selector": s.Transport.BillingInstructions = s.Row.RequestedBilling with { UseRequestedAddress = "ECA" }; break;
            case "billing": s.Transport.Order.BillToAccount = "OTHER"; break;
            case "items": s.Transport.Items[0].Description = "Space Only Package"; break;
            case "units": s.Transport.Items[0].Units = 7; break;
            case "booth": s.Transport.Activities[0].PlainText = "Accepted booth 102, and had these comments: changed"; break;
            case "activity": s.Transport.Activities[0].EnteredOn = new DateTime(2026, 10, 5); break;
            case "contract": s.Transport.DocumentData["C/11"] = RetryBoundaryTests.MakePdf(RetryBoundaryTests.TestSchedule + "\nRevision"); break;
            case "schedule": s.Transport.DocumentData["C/11"] = RetryBoundaryTests.MakePdf(RetryBoundaryTests.TestSchedule.Replace("50%", "100%")); break;
            case "rep": s.Transport.Exhibitor.Salesperson = "OTHER"; break;
            case "order category": s.Transport.Order.Category = 27; break;
            case "order status": s.Transport.Order.OrderStatus = "A"; break;
            case "exhibitor status": s.Transport.Exhibitor.ExhibitorStatus = 2; break;
            case "managed categories": s.Transport.Exhibitor.ExhibitorCategory = "2"; break;
            case "currency": s.Transport.Order.Currency = "EUR"; break;
            case "invalid category": s.Transport.Exhibitor.ExhibitorCategory = "unknown"; break;
            default: throw new Exception(input);
        }
    }
    public static IEnumerable<object[]> MaterialInputs() => new[] { "hold", "approval", "selector", "billing", "items", "units", "booth", "activity",
        "contract", "schedule", "rep", "order category", "order status", "exhibitor status", "managed categories", "currency", "invalid category" }.Select(x => new object[] { x });
    [Theory] [MemberData(nameof(MaterialInputs))] public void ChangedDecisionInputBeforeApplyBlocksEveryMutation(string input)
    {
        using var s = new RetryBoundaryTests.Scenario(); Change(s, input); s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "RECOVERY REVIEW", s.Row.UpdateMessage);
        Assert.Empty(s.Transport.Mutations); Assert.Equal("REVIEW", s.Row.ValidationStatus);
    }
    [Theory] [InlineData("hold")] [InlineData("approval")] [InlineData("selector")] [InlineData("items")]
    [InlineData("contract")] [InlineData("schedule")] [InlineData("booth")] [InlineData("billing")]
    public void MaterialChangesBetweenStagesStopAtNextDispatchBoundary(string input)
    {
        using var s = new RetryBoundaryTests.Scenario(); var changed = false;
        s.Transport.BeforeRequest = _ => { if (!changed && s.Gateway.Journal?.Evidence.Stages.Any(x => x.Operation == "Update service order" && x.Status == StageStatus.Verified) == true) { changed = true; Change(s, input); } };
        s.Apply(); Assert.True(changed); Assert.Single(s.Transport.Mutations); Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus);
        Assert.DoesNotContain("Send ready email", s.Transport.Mutations);
    }
    [Fact] public void FreshUnmanagedCategoriesAreRetained()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Exhibitor.ExhibitorCategory = "76,77";
        s.Apply(); Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.Equal("1,76,77", s.Transport.Exhibitor.ExhibitorCategory);
    }
    [Fact] public void UnmanagedCategoryAddedBetweenStagesSurvivesCategoryWriteAndActivation()
    {
        using var s = new RetryBoundaryTests.Scenario(); var added = false;
        s.Transport.BeforeRequest = _ => { if (!added && s.Transport.Mutations.Contains("Add payment schedule note")) { added = true; s.Transport.Exhibitor.ExhibitorCategory = "77"; } };
        s.Apply(); Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage); Assert.Equal("1,77", s.Transport.Exhibitor.ExhibitorCategory);
        Assert.Equal("1,77", s.Store.Load().Single().Plan.FinalExhibitorCategories);
    }
    [Fact] public void NewUnmanagedCategoryAfterVerifiedCategoryStageIsNotLostDuringActivation()
    {
        using var s = new RetryBoundaryTests.Scenario(); var added = false;
        s.Transport.BeforeRequest = _ => { if (!added && s.Gateway.Journal?.Evidence.Stages.Any(x => x.Operation == "Update exhibitor categories" && x.Status == StageStatus.Verified) == true) { added = true; s.Transport.Exhibitor.ExhibitorCategory = "1,77"; } };
        s.Apply(); Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.Equal("1,77", s.Transport.Exhibitor.ExhibitorCategory); // Never replace with stale category set, even when readback detects a concurrent edit.
    }
    [Fact] public void RecoveryRevalidatesSavedPlanBeforeFurtherMutation()
    {
        using var s = new RetryBoundaryTests.Scenario("Copy contract document"); s.Apply(); Change(s, "items");
        var before = s.Transport.Mutations.Count; var row = s.Apply(restart: true);
        Assert.Equal("RECOVERY REVIEW", row.ServiceOrderUpdateStatus); Assert.Equal(before, s.Transport.Mutations.Count);
        Assert.Equal(StageStatus.Verified, s.Store.Load().Single().Stages.Single(x => x.Operation == "Copy contract document").Status);
    }
    [Fact] public void MissingRevalidationEvidenceRequiresReview()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Row.DecisionInputs = null; s.Apply();
        Assert.Equal("RECOVERY REVIEW", s.Row.ServiceOrderUpdateStatus); Assert.Empty(s.Transport.Mutations);
    }
    [Theory] [InlineData("Cosmetic Package", null)] [InlineData("SME Package", 1)] [InlineData("SME-Package", 1)]
    [InlineData("SMEPlus Package", null)] [InlineData("Global SME Package", 1)]
    public void CategoryIdentifiersUseTokenBoundaries(string description, int? expected) => Assert.Equal(expected,
        CategoryRules.Resolve([new(1, "X", description, "")], [new("SME", 1, ["SME"])]).Sequence);
    [Fact] public void CosmeticPackageDoesNotMatchActualWorkbookSmeCategory()
    {
        var lookups = LookupLoader.LoadCategories(CliOptions.Parse(["preview"]).CategoryLookupPath);
        Assert.Null(CategoryRules.Resolve([new(1, "X", "Cosmetic Package", "")], lookups).Sequence);
    }
    [Fact] public void ExactConfiguredResourceCodeIdentifiesCategoryWithoutFreeTextPackage() => Assert.Equal(1,
        CategoryRules.Resolve([new(1, "SME", "Service", "")], [new("SME", 1, ["SME"])]).Sequence);
    [Theory] [InlineData("no sponsorship", false)] [InlineData("Turnkey package - no sponsorship", false)]
    [InlineData("Sponsorship not included", false)] [InlineData("non-sponsor package", false)]
    [InlineData("sponsor package", true)] [InlineData("Gold Sponsorship", true)] [InlineData("Sponsoring event", true)]
    [InlineData("sponsorship excluded", false)] [InlineData("without sponsorship", false)] [InlineData("sponsorless package", false)]
    public void SponsorRequiresAffirmativeEvidence(string description, bool expected) => Assert.Equal(expected,
        ExhibitorCategoryRules.Resolve("", "Turnkey", [new(1, "TK", description, "")], "N").Final.Contains(5));
}
