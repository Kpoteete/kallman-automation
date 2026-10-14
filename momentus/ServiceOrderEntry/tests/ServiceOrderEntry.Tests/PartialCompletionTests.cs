using Newtonsoft.Json;
using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class PartialCompletionTests
{
    static readonly CategoryLookup[] Categories = [new("Turnkey", 28, ["Turnkey"])];
    static RunRow Evaluate(RetryBoundaryTests.Scenario s) => Runner.Evaluate(s.Gateway, new(s.Transport.Order, s.Transport.Exhibitor), [], Categories);
    static void BadBilling(RetryBoundaryTests.Scenario s) => s.Transport.BillingInstructions = s.Transport.BillingInstructions with { City = "" };
    static int Run(RetryBoundaryTests.Scenario s, bool preview = false, int limit = 1)
    {
        var runner = new Runner(s.Options with { Apply = !preview, MaxUpdates = limit });
        runner.UseJournalStore(s.Store);
        return runner.ProcessCandidates(s.Gateway, [new(s.Transport.Order, s.Transport.Exhibitor)], [], Categories);
    }

    [Fact]
    public void BillingIssueSavesIndependentWorkAndEmailsKyleWhileBothStatusesStayPending()
    {
        using var s = new RetryBoundaryTests.Scenario(); BadBilling(s);
        Assert.Equal(2, Run(s));
        Assert.Equal("REP", s.Transport.Order.OrderAccountRep); Assert.Equal(28, s.Transport.Order.Category);
        Assert.Equal("101", s.Transport.Order.BoothNumber); Assert.Equal("ACCOUNT", s.Transport.Order.BillToAccount);
        Assert.Equal("CONTACT", s.Transport.Order.BillToContact); Assert.Equal("PC", s.Transport.Order.OrderStatus);
        Assert.Equal(35, s.Transport.Exhibitor.ExhibitorStatus); Assert.Single(s.Transport.Copies); Assert.Single(s.Transport.Notes);
        Assert.DoesNotContain("Create organization account", s.Transport.Mutations);
        Assert.DoesNotContain("Update billing contact", s.Transport.Mutations);
        Assert.DoesNotContain("Activate order", s.Transport.Mutations); Assert.DoesNotContain("Activate exhibitor", s.Transport.Mutations);
        var plan = Assert.Single(s.Store.Load()); Assert.False(plan.Complete); Assert.False(plan.OrderComplete);
        Assert.Equal("PARTIAL (REMAINS PC)", plan.Plan.ServiceOrderUpdateStatus);
        Assert.Equal("WITHHELD (INCOMPLETE ORDER)", plan.Plan.ReadyEmailStatus);
        Assert.All(plan.Stages, x => Assert.Equal(StageStatus.Verified, x.Status));
        var email = Assert.Single(s.Transport.Emails);
        Assert.Equal("kylep@kallman.com", Assert.Single(email.SendToAddresses).EmailAddress);
        Assert.Empty(email.CCAddresses); Assert.Empty(email.Attachments);
        Assert.Contains("Bill-To", email.HtmlText); Assert.Contains("city=&#39;&#39;", email.HtmlText);
        Assert.Contains("1 Test Street", email.HtmlText);
        Assert.Equal("Send ready email", s.Transport.Mutations.Last()); // Fake HTTP server labels all email sends this way.
    }

    [Fact]
    public void EveryIndependentProblemIsCollectedInOneEvaluation()
    {
        using var s = new RetryBoundaryTests.Scenario(); BadBilling(s);
        s.Transport.Exhibitor.Salesperson = ""; s.Transport.Items.Clear(); s.Transport.Activities.Clear();
        var row = Evaluate(s);
        Assert.Equal("REVIEW", row.ValidationStatus);
        Assert.Contains(row.Issues, x => x.Component == "Salesperson");
        Assert.Contains(row.Issues, x => x.Component == "Category");
        Assert.Contains(row.Issues, x => x.Component == "Booth");
        Assert.Contains(row.Issues, x => x.Component == "Bill-To");
        Assert.True(row.ContractsResolved); Assert.True(row.NoteResolved); Assert.Empty(s.Transport.Mutations);
    }

    [Fact]
    public void MissingBoothStillCreatesAndAssignsVerifiedBillingAndCopiesContracts()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Activities.Clear();
        Assert.Equal(2, Run(s));
        Assert.Equal("", s.Transport.Order.BoothNumber ?? ""); Assert.Equal("NEWACCOUNT", s.Transport.Order.BillToAccount);
        Assert.Equal("NEWCONTACT", s.Transport.Order.BillToContact); Assert.Equal("PC", s.Transport.Order.OrderStatus);
        Assert.Equal(35, s.Transport.Exhibitor.ExhibitorStatus); Assert.Single(s.Transport.Copies); Assert.Single(s.Transport.Notes);
        var row = Assert.Single(s.Store.Load()).Plan;
        Assert.Contains(row.PartialWork, x => x == "Bill-To verified");
        Assert.Contains("Booth", Assert.Single(s.Transport.Emails).HtmlText);
    }

    [Fact]
    public void InvalidPdfDoesNotPreventIndependentOrderAndBillingWork()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.DocumentData["C/11"] = [1, 2, 3];
        Assert.Equal(1, Run(s));
        Assert.Equal("REP", s.Transport.Order.OrderAccountRep); Assert.Equal("101", s.Transport.Order.BoothNumber);
        Assert.Equal("NEWACCOUNT", s.Transport.Order.BillToAccount); Assert.Empty(s.Transport.Copies); Assert.Empty(s.Transport.Notes);
        Assert.Equal("PC", s.Transport.Order.OrderStatus); Assert.Contains("Contracts", Assert.Single(s.Transport.Emails).HtmlText);
    }

    [Fact]
    public void MissingCategoryDoesNotEraseSavedCategoryOrPreventBillingAndDocuments()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Items.Clear(); s.Transport.Order.Category = 99;
        Assert.Equal(2, Run(s));
        Assert.Equal(99, s.Transport.Order.Category); Assert.Equal("REP", s.Transport.Order.OrderAccountRep);
        Assert.Equal("NEWACCOUNT", s.Transport.Order.BillToAccount); Assert.Single(s.Transport.Copies);
        Assert.DoesNotContain("Update exhibitor categories", s.Transport.Mutations);
    }

    [Fact]
    public void AmbiguousPaymentNotesAreRetainedAndDoNotBlockContractCopy()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Notes.Add(new() { Type = "OH", Class = "SON", OrderNumber = 3, SequenceNumber = 60, Title = ManagedNoteRules.Title, PlainText = "A" });
        s.Transport.Notes.Add(new() { Type = "OH", Class = "SON", OrderNumber = 3, SequenceNumber = 61, Title = ManagedNoteRules.Title, PlainText = "B" });
        Assert.Equal(2, Run(s)); Assert.Single(s.Transport.Copies); Assert.Equal(2, s.Transport.Notes.Count);
        Assert.DoesNotContain("Update payment schedule note", s.Transport.Mutations);
        Assert.DoesNotContain("Add payment schedule note", s.Transport.Mutations);
        Assert.Contains("Payment note", Assert.Single(s.Transport.Emails).HtmlText);
    }

    [Fact]
    public void MissingEmployeeEmailKeepsOrderPendingButFillsVerifiedFields()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Accounts["REP"].Email = "";
        Assert.Equal(2, Run(s)); Assert.Equal("REP", s.Transport.Order.OrderAccountRep);
        Assert.Equal("NEWACCOUNT", s.Transport.Order.BillToAccount); Assert.Equal("PC", s.Transport.Order.OrderStatus);
        Assert.Contains("Finance email", Assert.Single(s.Transport.Emails).HtmlText);
    }

    [Fact]
    public void PreviewEvaluatesAllProblemsAndSavesSummaryWithoutSendingOrWriting()
    {
        using var s = new RetryBoundaryTests.Scenario(); BadBilling(s); s.Transport.Activities.Clear();
        Assert.Equal(2, Run(s, preview: true)); Assert.Empty(s.Transport.Mutations); Assert.Empty(s.Transport.Emails);
        var file = Assert.Single(Directory.GetFiles(s.Options.RunFolder, "*.exceptions.html"));
        var html = File.ReadAllText(file); Assert.Contains("Bill-To", html); Assert.Contains("Booth", html);
        Assert.Contains("NO CHANGE (REMAINS PC)", html);
    }

    [Fact]
    public void UnknownPartialWriteStopsOrderStagesButStillSendsRunExceptionSummary()
    {
        using var s = new RetryBoundaryTests.Scenario("Update service order"); BadBilling(s);
        Assert.Equal(3, Run(s)); Assert.Empty(s.Transport.Copies); Assert.Empty(s.Transport.Notes);
        Assert.DoesNotContain("Update exhibitor categories", s.Transport.Mutations);
        var stage = Assert.Single(Assert.Single(s.Store.Load()).Stages);
        Assert.Equal("Update partial order", stage.Operation); Assert.Equal(StageStatus.Unknown, stage.Status); Assert.Equal(1, stage.Attempts);
        Assert.Contains("UNKNOWN", Assert.Single(s.Transport.Emails).HtmlText);
    }

    [Fact]
    public void ConfirmedHeaderRejectionDoesNotPreventUnrelatedDocumentAndNoteStages()
    {
        using var s = new RetryBoundaryTests.Scenario("Update service order"); BadBilling(s);
        s.Transport.WriteFailureStatus = 400;
        Assert.Equal(1, Run(s)); Assert.Single(s.Transport.Copies); Assert.Single(s.Transport.Notes);
        var plan = Assert.Single(s.Store.Load());
        Assert.Single(plan.Stages, x => x.Operation == "Update partial order" && x.Status == StageStatus.Failed);
        Assert.Single(s.Transport.Requests, x => x.StartsWith("PUT ServiceOrders"));
        Assert.Equal("PC", s.Transport.Order.OrderStatus);
    }

    [Fact]
    public void ChangedBillingInstructionsBlockOnlyBillingStage()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Activities.Clear();
        var row = Evaluate(s); Assert.True(row.BillingResolved);
        s.Transport.BillingInstructions = s.Transport.BillingInstructions with { Email = "changed@example.invalid" };
        var runner = new Runner(s.Options); runner.UseJournalStore(s.Store); runner.ApplyPartial(s.Gateway, row);
        Assert.Equal("ACCOUNT", s.Transport.Order.BillToAccount); Assert.Single(s.Transport.Copies); Assert.Single(s.Transport.Notes);
        Assert.Contains(row.Issues, x => x.Component == "Bill-To" && x.Reason.Contains("instructions changed"));
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("active")]
    [InlineData("not-main")]
    public void LostEligibilityPreventsEveryPartialMutation(string change)
    {
        using var s = new RetryBoundaryTests.Scenario(); BadBilling(s); var row = Evaluate(s);
        if (change == "hold") s.Transport.Exhibitor.ExhibitorCategory = "103";
        if (change == "active") s.Transport.Order.OrderStatus = "A";
        if (change == "not-main") s.Transport.Exhibitor.ExhibitorType = "OTHER";
        var runner = new Runner(s.Options); runner.UseJournalStore(s.Store); runner.ApplyPartial(s.Gateway, row);
        Assert.Empty(s.Transport.Mutations); Assert.Equal("REVIEW", row.Outcome);
    }

    [Fact]
    public void TwoIncompleteOrdersProduceOneSummaryAndCapDoesNotHideTheirIssues()
    {
        using var s = new RetryBoundaryTests.Scenario(); BadBilling(s);
        var second = JsonConvert.DeserializeObject<ServiceOrdersModel>(JsonConvert.SerializeObject(s.Transport.Order))!;
        second.OrderNumber = 4; s.Transport.OtherOrders.Add(second);
        var runner = new Runner(s.Options with { MaxUpdates = 1 }); runner.UseJournalStore(s.Store);
        Assert.Equal(2, runner.ProcessCandidates(s.Gateway, [new(s.Transport.Order, s.Transport.Exhibitor), new(second, s.Transport.Exhibitor)], [], Categories));
        var email = Assert.Single(s.Transport.Emails); Assert.Contains("Order 3", email.HtmlText); Assert.Contains("Order 4", email.HtmlText);
        Assert.Contains("Run limit", email.HtmlText);
        Assert.Equal("DEFERRED (ATTEMPT CAP)", s.Store.Load().Single(x => x.Identity.Order == 4).Plan.ServiceOrderUpdateStatus);
        Assert.Contains("Bill-To", s.Store.Load().Single(x => x.Identity.Order == 4).Plan.ValidationMessage);
    }

    [Fact]
    public void UnknownSummarySendIsJournaledOnceAndNotRetried()
    {
        using var s = new RetryBoundaryTests.Scenario("Send ready email"); BadBilling(s);
        Assert.Equal(3, Run(s)); Assert.Single(s.Transport.Emails);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(s.Options.StateFolder, "notifications"), "*.json", SearchOption.AllDirectories));
        var evidence = System.Text.Json.JsonSerializer.Deserialize<OrderEvidence>(File.ReadAllText(file), new System.Text.Json.JsonSerializerOptions
            { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        var stage = Assert.Single(evidence.Stages); Assert.Equal("Send exception summary", stage.Operation);
        Assert.Equal(StageStatus.Unknown, stage.Status); Assert.Equal(1, stage.Attempts); Assert.False(evidence.Complete);
        var email = Assert.Single(s.Transport.Emails);
        Assert.Throws<RecoveryReviewException>(() => s.Gateway.SendExceptionSummary(evidence.Plan, email.EmailSubject, email.HtmlText, evidence.CreatedRunId));
        Assert.Single(s.Transport.Emails);
    }

    [Fact]
    public void RejectedSummarySendReturnsOperationalFailureEvenWhenOrdersOnlyNeedReview()
    {
        using var s = new RetryBoundaryTests.Scenario("Send ready email"); BadBilling(s);
        s.Transport.BeforeRequest = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Emails/Send")) s.Transport.WriteFailureStatus = 400;
        };
        Assert.Equal(1, Run(s));
        Assert.Single(s.Transport.Requests, x => x.StartsWith("POST Emails/Send"));
        var file = Assert.Single(Directory.GetFiles(Path.Combine(s.Options.StateFolder, "notifications"), "*.json", SearchOption.AllDirectories));
        Assert.Contains("\"Status\": \"Failed\"", File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(s.Options.RunFolder, "*.exceptions.html"));
    }

    [Fact]
    public void SummaryEscapesSubmittedValuesAndIncludesEveryProblem()
    {
        var row = new RunRow { OrderNumber = 12, EventId = 1, ExhibitorId = 2, Outcome = "REVIEW", ExhibitorName = "<script>" };
        row.Issues.Add(new("Bill-To", "Address mismatch", "street='<b>Example</b>', postal='12345'"));
        row.Issues.Add(new("Booth", "Missing accepted activity", "submitted booth=''"));
        var html = ExceptionSummary.Build([row], "run", "report.csv");
        Assert.DoesNotContain("<script>", html); Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Address mismatch", html); Assert.Contains("12345", html); Assert.Contains("Missing accepted activity", html);
    }

    [Fact]
    public void FreshRunAfterManualCorrectionCompletesAndReusesVerifiedCopiesAndNotes()
    {
        using var s = new RetryBoundaryTests.Scenario(); BadBilling(s);
        Assert.Equal(2, Run(s)); var history = Assert.Single(s.Store.Load());
        s.Transport.BillingInstructions = RetryBoundaryTests.TestRequest();
        var fresh = new FileJournalStore(Path.Combine(s.Options.StateFolder, "fresh-run"));
        var runner = new Runner(s.Options); runner.UseJournalStore(fresh);
        Assert.Equal(0, runner.ProcessCandidates(s.Gateway, [new(s.Transport.Order, s.Transport.Exhibitor)], [], Categories));
        Assert.True(Assert.Single(fresh.Load()).Complete); Assert.False(Assert.Single(s.Store.Load()).Complete);
        Assert.Equal("A", s.Transport.Order.OrderStatus); Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus);
        Assert.Single(s.Transport.Copies); Assert.Single(s.Transport.Notes);
        Assert.Single(s.Transport.Emails, x => x.EmailSubject.StartsWith("Ready for invoicing"));
        Assert.Single(s.Transport.Emails, x => x.EmailSubject.StartsWith("Service Order Entry — attention needed"));
        Assert.Equal(history.ProcessingId, Assert.Single(s.Store.Load()).ProcessingId);
    }

    [Fact]
    public void BusinessReviewArisingDuringFullPreflightStillFillsIndependentFields()
    {
        using var s = new RetryBoundaryTests.Scenario();
        var changed = false;
        s.Transport.BeforeRequest = _ =>
        {
            if (changed || s.Gateway.Journal?.Evidence.Plan.ComponentEvaluation != true ||
                s.Gateway.Journal.Evidence.Plan.ValidationStatus is not ("READY" or "PREPARED")) return;
            changed = true; s.Transport.BillingInstructions = s.Transport.BillingInstructions with { Email = "changed@example.invalid" };
        };
        Assert.Equal(2, Run(s)); Assert.True(changed);
        Assert.Equal("REP", s.Transport.Order.OrderAccountRep); Assert.Equal(28, s.Transport.Order.Category);
        Assert.Equal("101", s.Transport.Order.BoothNumber); Assert.Equal("PC", s.Transport.Order.OrderStatus);
        Assert.Equal("ACCOUNT", s.Transport.Order.BillToAccount); Assert.Single(s.Transport.Copies);
        Assert.Contains("instructions changed", Assert.Single(s.Transport.Emails).HtmlText);
    }

    [Fact]
    public void ExistingNonblankBoothIsPreservedExactlyDuringPartialWork()
    {
        using var s = new RetryBoundaryTests.Scenario(); BadBilling(s);
        const string booth = " S11.D76, S11.D77 "; s.Transport.Order.BoothNumber = booth;
        s.Transport.FailReadPath = "Activities/10";
        Assert.Equal(2, Run(s)); Assert.Equal(booth, s.Transport.Order.BoothNumber);
        Assert.DoesNotContain("GET Activities/10", s.Transport.Requests);
    }

    [Fact]
    public void WrongSalespersonAccountDoesNotGetAssignedWhileOtherFieldsAreFilled()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Accounts["REP"].AccountCode = "OTHER";
        s.Transport.Order.OrderAccountRep = "ORIGINAL";
        Assert.Equal(2, Run(s)); Assert.Equal("ORIGINAL", s.Transport.Order.OrderAccountRep);
        Assert.Equal(28, s.Transport.Order.Category); Assert.Equal("101", s.Transport.Order.BoothNumber);
        Assert.Contains("Salesperson", Assert.Single(s.Transport.Emails).HtmlText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnexpectedActivationDuringPartialWriteFailsReadbackInsteadOfClaimingPending(bool exhibitor)
    {
        using var s = new RetryBoundaryTests.Scenario(); BadBilling(s); var changed = false;
        s.Transport.BeforeRequest = _ =>
        {
            if (changed || !s.Transport.Mutations.Contains(exhibitor ? "Update exhibitor categories" : "Update service order")) return;
            changed = true;
            if (exhibitor) s.Transport.Exhibitor.ExhibitorStatus = 2;
            else s.Transport.Order.OrderStatus = "A";
        };
        Assert.Equal(3, Run(s)); Assert.True(changed);
        Assert.Equal("UNKNOWN", Assert.Single(s.Store.Load()).Plan.Outcome);
        Assert.Equal(StageStatus.Unknown, Assert.Single(s.Store.Load()).Stages.Last().Status);
        Assert.Single(s.Transport.Emails);
    }

    [Fact]
    public void PartialJournalFailureHaltsRemainingOrdersAndSummaryDispatch()
    {
        using var s = new RetryBoundaryTests.Scenario(); BadBilling(s);
        var runner = new Runner(s.Options); runner.UseJournalStore(new FailPartialVerification(s.Store));
        Assert.Throws<JournalStorageException>(() => runner.ProcessCandidates(s.Gateway, [new(s.Transport.Order, s.Transport.Exhibitor)], [], Categories));
        Assert.Single(s.Transport.Mutations); Assert.Empty(s.Transport.Copies); Assert.Empty(s.Transport.Notes); Assert.Empty(s.Transport.Emails);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MismatchedExhibitorEventOrOrganizationNeverAuthorizesPartialWork(bool eventMismatch)
    {
        using var s = new RetryBoundaryTests.Scenario(); BadBilling(s);
        if (eventMismatch) s.Transport.Exhibitor.Event = 99;
        else s.Transport.Exhibitor.OrganizationCode = "99";
        var row = Evaluate(s); var runner = new Runner(s.Options); runner.UseJournalStore(s.Store);
        runner.ApplyPartial(s.Gateway, row);
        Assert.Empty(s.Transport.Mutations); Assert.Equal("REVIEW", row.Outcome);
    }

    sealed class FailPartialVerification(IJournalStore inner) : IJournalStore
    {
        public IReadOnlyList<OrderEvidence> Load() => inner.Load();
        public void Save(OrderEvidence evidence)
        {
            inner.Save(evidence);
            if (evidence.Stages.Any(x => x.Operation == "Update partial order" && x.Status == StageStatus.Verified))
                throw new JournalStorageException("Offline journal write failure", new IOException());
        }
    }
}
