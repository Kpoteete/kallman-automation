using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class ItemEvidenceTests
{
    static ServiceOrderItemsModel Item() => new() { OrderLineNumber = 1, ResourceCode = "TK", Description = "Turnkey Package",
        Units = 7.5m, UnitCharge = 2090m, UnitCost = 0m, EnteredOn = new DateTime(2026, 10, 5, 12, 53, 10),
        ChangedOn = new DateTime(2026, 10, 5, 12, 53, 10), EnteredBy = "portal", ChangedBy = "portal" };

    [Theory] [InlineData(false)] [InlineData(true)]
    public void OnlyAuditMetadataChangeMatchesNewAndProvenLegacyEvidence(bool legacy)
    {
        var item = Item(); var saved = ItemEvidenceRules.Capture(item);
        if (legacy) saved = saved with { DecisionHash = "" };
        item.ChangedOn = new DateTime(2026, 10, 6, 8, 6, 26); item.ChangedBy = "automation";
        var current = ItemEvidenceRules.Capture(item);
        Assert.NotEqual(saved.EvidenceHash, current.EvidenceHash);
        Assert.True(ItemEvidenceRules.Same([saved], [current]));
    }

    [Theory] [InlineData("units")] [InlineData("charge")] [InlineData("small charge change")] [InlineData("cost")] [InlineData("bill account")]
    [InlineData("item status")] [InlineData("start date")] [InlineData("resource")]
    public void MaterialChangesRemainVisibleInNewAndLegacyAuditFingerprints(string change)
    {
        var item = Item(); var saved = ItemEvidenceRules.Capture(item);
        item.ChangedOn = item.ChangedOn!.Value.AddDays(1); item.ChangedBy = "automation";
        switch (change)
        {
            case "units": item.Units = 10m; break;
            case "charge": item.UnitCharge = 1m; break;
            case "small charge change": item.UnitCharge = 2090.000000000001m; break;
            case "cost": item.UnitCost = 5m; break;
            case "bill account": item.BillToAccountCode = "OTHER"; break;
            case "item status": item.ItemStatus = "C"; break;
            case "start date": item.StartDate = new DateTime(2027, 1, 1); break;
            case "resource": item.ResourceCode = "SPACE"; break;
        }
        var current = ItemEvidenceRules.Capture(item);
        Assert.False(ItemEvidenceRules.Same([saved], [current]));
        Assert.False(ItemEvidenceRules.Same([saved with { DecisionHash = "" }], [current]));
    }

    [Fact] public void UnprovableLegacyMetadataChangeStillDiffersInAuditComparison()
    {
        var item = Item(); item.ChangedBy = "prior editor"; item.ChangedOn = item.ChangedOn!.Value.AddHours(1);
        var saved = ItemEvidenceRules.Capture(item) with { DecisionHash = "" };
        item.ChangedBy = "automation"; item.ChangedOn = item.ChangedOn!.Value.AddDays(1);
        Assert.False(ItemEvidenceRules.Same([saved], [ItemEvidenceRules.Capture(item)]));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ActivationAuditSideEffectDoesNotBlockExhibitorOrFinalEmail(bool legacy)
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Items[0].EnteredOn = new DateTime(2026, 10, 5); s.Transport.Items[0].ChangedOn = s.Transport.Items[0].EnteredOn;
        s.Transport.Items[0].EnteredBy = "portal"; s.Transport.Items[0].ChangedBy = "portal";
        var captured = s.Gateway.GetOrderItems(3);
        s.Row.DecisionInputs = s.Row.DecisionInputs! with { Items = legacy ? captured.Select(x => x with { DecisionHash = "" }).ToList() : captured };
        var changed = false;
        s.Transport.BeforeRequest = _ =>
        {
            if (changed || s.Gateway.Journal?.Evidence.Stages.Any(x => x.Operation == "Activate order" && x.Status == StageStatus.Verified) != true) return;
            changed = true; s.Transport.Items[0].ChangedOn = new DateTime(2026, 10, 6); s.Transport.Items[0].ChangedBy = "automation";
        };
        s.Apply(); Assert.True(changed); Assert.True(s.Row.Outcome == "SUCCESS", s.Row.UpdateMessage);
        Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus); Assert.Single(s.Transport.Emails);
        Assert.Equal("Send ready email", s.Transport.Mutations.Last());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ItemFingerprintChangesAfterHeaderUpdateDoNotBlockPaymentNoteActivationOrEmail(bool legacy)
    {
        using var s = new RetryBoundaryTests.Scenario();
        var captured = s.Gateway.GetOrderItems(3);
        s.Row.DecisionInputs = s.Row.DecisionInputs! with { Items = legacy ? captured.Select(x => x with { DecisionHash = "" }).ToList() : captured };
        var original = s.Row.DecisionInputs.Items[0].EvidenceHash;
        var changed = false;
        s.Transport.BeforeRequest = _ =>
        {
            if (changed || s.Gateway.Journal?.Evidence.Stages.Any(x => x.Operation == "Update service order" && x.Status == StageStatus.Verified) != true) return;
            changed = true;
            s.Transport.Items[0].BillToAccountCode = s.Transport.Order.BillToAccount;
            s.Transport.Items[0].ChangedBy = "automation";
            s.Transport.Items[0].ChangedOn = new DateTime(2026, 10, 8, 13, 57, 50);
            s.Transport.Items[0].Units = 7m;
            s.Transport.Items[0].UnitCharge = 550m;
        };
        s.Apply();
        Assert.True(changed);
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.Contains("Add payment schedule note", s.Transport.Mutations);
        Assert.Equal("A", s.Transport.Order.OrderStatus);
        Assert.Equal(2, s.Transport.Exhibitor.ExhibitorStatus);
        Assert.Single(s.Transport.Emails);
        Assert.Equal(original, s.Store.Load().Single().Plan.DecisionInputs!.Items[0].EvidenceHash);
        Assert.NotEqual(original, s.Gateway.GetOrderItems(3)[0].EvidenceHash);
        var before = s.Transport.Mutations.Count;
        Assert.Equal("COMPLETED", s.Apply(restart: true).ServiceOrderUpdateStatus);
        Assert.Equal(before, s.Transport.Mutations.Count);
        Assert.Single(s.Transport.Emails);
    }

    [Fact]
    public void NonCategoryItemChangesBeforeApplyDoNotBlockProcessing()
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Items[0].Units = 7m;
        s.Transport.Items[0].UnitCharge = 550m;
        s.Transport.Items[0].ChangedBy = "api";
        s.Apply();
        Assert.True(s.Row.ServiceOrderUpdateStatus == "COMPLETED", s.Row.UpdateMessage);
        Assert.Single(s.Transport.Emails);
    }
}
