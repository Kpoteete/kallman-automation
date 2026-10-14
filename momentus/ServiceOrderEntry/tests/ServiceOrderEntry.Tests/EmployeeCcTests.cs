using Ungerboeck.Api.Models.Subjects;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class EmployeeCcTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvaluationCopiesEmailFromTheResolvedRepAccount(bool useLookup)
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Accounts["EMPLOYEE"] = new AllAccountsModel { Organization = "10", AccountCode = "EMPLOYEE", Class = "P", Email = "employee@example.invalid" };
        var reps = useLookup ? new[] { new SalesRepLookup("REP", "REP", "EMPLOYEE") } : [];
        var exhibitor = s.Transport.Exhibitor;
        if (useLookup) exhibitor.ExhibitorUserFields = new() { UserText02 = "REP", UserText14 = "YES" };
        else exhibitor.Salesperson = "EMPLOYEE";
        var row = Runner.Evaluate(s.Gateway, new(s.Transport.Order, exhibitor), reps, [new("Turnkey", 28, ["Turnkey"])]);
        Assert.Equal("EMPLOYEE", row.ProposedOrderAccountRep);
        Assert.Equal("kylep@kallman.com; employee@example.invalid", row.ReadyEmailCcRecipient);
        Assert.Empty(s.Transport.Mutations);
    }

    [Theory]
    [InlineData(" KYLEP@kallman.com ", "kylep@kallman.com")]
    [InlineData("MIRANAC@kallman.com", "kylep@kallman.com")]
    [InlineData("employee@example.invalid", "kylep@kallman.com; employee@example.invalid")]
    public void EmployeeAlreadyInToOrCcIsNotDuplicated(string email, string expected) =>
        Assert.Equal(expected, ReadyEmailBuilder.FinanceCcWithEmployee(email));

    [Theory]
    [InlineData("")]
    [InlineData("broken@@example.invalid")]
    [InlineData("test@example.invalid; other@example.invalid")]
    public void MissingOrInvalidEmployeeEmailBlocksWrites(string email)
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.Accounts["REP"].Email = email;
        s.Apply();
        Assert.Equal("REVIEW", s.Row.Outcome);
        Assert.Empty(s.Transport.Mutations);
        Assert.Empty(s.Transport.Emails);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("organization")]
    public void WrongEmployeeIdentityBlocksWrites(string field)
    {
        using var s = new RetryBoundaryTests.Scenario();
        if (field == "account") s.Transport.Accounts["REP"].AccountCode = "OTHER";
        else s.Transport.Accounts["REP"].Organization = "99";
        s.Apply();
        Assert.Equal("REVIEW", s.Row.Outcome);
        Assert.Empty(s.Transport.Mutations);
    }

    [Theory]
    [InlineData("changed@example.invalid")]
    [InlineData("test@example.invalid")]
    public void ChangedEmployeeEmailStopsTheFinalSend(string changedEmail)
    {
        using var s = new RetryBoundaryTests.Scenario();
        s.Transport.BeforeRequest = _ =>
        {
            if (s.Transport.Mutations.Contains("Activate order")) s.Transport.Accounts["REP"].Email = changedEmail;
        };
        s.Apply();
        Assert.Equal("REVIEW", s.Row.Outcome);
        Assert.Empty(s.Transport.Emails);
        Assert.DoesNotContain("Send ready email", s.Transport.Mutations);
    }
}
