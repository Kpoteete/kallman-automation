using ServiceOrderEntry;
using Xunit;

namespace ServiceOrderEntry.Tests;

public sealed class RulesTests
{
    [Theory]
    [InlineData("BK", "BKELLER")]
    [InlineData("Brian Keller", "BKELLER")]
    public void SalesRepMatchesNameOrCode(string input, string expected)
    {
        var rows = new[] { new SalesRepLookup("Brian Keller", "BK", "BKELLER") };
        Assert.Equal(expected, SalesRepRules.Resolve(input, "FALLBACK", rows));
    }

    [Fact]
    public void BlankSalesRepUsesExhibitorFallback() =>
        Assert.Equal("FALLBACK", SalesRepRules.Resolve("", " FALLBACK ", []));

    [Theory]
    [InlineData("Current Scientific Corporation", "Current Scientific")]
    [InlineData("Acme, Inc.", "ACME")]
    public void CompanyComparisonIgnoresLegalSuffixes(string requested, string existing) =>
        Assert.True(TextRules.CompanyMatches(requested, existing));

    [Fact]
    public void CategoryChecksEveryPackageLine()
    {
        var items = new[]
        {
            new OrderItemInfo(1, "X", "Generic Package", "Generic Package"),
            new OrderItemInfo(2, "Y", "Canada Pavilion Turnkey Package", "Canada Pavilion Turnkey Package")
        };
        var lookups = new[] { new CategoryLookup("Turnkey", 28, new[] { "Turnkey", "Canada Pavilion Turnkey" }) };
        var result = CategoryRules.Resolve(items, lookups);
        Assert.Equal(28, result.Sequence);
        Assert.Equal("Turnkey", result.Description);
        Assert.Contains("Canada Pavilion", result.ItemIdentifier);
    }

    [Fact]
    public void ConflictingPackageCategoriesRequireReview()
    {
        var items = new[] { new OrderItemInfo(1, "", "Turnkey Package and Space Only Package", "") };
        var lookups = new[]
        {
            new CategoryLookup("Turnkey", 28, new[] { "Turnkey" }),
            new CategoryLookup("Space Only", 27, new[] { "Space Only" })
        };
        Assert.Null(CategoryRules.Resolve(items, lookups).Sequence);
    }

    [Fact]
    public void BoothParserUsesAcceptedBoothText() =>
        Assert.Equal("A-101,B-2", BoothRules.Parse("Accepted booth A-101, B-2, and had these comments: ok"));

    [Fact]
    public void SharedAccountAddressUpdateRequiresReview()
    {
        var row = ReadyRow();
        row.BillToAddressAction = "UPDATE EXISTING BILL-TO ACCOUNT";
        row.FinalAddress = "1 Main"; row.FinalCity = "New York"; row.FinalPostalCode = "10001"; row.FinalCountry = "USA";
        var result = ValidationRules.Validate(row);
        Assert.Equal("REVIEW", result.Status);
    }

    [Fact]
    public void BlankSelectorUsesExistingAboveValues()
    {
        Assert.True(BillingRules.IsComplete(RunRow.EmptyBilling));
    }

    [Fact]
    public void EcaBillingRequestDoesNotRequireAReplacementAddress()
    {
        var request = RunRow.EmptyBilling with
        {
            CompanyName = "Example", FirstName = "Jane", LastName = "Doe", Email = "jane@example.com", UseRequestedAddress = "ECA"
        };
        Assert.True(BillingRules.IsComplete(request));
    }

    [Fact]
    public void BaBillingRequestRequiresACompleteReplacementAddress()
    {
        var request = RunRow.EmptyBilling with
        {
            CompanyName = "Example", FirstName = "Jane", LastName = "Doe", Email = "jane@example.com", UseRequestedAddress = "BA",
            Address = "1 Main", City = "New York", PostalCode = "10001", Country = "USA"
        };
        Assert.True(BillingRules.IsComplete(request));
        Assert.False(BillingRules.IsComplete(request with { PostalCode = "" }));
    }

    [Fact]
    public void NewRelatedAccountIsPreparedUntilIdentitiesAreVerified()
    {
        var row = ReadyRow();
        row.FinalBillToAccount = "";
        row.FinalBillToContact = "";
        row.ContactAction = "CREATE CONTACT";
        row.BillToAddressAction = "CREATE RELATED BILL-TO ACCOUNT";
        BillingState.Set(row, row.EffectiveBilling! with { Account = row.EffectiveBilling.Account with { AccountCode = "" },
            Contact = row.EffectiveBilling.Contact with { AccountCode = "" }, AccountCreationPending = true, ContactCreationPending = true });
        var result = ValidationRules.Validate(row);
        Assert.Equal("PREPARED", result.Status);
    }

    [Theory]
    [InlineData("USA", "***")]
    [InlineData("United States", "***")]
    [InlineData("CAN", "CAN")]
    public void CountryValuesUseMomentusCodes(string input, string expected) =>
        Assert.Equal(expected, TextRules.MomentusCountry(input));

    [Fact]
    public void ReadyRowPassesValidation()
    {
        var result = ValidationRules.Validate(ReadyRow());
        Assert.Equal("READY", result.Status);
        Assert.Equal("", result.Message);
    }

    [Fact]
    public void ApplyRequiresConfirmationAndExhibitor()
    {
        Assert.Throws<CliException>(() => CliOptions.Parse(new[] { "apply" }));
        Assert.Throws<CliException>(() => CliOptions.Parse(new[] { "apply", "--confirm-service-order-entry" }));
    }

    [Fact]
    public void ReadyEmailIsEnabledByDefaultAndCanBeSkipped()
    {
        var enabled = CliOptions.Parse(new[] { "apply", "--confirm-service-order-entry", "--exhibitor", "1" });
        var skipped = CliOptions.Parse(new[] { "apply", "--confirm-service-order-entry", "--exhibitor", "1", "--skip-ready-email" });
        Assert.True(enabled.SendReadyEmail);
        Assert.False(skipped.SendReadyEmail);
    }

    [Fact]
    public void BulkApplyDefaultsToTenAndRejectsLargerBatches()
    {
        var bulk = CliOptions.Parse(new[] { "apply", "--confirm-service-order-entry", "--all" });
        Assert.True(bulk.All);
        Assert.Equal(10, bulk.MaxUpdates);
        Assert.Throws<CliException>(() => CliOptions.Parse(new[] { "apply", "--confirm-service-order-entry", "--all", "--max-updates", "11" }));
        Assert.Throws<CliException>(() => CliOptions.Parse(new[] { "apply", "--confirm-service-order-entry", "--all", "--exhibitor", "1" }));
    }

    [Fact]
    public void EnabledEventListReadsIdsCommentsAndRejectsInvalidValues()
    {
        var validPath = Path.GetTempFileName();
        var invalidPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(validPath, "# disabled events\n6193\n6208 # reason\n6193\n");
            File.WriteAllText(invalidPath, "not-an-event\n");

            Assert.Equal(new[] { 6193, 6208 }, EnabledEventList.Load(validPath).Order());
            Assert.Throws<CliException>(() => EnabledEventList.Load(invalidPath));
        }
        finally
        {
            File.Delete(validPath);
            File.Delete(invalidPath);
        }
    }

    [Fact]
    public void IndividualExhibitorRunBypassesEventAllowlistButBulkDoesNot()
    {
        IReadOnlySet<int> enabled = new HashSet<int> { 6298 };

        Assert.True(EventScopeRules.IsAllowed(6298, enabled, false));
        Assert.False(EventScopeRules.IsAllowed(6305, enabled, false));
        Assert.True(EventScopeRules.IsAllowed(6305, enabled, true));
    }

    [Fact]
    public void ReadyEmailContainsOrderBillingPaymentAndAttachmentDetails()
    {
        var row = ReadyRow();
        row.ExhibitorName = "Example & Company";
        row.OrderDate = new DateTime(2026, 9, 3);
        row.ProposedCategoryName = "Turnkey";
        row.FinalExhibitorCategories = "1,66";
        row.OrderStatusAction = "NO CHANGE (REMAINS PC)";
        row.ExhibitorStatusAction = "NO CHANGE (REMAINS 35)";
        row.FinalAddress = "1 Main Street";
        row.FinalCity = "New York";
        row.FinalState = "NY";
        row.FinalPostalCode = "10001";
        row.FinalCountry = "***";
        row.PaymentScheduleText = "Payment Schedule\n50% Deposit";
        row.RequestedBilling = row.RequestedBilling with { CompanyName = "Bill To Co", FirstName = "Jane", LastName = "Doe", Email = "jane@example.com" };
        BillingState.Set(row, row.EffectiveBilling! with { Contact = row.EffectiveBilling.Contact with { FirstName = "Jane", LastName = "Doe", Email = "jane@example.com" } });
        var html = ReadyEmailBuilder.Build(row, [new DocumentInfo("C", 1, "contract.pdf", "Signed Contract", "CON")]);
        Assert.Contains("Example &amp; Company", html);
        Assert.Contains("September 3, 2026", html);
        Assert.Contains("Bill-To Information", html);
        Assert.Contains("jane@example.com", html);
        Assert.Contains("Payment Schedule<br>50% Deposit", html);
        Assert.Contains("Signed Contract", html);
        Assert.DoesNotContain("Exhibitor categories", html);
        Assert.DoesNotContain("Order status action", html);
        Assert.DoesNotContain("Exhibitor status action", html);
        Assert.DoesNotContain("Address action", html);
        Assert.DoesNotContain("Contact action", html);
    }

    [Fact]
    public void ApprovalNeededEmailWarnsToWaitForSalesInSubjectAndAtTop()
    {
        var row = ReadyRow();
        row.ApprovalNeeded = true;

        var subject = ReadyEmailBuilder.Subject(row);
        var html = ReadyEmailBuilder.Build(row, [new DocumentInfo("C", 1, "contract.pdf", "Signed Contract", "CON")]);

        Assert.StartsWith("WAIT FOR SALES APPROVAL", subject);
        Assert.Contains("Do not continue until you receive notice from Sales", html);
        Assert.Contains("pending final approval", html);
        Assert.True(html.IndexOf("Do not continue", StringComparison.Ordinal) < html.IndexOf("ready for invoicing", StringComparison.Ordinal));
    }

    [Fact]
    public void ExhibitorCategoriesPreserveExistingAndAddOrderAndLineRules()
    {
        var items = new[] { new OrderItemInfo(1, "", "Custom Build package with sponsorship", "") };
        var result = ExhibitorCategoryRules.Resolve("76,1", "Space Only", items, "Y");
        Assert.Equal(new[] { 1, 76 }, result.Existing);
        Assert.Equal(new[] { 2, 3, 5, 65, 66 }, result.Add);
        Assert.Equal(new[] { 1 }, result.Remove);
        Assert.Equal("2,3,5,65,66,76", ExhibitorCategoryRules.Format(result.Final));
    }

    [Fact]
    public void ApprovalNeededBlocksActivationButIsNeverAddedByRules()
    {
        var result = ExhibitorCategoryRules.Resolve("102,9", "Kiosk", [], "N");
        Assert.True(result.ApprovalNeeded);
        Assert.Empty(result.Add);
        Assert.Empty(result.Remove);
        Assert.Equal("9,102", ExhibitorCategoryRules.Format(result.Final));
    }

    [Theory]
    [InlineData("103", true)]
    [InlineData("1,103,76", true)]
    [InlineData("1,102,76", false)]
    [InlineData("", false)]
    public void HoldCategoryIsDetected(string categories, bool expected)
    {
        Assert.Equal(expected, ExhibitorCategoryRules.HasCode(categories, ExhibitorCategoryRules.Hold));
    }

    [Fact]
    public void CountryCategoriesAreNotAdded()
    {
        var result = ExhibitorCategoryRules.Resolve("", "Turnkey", [], "N");
        Assert.DoesNotContain(55, result.Final);
        Assert.DoesNotContain(56, result.Final);
    }

    [Fact]
    public void SpaceOnlyReplacesStaleTurnkeyAndCustomBuildCategories()
    {
        var result = ExhibitorCategoryRules.Resolve("1,3,76", "Space Only", [], "N");
        Assert.Equal(new[] { 2 }, result.Add);
        Assert.Equal(new[] { 1, 3 }, result.Remove);
        Assert.Equal("2,76", ExhibitorCategoryRules.Format(result.Final));
    }

    [Fact]
    public void PaymentScheduleIsExtractedAsACompleteBlock()
    {
        const string text = "Before\nPayment Schedule\nAdvanced Payment Schedule\n25% Deposit: Due upon receipt of invoice.\nStandard Payment Schedule\n50% Deposit: Due upon receipt of invoice.\nLate Booking Schedule\n100% Full Payment: Due upon contract acceptance.\nNote: Please refer to your specific contract and invoice\nfor final payment terms and due dates.\nAfter";
        var result = PaymentScheduleExtractor.FromText(text);
        Assert.StartsWith("Payment Schedule", result);
        Assert.Contains("Advanced Payment Schedule", result);
        Assert.Contains("Late Booking Schedule", result);
        Assert.EndsWith("for final payment terms and due dates.", result);
        Assert.DoesNotContain("Before", result);
        Assert.DoesNotContain("After", result);
    }

    [Fact]
    public void PaymentScheduleWithoutAdvancedSectionIsExtracted()
    {
        const string text = "Payment Schedule\nStandard Payment Schedule\n50% Deposit: Due upon receipt of invoice.\n50% Final Balance: Due 180 days before the show start date.\nLate Booking Schedule\n(Applies to bookings made within 180 days of the show start date.)\n100% Full Payment: Due upon contract acceptance.\nNote: Please refer to your specific contract and invoice\nfor final payment terms and due dates.";
        var result = PaymentScheduleExtractor.FromText(text);
        Assert.StartsWith("Payment Schedule", result);
        Assert.Contains("Standard Payment Schedule", result);
        Assert.Contains("Late Booking Schedule", result);
        Assert.DoesNotContain("Advanced Payment Schedule", result);
        Assert.EndsWith("for final payment terms and due dates.", result);
    }

    [Fact]
    public void CompactPaymentScheduleWithBulletSeparatorsIsExtracted()
    {
        const string text = "Payment Schedule\nStandard Payment Schedule: For bookings made more than 1 year before the show start date • 30% due upon invoice • 30% Due May 31, 2027 • 40% due 180 days before the show\nAccelerated Payment Schedule: For bookings made within 1 year of the show but more than 180 days before the show • 50% due upon invoice • 50% due 180 days before the show\nLate Booking Schedule: For bookings made within 180 days of the show start date • 100% due upon contract acceptance\nPlease refer to your specific contract and invoice for final payment terms and due dates.";

        var result = PaymentScheduleExtractor.FromText(text);

        Assert.StartsWith("Payment Schedule", result);
        Assert.Contains("Standard Payment Schedule:", result);
        Assert.Contains("30% Due May 31, 2027", result);
        Assert.Contains("Accelerated Payment Schedule:", result);
        Assert.Contains("Late Booking Schedule:", result);
        Assert.EndsWith("final payment terms and due dates.", result);
    }

    [Fact]
    public void PaymentScheduleNormalizesPdfLigaturesForMomentusNotes()
    {
        const string text = "Payment Schedule\nStandard Payment Schedule\nNote: Please refer to your speciﬁc contract and invoice\nfor ﬁnal payment terms and due dates.";
        var result = PaymentScheduleExtractor.FromText(text);
        Assert.Contains("specific contract", result);
        Assert.Contains("final payment", result);
        Assert.DoesNotContain('ﬁ', result);
    }

    [Fact]
    public void PaymentScheduleRemovesUnsupportedPdfBulletMarkers()
    {
        var text = "Payment Schedule\n?Standard Schedule\n?50% Deposit: Due upon receipt of invoice.\nNote: Please refer to your specific contract and invoice for final payment terms and due dates.";

        var result = PaymentScheduleExtractor.FromText(text);

        Assert.Contains("Standard Schedule", result);
        Assert.Contains("50% Deposit", result);
        Assert.DoesNotContain("?", result);
    }

    private static RunRow ReadyRow() => new()
    {
        EventId = 1, ExhibitorId = 2, OrderNumber = 3, ProposedOrderAccountRep = "REP", ProposedCategory = 28,
        ProposedBoothNumber = "101", FinalBillToAccount = "ACCOUNT", FinalBillToContact = "CONTACT",
        BillingVersion = 1, ValidCategoryIds = [28],
        EffectiveBilling = new(RetryBoundaryTests.TestAccount(), RetryBoundaryTests.TestContact(), "Existing", "Existing", true),
        BillToAddressAction = "KEEP EXISTING", RequestedBilling = RunRow.EmptyBilling with { Email = "billing@example.com" }
        , PaymentScheduleText = "Payment Schedule", PaymentScheduleNoteAction = "ADD"
    };
}
