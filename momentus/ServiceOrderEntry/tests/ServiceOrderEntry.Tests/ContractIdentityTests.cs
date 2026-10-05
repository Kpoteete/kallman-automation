using Newtonsoft.Json;
using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class ContractIdentityTests
{
    static ContractSnapshot Contract(int id, string schedule, string hash = "hash", string owner = "Exhibitor") =>
        new("10", owner, owner == "Order" ? 3 : 2, new("C", id, $"{id}.pdf", "Contract", "CON") { ContentHash = hash }, hash, schedule);
    static NotesModel Note(int id, string title, string text) => new() { SequenceNumber = id, Title = title, PlainText = text, Type = "OH", Class = "SON", OrderNumber = 3 };
    static StageEvidence Proof(NotesModel note) => new() { Operation = "Add payment schedule note", Status = StageStatus.Verified, Result = JsonConvert.SerializeObject(note) };
    static void Copy(RetryBoundaryTests.Scenario s, int id, byte[]? data = null) => s.Transport.Copies.Add(new()
    { Type = "C", SequenceNumber = id, DocumentID = "contract.pdf", Category = "CON", Description = "Contract", Order = 3,
        NewDocumentData = Convert.ToBase64String(data ?? RetryBoundaryTests.TestContractPdf) });

    [Fact] public void ConflictingExhibitorAndOrderSchedulesRequireReview()
    {
        var decision = ContractRules.Select([Contract(1, "50%")], [Contract(2, "100%", "different", "Order")]);
        Assert.Contains("conflicting", decision.ReviewMessage); Assert.Null(decision.ScheduleSource);
    }
    [Fact] public void EquivalentSchedulesChooseStableAuditIdentityRegardlessOfResponseOrder()
    {
        var a = Contract(1, "50%\nDue now"); var b = Contract(2, "50%\r\nDue now", "another");
        Assert.Equal(a, ContractRules.Select([b, a], []).ScheduleSource);
        Assert.Equal(a, ContractRules.Select([a, b], []).ScheduleSource);
    }
    [Fact] public void SameDescriptionDifferentBytesDoNotMatch()
    {
        Assert.False(Runner.HasMatchingDocument([Contract(1, "terms", "old").Document], Contract(2, "terms", "new").Document));
        Assert.False(ContractRules.SameContent(new("C", 1, "a", "Contract", "CON"), new("C", 2, "b", "Contract", "CON")));
    }
    [Fact] public void MultipleIndistinguishableOrderContractsRequireReview() => Assert.Contains("indistinguishable",
        ContractRules.Select([], [Contract(1, "terms", owner: "Order"), Contract(2, "terms", owner: "Order")]).ReviewMessage);
    [Fact] public void ConflictingSchedulesWithinOneDocumentRequireReview() => Assert.Throws<RecoveryReviewException>(() =>
        PaymentScheduleExtractor.FromPdf(RetryBoundaryTests.MakePdf(RetryBoundaryTests.TestSchedule + "\n" + RetryBoundaryTests.TestSchedule.Replace("50%", "100%"))));
    [Fact] public void ConflictingApplicablePdfCandidatesRequireReview()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Contracts.Add(new() { Type = "C", SequenceNumber = 13, DocumentID = "second.pdf", Description = "Contract", Category = "CON", Exhibitor = 2 });
        s.Transport.DocumentData["C/13"] = RetryBoundaryTests.MakePdf(RetryBoundaryTests.TestSchedule.Replace("50%", "100%"));
        Assert.Contains("conflicting", s.Gateway.ResolveContracts(2, 3, null).ReviewMessage); Assert.Empty(s.Transport.Mutations);
    }
    [Fact] public void OrderOnlyContractProvidesScheduleWithoutFallback()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Contracts.Clear(); Copy(s, 22);
        var result = s.Gateway.ResolveContracts(2, 3, null);
        Assert.False(result.NearbyFallback); Assert.Equal("Order", result.ScheduleSource!.Owner); Assert.Empty(result.Sources);
    }
    [Fact] public void ExistingNormalContractWithoutScheduleDoesNotAuthorizeNearbyFallback()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.DocumentData["C/11"] = RetryBoundaryTests.MakePdf("Other contract text");
        var result = s.Gateway.ResolveContracts(2, 3, DateTime.Today);
        Assert.False(result.NearbyFallback); Assert.Contains("No complete", result.ReviewMessage);
    }
    [Fact] public void EmptyFallbackIsReviewInsteadOfIndexFailure()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Contracts.Clear();
        var result = s.Gateway.ResolveContracts(2, 3, DateTime.Today);
        Assert.True(result.NearbyFallback); Assert.Null(result.ScheduleSource); Assert.NotEmpty(result.ReviewMessage);
    }
    [Fact] public void RevisedSourceWithUnchangedDescriptionBlocksCopy()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.DocumentData["C/11"] = RetryBoundaryTests.MakePdf(RetryBoundaryTests.TestSchedule + "\nRevised");
        Assert.Throws<RecoveryReviewException>(() => s.Gateway.EnsureContractCopies(s.Transport.Order, s.Row)); Assert.Empty(s.Transport.Mutations);
    }
    [Fact] public void ExistingExactContentCopyIsReusedAndPersisted()
    {
        using var s = new RetryBoundaryTests.Scenario(); Copy(s, 22);
        Assert.Equal(0, s.Gateway.EnsureContractCopies(s.Transport.Order, s.Row));
        Assert.Equal(22, Assert.Single(s.Store.Load().Single().Plan.ContractCopies).Destination.SequenceNumber); Assert.Empty(s.Transport.Mutations);
    }
    [Fact] public void SameDescriptionDifferentContentStillCopiesApplicableSource()
    {
        using var s = new RetryBoundaryTests.Scenario(); Copy(s, 22, RetryBoundaryTests.MakePdf("Different content"));
        Assert.Equal(1, s.Gateway.EnsureContractCopies(s.Transport.Order, s.Row)); Assert.Single(s.Transport.Mutations);
    }
    [Fact] public void EqualSourceBytesAreCopiedOnlyOnce()
    {
        using var s = new RetryBoundaryTests.Scenario(); var first = s.Row.Contracts!.Sources[0];
        var second = first with { Document = first.Document with { SequenceNumber = 13 } };
        s.Transport.DocumentData["C/13"] = RetryBoundaryTests.TestContractPdf;
        s.Row.Contracts = ContractRules.Select([second, first], []);
        Assert.Equal(1, s.Gateway.EnsureContractCopies(s.Transport.Order, s.Row)); Assert.Single(s.Transport.Copies);
    }
    [Fact] public void JournaledCopyIdentityIsReusedAfterRestart()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Gateway.EnsureContractCopies(s.Transport.Order, s.Row);
        var row = s.Store.Load().Single().Plan;
        Assert.Equal(0, s.Gateway.EnsureContractCopies(s.Transport.Order, row)); Assert.Single(s.Transport.Mutations);
        Assert.Equal(22, Assert.Single(row.ContractCopies).Destination.SequenceNumber);
    }
    [Fact] public void MissingJournaledDestinationCannotCreateReplacement()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Gateway.EnsureContractCopies(s.Transport.Order, s.Row); s.Transport.Copies.Clear();
        Assert.Throws<RecoveryReviewException>(() => s.Gateway.EnsureContractCopies(s.Transport.Order, s.Row)); Assert.Single(s.Transport.Mutations);
    }
    [Theory] [InlineData(false)] [InlineData(true)] public void UnknownCopyIsReconciledWithoutRedispatchOrReviewsAmbiguity(bool ambiguous)
    {
        using var s = new RetryBoundaryTests.Scenario("Copy contract document");
        Assert.Throws<UnknownWriteOutcomeException>(() => s.Gateway.EnsureContractCopies(s.Transport.Order, s.Row));
        if (ambiguous) Copy(s, 23);
        if (ambiguous) Assert.Throws<RecoveryReviewException>(() => s.Gateway.ReconcileIncomplete());
        else
        {
            s.Gateway.ReconcileIncomplete(); Assert.Equal(0, s.Gateway.EnsureContractCopies(s.Transport.Order, s.Row));
            var stage = Assert.Single(s.Store.Load().Single().Stages); Assert.Equal(StageStatus.Verified, stage.Status);
            Assert.Equal("22", stage.Source["destinationSequence"]); Assert.Equal(stage.Source["contentHash"], stage.Source["destinationHash"]);
        }
        Assert.Single(s.Transport.Mutations);
    }
    [Fact] public void UnrelatedSonInstructionsSurviveManagedNoteCreation()
    {
        using var s = new RetryBoundaryTests.Scenario(); var unrelated = Note(9, "Delivery instructions", "Keep this instruction"); s.Transport.Notes.Add(unrelated);
        Assert.Equal("ADDED", s.Gateway.SavePaymentScheduleNote(3, s.Row.PaymentScheduleText));
        Assert.Equal("Keep this instruction", s.Transport.Notes.Single(x => x.SequenceNumber == 9).PlainText); Assert.Equal(2, s.Transport.Notes.Count);
    }
    [Fact] public void OwnedNoteIsUpdatedByIdentityAndPersistsHash()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Notes.Add(Note(9, ManagedNoteRules.Title, "old terms"));
        s.Row.ManagedNoteSequence = 9; s.Row.ManagedNoteContentHash = ManagedNoteRules.ContentHash("old terms");
        Assert.Equal("UPDATED", s.Gateway.SavePaymentScheduleNote(3, s.Row.PaymentScheduleText));
        var row = s.Store.Load().Single().Plan; Assert.Equal(9, row.ManagedNoteSequence);
        Assert.Equal(ManagedNoteRules.ContentHash(s.Row.PaymentScheduleText), row.ManagedNoteContentHash); Assert.Single(s.Transport.Notes);
    }
    [Fact] public void MultipleOwnedNotesRequireReviewWithoutMutation()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Notes.AddRange([Note(1, ManagedNoteRules.Title, "a"), Note(2, ManagedNoteRules.Title, "b")]);
        Assert.Throws<RecoveryReviewException>(() => s.Gateway.SavePaymentScheduleNote(3, s.Row.PaymentScheduleText)); Assert.Empty(s.Transport.Mutations);
    }
    [Fact] public void LegacyTitleWithoutJournalOwnershipIsPreservedForReview()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Notes.Add(Note(9, "Payment Schedule", "legacy terms"));
        Assert.Throws<RecoveryReviewException>(() => s.Gateway.SavePaymentScheduleNote(3, s.Row.PaymentScheduleText)); Assert.Empty(s.Transport.Mutations);
        Assert.Equal("legacy terms", Assert.Single(s.Transport.Notes).PlainText);
    }
    [Fact] public void VerifiedUnchangedLegacyNoteMayMigrateOnlyByStableId()
    {
        var legacy = Note(9, "Payment Schedule", "legacy terms");
        Assert.Equal("OWNED", ManagedNoteRules.Resolve([legacy], [Proof(legacy)]).Action);
        Assert.Equal("REVIEW", ManagedNoteRules.Resolve([Note(10, "Payment Schedule", "legacy terms")], [Proof(legacy)]).Action);
        Assert.Equal("REVIEW", ManagedNoteRules.Resolve([Note(9, "Payment Schedule", "edited terms")], [Proof(legacy)]).Action);
    }
    [Fact] public void ChangedManagedContentAfterEvaluationIsNotOverwritten()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Transport.Notes.Add(Note(9, ManagedNoteRules.Title, "new human terms"));
        s.Row.ManagedNoteSequence = 9; s.Row.ManagedNoteContentHash = ManagedNoteRules.ContentHash("evaluated terms");
        Assert.Throws<RecoveryReviewException>(() => s.Gateway.SavePaymentScheduleNote(3, s.Row.PaymentScheduleText)); Assert.Empty(s.Transport.Mutations);
    }
    [Fact] public void JournaledNoteIdentityIsReusedWithoutAnotherCreation()
    {
        using var s = new RetryBoundaryTests.Scenario(); s.Gateway.SavePaymentScheduleNote(3, s.Row.PaymentScheduleText);
        Assert.Equal("ALREADY MATCHED", s.Gateway.SavePaymentScheduleNote(3, s.Row.PaymentScheduleText));
        Assert.Single(s.Transport.Mutations); Assert.Equal(12, s.Store.Load().Single().Plan.ManagedNoteSequence);
    }
    [Fact] public void UnknownNoteAddIsReconciledWithoutDuplicate()
    {
        using var s = new RetryBoundaryTests.Scenario("Add payment schedule note");
        Assert.Throws<UnknownWriteOutcomeException>(() => s.Gateway.SavePaymentScheduleNote(3, s.Row.PaymentScheduleText));
        s.Gateway.ReconcileIncomplete(); Assert.Equal("ALREADY MATCHED", s.Gateway.SavePaymentScheduleNote(3, s.Row.PaymentScheduleText));
        Assert.Single(s.Transport.Mutations); Assert.Single(s.Transport.Notes);
    }
}
