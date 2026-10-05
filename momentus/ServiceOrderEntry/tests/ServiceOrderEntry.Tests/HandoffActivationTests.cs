using System.Net;
using System.Text;
using Newtonsoft.Json;
using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class HandoffActivationTests
{
    static ServiceOrdersModel Other(int number = 4, string status = "PC") => new() { OrganizationCode = "10", OrderNumber = number, Exhibitor = 2, Event = 1,
        Function = 4, Account = "ACCOUNT", BillToAccount = "ACCOUNT", BillToContact = "CONTACT", OrderStatus = status };
    static HttpResponseMessage Page(object[] rows, int page = 1, string? next = null, int total = 1) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonConvert.SerializeObject(rows), Encoding.UTF8, "application/json"), Headers =
        { { "X-SearchMetadata", JsonConvert.SerializeObject(new { Page = page, Page_Size = 1, PageTotal = total, ResultsTotal = total, Links = new { Next = next } }) } } };
    static int Run(RetryBoundaryTests.Scenario s, CliOptions? options = null, IReadOnlyList<Candidate>? candidates = null)
    {
        var runner = new Runner(options ?? s.Options); runner.UseJournalStore(s.Store);
        return runner.ProcessCandidates(s.Gateway, candidates ?? [new(s.Transport.Order, s.Transport.Exhibitor)], [], [new("Turnkey", 28, ["Turnkey"])]);
    }
    [Theory] [InlineData("above")] [InlineData("reuse")] [InlineData("new")]
    public void EmailBindsToVerifiedEffectiveBillingAndCommittedOrder(string mode)
    {
        using var s = new RetryBoundaryTests.Scenario(mode == "new" ? "Create organization account" : null); s.Transport.FaultEnabled = false;
        if (mode == "above")
        {
            s.Row.RequestedBilling = s.Row.RequestedBilling with { UseRequestedAddress = "ECA", CompanyName = "Ignore this requested name", Email = "irrelevant@example.invalid", AttentionOf = "Ignore attention" };
            s.Transport.BillingInstructions = s.Row.RequestedBilling;
            Runner.DecideAccountAndAddress(s.Gateway, s.Row); Runner.DecideContact(s.Gateway, s.Row);
        }
        s.Apply(); Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        var handoff = s.Store.Load().Single().Handoff!; var email = Assert.Single(s.Transport.Emails);
        Assert.Contains(handoff.Billing.Account.Company, email.HtmlText); Assert.Contains(handoff.Billing.Account.AccountCode, email.HtmlText);
        Assert.Contains(handoff.Billing.Contact.Email, email.HtmlText); Assert.Contains("Turnkey (28)", email.HtmlText); Assert.Contains("REP", email.HtmlText);
        Assert.DoesNotContain("Ignore", email.HtmlText); Assert.NotEmpty(handoff.Attachments);
    }
    [Fact] public void OrderOnlyContractIsAttachedWithoutCopyingExhibitorDocument()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Contracts.Clear();
        s.Transport.Copies.Add(new() { Type = "C", SequenceNumber = 22, DocumentID = "order-only.pdf", Category = "CON", Description = "Order contract", Order = 3,
            NewDocumentData = Convert.ToBase64String(RetryBoundaryTests.TestContractPdf) });
        s.Row.Contracts = s.Gateway.ResolveContracts(2, 3, null);
        s.Apply(); Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.DoesNotContain("Copy contract document", s.Transport.Mutations); Assert.Single(Assert.Single(s.Transport.Emails).Attachments);
    }
    [Theory] [InlineData("missing hash")] [InlineData("malformed pdf")] [InlineData("changed bytes")] [InlineData("empty recipient")] [InlineData("invalid CC")] [InlineData("invalid second To")]
    public void DetectableAttachmentOrRecipientProblemBlocksFirstWrite(string error)
    {
        using var s = new RetryBoundaryTests.Scenario();
        if (error == "empty recipient") s.Row.ReadyEmailRecipient = "";
        else if (error == "invalid CC") s.Row.ReadyEmailCcRecipient = "kylep@@kallman.com";
        else if (error == "invalid second To") s.Row.ReadyEmailRecipient = "finance@example.invalid; broken@@example.invalid";
        else if (error == "missing hash") { var source = s.Row.Contracts!.Sources[0]; s.Row.Contracts = s.Row.Contracts with { Sources = [source with { Document = source.Document with { ContentHash = "" } }] }; }
        else s.Transport.DocumentData["C/11"] = error == "malformed pdf" ? Encoding.UTF8.GetBytes("not a PDF") : RetryBoundaryTests.MakePdf(RetryBoundaryTests.TestSchedule + "\nRevision");
        s.Apply(); Assert.Empty(s.Transport.Mutations); Assert.NotEqual(0, Runner.ApplyExitCode([s.Row]));
    }
    [Fact] public void SavedDescriptionAloneCannotAuthorizeSendOrAcceptedState()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.SavedEmails.Add(new() { Type = "M", SequenceNumber = 99, Order = 3, Description = "Ready for invoicing - Service Order 3" });
        Assert.Throws<RecoveryReviewException>(() => s.Gateway.ReadyEmailWasSent(3)); Assert.Empty(s.Transport.Mutations);
    }
    [Fact] public void EmailTimeoutCannotResendAfterRestart()
    {
        using var s = new RetryBoundaryTests.Scenario("Send ready email"); s.Apply();
        Assert.Equal("UNKNOWN", s.Row.Outcome); Assert.Equal(3, Runner.ApplyExitCode([s.Row]));
        var row = s.Apply(restart: true); Assert.Equal("UNKNOWN", row.Outcome); Assert.Single(s.Transport.Emails);
        Assert.DoesNotContain("Activate order", s.Transport.Mutations);
    }
    [Fact] public void UnverifiedRequiredOrderStageCannotAuthorizeOrderActivation()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Gateway.Journal!.Evidence.Handoff = null;
        Assert.Throws<RecoveryReviewException>(() => HandoffRules.RequireOrderStages(s.Gateway.Journal.Evidence, includeActivation: false));
        Assert.Empty(s.Transport.Mutations);
    }
    [Fact] public void OtherPendingOrderDefersExhibitorAndRemainsRecoverable()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.OtherOrders.Add(Other()); s.Apply();
        Assert.Equal(2, Runner.ApplyExitCode([s.Row])); Assert.Equal(35, s.Transport.Exhibitor.ExhibitorStatus); Assert.Equal("A", s.Transport.Order.OrderStatus);
        var evidence = s.Store.Load().Single(); Assert.True(evidence.OrderComplete); Assert.True(evidence.Complete); Assert.True(evidence.ExhibitorActivationPending);
        Assert.Single(Runner.RecoveryWork([evidence], s.Options)); Assert.DoesNotContain("Activate exhibitor", s.Transport.Mutations);
    }
    [Theory] [InlineData("UNKNOWN")] [InlineData("FAILED")] [InlineData("REVIEW")] [InlineData("PENDING")]
    public void UnfinishedPeerJournalBlocksExhibitorEvenWhenItsOrderIsActive(string outcome)
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.OtherOrders.Add(Other(status: "A"));
        var peer = new OrderEvidence { Identity = OrderIdentity.From(s.Options, s.Row) with { Order = 4 },
            Plan = new RunRow { EventId = 1, ExhibitorId = 2, OrderNumber = 4, Outcome = outcome } }; s.Store.Save(peer);
        s.Apply(); Assert.Equal(35, s.Transport.Exhibitor.ExhibitorStatus); Assert.True(s.Row.ExhibitorActivationPending);
        Assert.DoesNotContain("Activate exhibitor", s.Transport.Mutations);
    }
    [Fact] public void BothApplicableOrdersFinishBeforeSingleExhibitorActivation()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.OtherOrders.Add(Other());
        var candidates = new Candidate[] { new(s.Transport.Order, s.Transport.Exhibitor), new(s.Transport.OtherOrders[0], s.Transport.Exhibitor) };
        Assert.Equal(0, Run(s, s.Options with { MaxUpdates = 10 }, candidates));
        Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus); Assert.All(new[] { s.Transport.Order }.Concat(s.Transport.OtherOrders), x => Assert.Equal("A", x.OrderStatus));
        Assert.Single(s.Transport.Mutations, x => x == "Activate exhibitor"); Assert.Equal(2, s.Transport.Emails.Count);
        var records = s.Store.Load(); Assert.Equal(2, records.Count); Assert.All(records, x => Assert.True(x.OrderComplete));
        var previous = records.Single(x => x.Identity.Order == 3); Assert.False(previous.ExhibitorActivationPending);
        var count = s.Transport.Mutations.Count;
        var runner = new Runner(s.Options); runner.UseJournalStore(s.Store);
        runner.Apply(s.Gateway, new(s.Transport.Order, s.Transport.Exhibitor), previous.Plan);
        Assert.Equal("SUCCESS", previous.Plan.Outcome); Assert.Equal(count, s.Transport.Mutations.Count);
    }
    [Theory] [InlineData("hold")] [InlineData("approval")]
    public void HoldOrApprovalAddedAfterOrderActivationBlocksExhibitor(string condition)
    {
        using var s = new RetryBoundaryTests.Scenario(); var changed = false;
        s.Transport.BeforeRequest = _ => { if (!changed && s.Gateway.Journal?.Evidence.Stages.Any(x => x.Operation == "Activate order" && x.Status == StageStatus.Verified) == true)
            { changed = true; s.Transport.Exhibitor.ExhibitorCategory = condition == "hold" ? "1,103" : "1,102"; } };
        s.Apply(); Assert.True(changed); Assert.Equal(35, s.Transport.Exhibitor.ExhibitorStatus); Assert.DoesNotContain("Activate exhibitor", s.Transport.Mutations);
    }
    [Fact] public void LaterPagePcOrderBlocksActivation()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.SearchOverride = request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/ServiceOrders/10")) return null;
            return request.RequestUri.Query.Contains("page=2") ? Page([Other()], 2, total: 2) : Page([s.Transport.Order], 1, "?page=2", 2);
        };
        s.Apply(); Assert.Equal(35, s.Transport.Exhibitor.ExhibitorStatus); Assert.True(s.Row.ExhibitorActivationPending);
    }
    [Fact] public void IncompleteActivationOrderSearchFailsClosed()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.SearchOverride = request => request.RequestUri!.AbsolutePath.EndsWith("/ServiceOrders/10") ? Page([s.Transport.Order], total: 2) : null;
        s.Apply(); Assert.Equal("REVIEW", s.Row.Outcome); Assert.Equal(2, Runner.ApplyExitCode([s.Row])); Assert.Equal(35, s.Transport.Exhibitor.ExhibitorStatus);
    }
    [Theory] [InlineData("retrieval")] [InlineData("parse")] [InlineData("configuration")]
    public void EvaluationOnlyFailuresAreOperationalFailures(string failure)
    {
        using var s = new RetryBoundaryTests.Scenario();
        if (failure == "retrieval") s.Transport.FailReadPath = "Accounts/10/ACCOUNT";
        if (failure == "parse") s.Transport.DocumentData["C/11"] = Encoding.UTF8.GetBytes("malformed PDF");
        if (failure == "configuration") s.Transport.Accounts["ACCOUNT"] = new() { AccountCode = "ACCOUNT", Class = "O", AccountUserFieldSets = [] };
        Assert.Equal(1, Run(s, s.Options with { Apply = false })); Assert.Empty(s.Transport.Mutations);
        var report = File.ReadAllText(Directory.GetFiles(s.Options.RunFolder, "*.csv").Single()); Assert.Contains("FAILED", report);
    }
    [Fact] public void BusinessReviewHasDistinctExitStatusAndNoMutations()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.BillingInstructions = s.Row.RequestedBilling with { UseRequestedAddress = "UNKNOWN" };
        Assert.Equal(2, Run(s, s.Options with { Apply = false })); Assert.Empty(s.Transport.Mutations);
    }
    [Fact] public void StoragePreflightFailurePreventsAllRequests()
    {
        using var s = new RetryBoundaryTests.Scenario(); Directory.CreateDirectory(s.Options.RunFolder);
        var file = Path.Combine(s.Options.RunFolder, "not-a-folder"); File.WriteAllText(file, "preserve");
        Assert.ThrowsAny<IOException>(() => Run(s, s.Options with { RunFolder = file })); Assert.Empty(s.Transport.Requests); Assert.Empty(s.Transport.Mutations);
    }
    [Theory] [InlineData("body")] [InlineData("attachments")] [InlineData("recipient")] [InlineData("cc")]
    public void AcceptedEmailResponseMustCorroborateExactIntent(string field)
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Row.ReadyEmailCcRecipient = "copy@example.invalid";
        s.Transport.ResponseOverride = result =>
        {
            if (result is not EmailsModel email) return result;
            var changed = JsonConvert.DeserializeObject<EmailsModel>(JsonConvert.SerializeObject(email))!;
            if (field == "body") changed.HtmlText = "different body";
            if (field == "attachments") changed.Attachments.Clear();
            if (field == "recipient") changed.SendToAddresses[0].EmailAddress = "other@example.invalid";
            if (field == "cc") changed.CCAddresses[0].EmailAddress = "other@example.invalid";
            return changed;
        };
        s.Apply(); Assert.Equal("UNKNOWN", s.Row.Outcome); Assert.DoesNotContain("Activate order", s.Transport.Mutations);
        s.Apply(restart: true); Assert.Single(s.Transport.Emails);
    }

    [Fact] public void FinanceEmailUsesTwoToRecipientsCcAndLeadingDevelopmentNotice()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Row.ReadyEmailRecipient = ReadyEmailBuilder.FinanceRecipients;
        s.Row.ReadyEmailCcRecipient = ReadyEmailBuilder.FinanceCcRecipient;
        s.Apply();
        Assert.Equal("SUCCESS", s.Row.Outcome);
        var email = Assert.Single(s.Transport.Emails);
        Assert.Equal(new[] { "MiranaC@kallman.com", "LindsayH@kallman.com" }, email.SendToAddresses.Select(x => x.EmailAddress));
        Assert.Equal("kylep@kallman.com", Assert.Single(email.CCAddresses).EmailAddress);
        Assert.Empty(email.BCCAddresses);
        Assert.True(email.HtmlText.IndexOf(ReadyEmailBuilder.DevelopmentNotice, StringComparison.Ordinal) < email.HtmlText.IndexOf("Hi Mirana", StringComparison.Ordinal));
        var intent = Newtonsoft.Json.Linq.JObject.Parse(s.Store.Load().Single().Stages.Single(x => x.Operation == "Send ready email").Intent);
        Assert.Equal("kylep@kallman.com", (string?)intent["CCAddresses"]![0]!["EmailAddress"]);
        Assert.Equal("A", s.Transport.Order.OrderStatus);
        Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus);
        s.Apply(restart: true);
        Assert.Single(s.Transport.Emails);
    }
    [Fact] public void WrongOrganizationReturnedForOrderCannotAuthorizeMutation()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Order.OrganizationCode = "99"; s.Apply();
        Assert.Equal("REVIEW", s.Row.Outcome); Assert.Empty(s.Transport.Mutations);
    }
    [Fact] public void MissingHeaderIdentityCannotAuthorizeNoteUpdate()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.SearchOverride = request => request.RequestUri!.AbsolutePath.EndsWith("/Notes/10")
            ? Page([new NotesModel { SequenceNumber = 9, Type = "OH", Class = "SON", Title = ManagedNoteRules.Title, PlainText = "existing", OrderNumber = 4 }]) : null;
        s.Apply(); Assert.Equal("REVIEW", s.Row.Outcome); Assert.Empty(s.Transport.Mutations);
    }
    [Fact] public void AttemptCapRetainsUnprocessedOrderIntentForActivationGate()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.OtherOrders.Add(Other());
        Run(s, candidates: [new(s.Transport.Order, s.Transport.Exhibitor), new(s.Transport.OtherOrders[0], s.Transport.Exhibitor)]);
        Assert.Equal(2, s.Store.Load().Count); Assert.False(s.Store.Load().Single(x => x.Identity.Order == 4).Complete);
        Assert.Equal(35, s.Transport.Exhibitor.ExhibitorStatus);
    }
}
