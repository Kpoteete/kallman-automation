using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class MissingPaymentPlanTests
{
    static void EvaluateMissingSchedule(RetryBoundaryTests.Scenario s, bool orderOnly = false)
    {
        var data = RetryBoundaryTests.MakePdf("Signed contract. Payment terms are stated elsewhere in the agreement.");
        if (orderOnly)
        {
            s.Transport.Contracts.Clear();
            s.Transport.Copies.Add(new() { Type = "C", SequenceNumber = 22, DocumentID = "order-only.pdf", Category = "CON",
                Description = "Order contract", Order = 3, NewDocumentData = Convert.ToBase64String(data) });
        }
        else s.Transport.DocumentData["C/11"] = data;
        s.Row.Contracts = s.Gateway.ResolveContracts(2, 3, null);
        s.Row.PaymentScheduleText = s.Row.Contracts.PaymentSchedule;
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void MissingScheduleCompletesWithExactNoteEmailAndContractAttachment(bool orderOnly)
    {
        using var s = new RetryBoundaryTests.Scenario(); EvaluateMissingSchedule(s, orderOnly);
        s.Apply();
        Assert.True(s.Row.Outcome == "SUCCESS", s.Row.UpdateMessage);
        Assert.Equal("COMPLETED", s.Row.ServiceOrderUpdateStatus);
        Assert.Equal("A", s.Transport.Order.OrderStatus); Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus);
        Assert.Equal(ContractRules.MissingPaymentPlan, ManagedNoteRules.Text(Assert.Single(s.Transport.Notes)));
        var email = Assert.Single(s.Transport.Emails);
        Assert.Contains(ContractRules.MissingPaymentPlan, email.HtmlText); Assert.Single(email.Attachments);
        var evidence = Assert.Single(s.Store.Load());
        Assert.True(evidence.Plan.Contracts!.MissingPaymentSchedule); Assert.Null(evidence.Plan.Contracts.ScheduleSource);
        Assert.Equal(ContractRules.MissingPaymentPlan, evidence.Handoff!.PaymentSchedule);
        var count = s.Transport.Mutations.Count;
        Assert.Equal("SUCCESS", s.Apply(restart: true).Outcome);
        Assert.Equal(count, s.Transport.Mutations.Count); Assert.Single(s.Transport.Emails);
    }

    [Theory] [InlineData("terms added")] [InlineData("contract removed")] [InlineData("invalid PDF")]
    public void ChangedContractEvidenceStopsMissingSchedulePlanBeforeFirstWrite(string change)
    {
        using var s = new RetryBoundaryTests.Scenario(); EvaluateMissingSchedule(s);
        if (change == "contract removed") s.Transport.Contracts.Clear();
        else s.Transport.DocumentData["C/11"] = change == "terms added" ? RetryBoundaryTests.TestContractPdf : [1, 2, 3];
        s.Apply(); Assert.Empty(s.Transport.Mutations); Assert.NotEqual("SUCCESS", s.Row.Outcome);
    }

    [Fact] public void NoApplicableContractCompletesWithFallbackAndNoInventedAttachment()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Contracts.Clear();
        s.Row.Contracts = s.Gateway.ResolveContracts(2, 3, null); s.Row.PaymentScheduleText = s.Row.Contracts.PaymentSchedule;
        Assert.True(s.Row.Contracts.HasResolvedSchedule); Assert.True(s.Row.Contracts.MissingPaymentSchedule);
        s.Apply(); Assert.True(s.Row.Outcome == "SUCCESS", s.Row.UpdateMessage);
        Assert.Equal(ContractRules.MissingPaymentPlan, ManagedNoteRules.Text(Assert.Single(s.Transport.Notes)));
        var email = Assert.Single(s.Transport.Emails); Assert.Empty(email.Attachments);
        Assert.Contains(ContractRules.MissingPaymentPlan, email.HtmlText);
        Assert.Equal("A", s.Transport.Order.OrderStatus); Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus);
        Assert.DoesNotContain("Copy contract document", s.Transport.Mutations);
        Assert.Equal("SUCCESS", s.Apply(restart: true).Outcome); Assert.Single(s.Transport.Emails);
    }

    [Fact] public void ContractAddedAfterConfirmedAbsenceStopsTheStalePlan()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Contracts.Clear();
        s.Row.Contracts = s.Gateway.ResolveContracts(2, 3, null); s.Row.PaymentScheduleText = s.Row.Contracts.PaymentSchedule;
        s.Transport.Contracts.Add(new() { Type = "C", SequenceNumber = 11, DocumentID = "contract.pdf", Category = "CON", Exhibitor = 2 });
        s.Apply(); Assert.Equal("REVIEW", s.Row.Outcome); Assert.Empty(s.Transport.Mutations);
    }

    [Fact] public void MissingScheduleDoesNotBypassDuplicateDocumentAmbiguity()
    {
        using var s = new RetryBoundaryTests.Scenario(); EvaluateMissingSchedule(s, orderOnly: true);
        var copy = s.Transport.Copies[0];
        s.Transport.Copies.Add(new() { Type = copy.Type, SequenceNumber = 23, DocumentID = copy.DocumentID, Category = copy.Category,
            Description = copy.Description, Order = copy.Order, NewDocumentData = copy.NewDocumentData });
        var plan = s.Gateway.ResolveContracts(2, 3, null);
        Assert.Contains("indistinguishable", plan.ReviewMessage); Assert.False(plan.HasResolvedSchedule);
    }

    [Fact] public void MissingSchedulePreservesLegacyNoteAndAddsCurrentManagedFallback()
    {
        using var s = new RetryBoundaryTests.Scenario(); EvaluateMissingSchedule(s);
        s.Transport.Notes.Add(new NotesModel { SequenceNumber = 9, Title = "Payment Schedule", PlainText = "Human-entered terms",
            Type = "OH", Class = "SON", OrderNumber = 3 });
        s.Apply(); Assert.Equal("SUCCESS", s.Row.Outcome);
        Assert.Equal("Human-entered terms", s.Transport.Notes.Single(x => x.SequenceNumber == 9).PlainText);
        Assert.Single(s.Transport.Notes, x => x.Title == ManagedNoteRules.Title && ManagedNoteRules.Text(x) == ContractRules.MissingPaymentPlan);
    }

    [Fact] public void UnknownMissingScheduleNoteIsReconciledWithoutDuplicate()
    {
        using var s = new RetryBoundaryTests.Scenario("Add payment schedule note"); EvaluateMissingSchedule(s);
        s.Apply(); Assert.Equal("UNKNOWN", s.Row.Outcome); Assert.Empty(s.Transport.Emails);
        Assert.Equal("SUCCESS", s.Apply(restart: true).Outcome);
        Assert.Single(s.Transport.Notes); Assert.Single(s.Transport.Emails);
        Assert.Single(s.Transport.Mutations, x => x == "Add payment schedule note");
    }
}
