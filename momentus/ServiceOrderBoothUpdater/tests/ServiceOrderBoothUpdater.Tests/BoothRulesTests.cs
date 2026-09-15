using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderBoothUpdater.Tests;

public sealed class BoothRulesTests
{
    [Theory]
    [InlineData("John Smith with Acme accepted booth S2-234, and had these comments \"\".", "S2-234")]
    [InlineData("Musaib Pathan with Current Scientific accepted booth H13-736, and had these comments \"\".", "H13-736")]
    [InlineData("Carol accepted booth  B85 , and had these comments \"Approved\".", "B85")]
    [InlineData("Pat accepted BOOTH s3-141, AND HAD THESE COMMENTS none.", "s3-141")]
    [InlineData("Pat accepted booth S1-100, S1-102, and had these comments none.", "S1-100,S1-102")]
    public void Extracts_booth_from_confirmed_template(string text, string expected)
    {
        var activity = Activity(text);
        Assert.Equal(expected, BoothTextParser.TryCreateCandidate(activity)?.BoothNumber);
    }

    [Theory]
    [InlineData("Booth proposal sent for S2-234.")]
    [InlineData("Accepted booth S2-234 without the expected ending")]
    [InlineData("John accepted booth S2-234<script>, and had these comments none.")]
    public void Rejects_unconfirmed_or_unsafe_text(string text)
    {
        Assert.Null(BoothTextParser.TryCreateCandidate(Activity(text)));
    }

    [Fact]
    public void Rejects_non_bp_activity()
    {
        var activity = Activity("John accepted booth S2-234, and had these comments none.");
        activity.Type = "EMR";
        Assert.Null(BoothTextParser.TryCreateCandidate(activity));
    }

    [Fact]
    public void Eligibility_requires_active_status_and_same_exhibitor_event()
    {
        var candidate = BoothTextParser.TryCreateCandidate(Activity("John accepted booth S2-234, and had these comments none."))!;
        var eligible = new ServiceOrdersModel { OrderStatus = "PC", Exhibitor = 180016, Event = 6209, Account = "00001808" };
        var closed = new ServiceOrdersModel { OrderStatus = "C", Exhibitor = 180016, Event = 6209, Account = "00001808" };
        var otherEvent = new ServiceOrdersModel { OrderStatus = "A", Exhibitor = 180016, Event = 6210, Account = "00001808" };

        Assert.True(OrderRules.IsEligible(eligible, candidate));
        Assert.False(OrderRules.IsEligible(closed, candidate));
        Assert.False(OrderRules.IsEligible(otherEvent, candidate));
    }

    [Fact]
    public void Apply_requires_explicit_confirmation()
    {
        Assert.Throws<CliException>(() => CliOptions.Parse(["apply"]));
        Assert.True(CliOptions.Parse(["apply", "--confirm-update-booth-number"]).Apply);
    }

    [Fact]
    public void Scoped_preview_requires_exhibitor_and_event_together()
    {
        Assert.Throws<CliException>(() => CliOptions.Parse(["preview", "--exhibitor", "193914"]));
        var options = CliOptions.Parse(["preview", "--exhibitor", "193914", "--event", "6208"]);
        Assert.Equal(193914, options.ExhibitorId);
        Assert.Equal(6208, options.EventId);
    }

    private static ActivitiesModel Activity(string text) => new()
    {
        OrganizationCode = "10",
        Account = "00001808",
        SequenceNumber = 451,
        Type = "BP",
        PlainText = text,
        EnteredOn = new DateTime(2025, 9, 17, 14, 19, 0),
        Event = 6209,
        ExhibitorID = 180016
    };
}
