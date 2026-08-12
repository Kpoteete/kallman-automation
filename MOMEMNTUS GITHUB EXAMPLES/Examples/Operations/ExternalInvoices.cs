using System;
using System.Net.Http;
using System.Threading.Tasks;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;
using System.Collections.Generic;

namespace Examples.Operations
{
  public class ExternalInvoices : Base
  {
    public ExternalInvoices(ApiClient apiClient) : base(apiClient)
    {
    }
    /// <summary>
		/// A basic retrieve example
		/// </summary>
		public ExternalInvoicesModel Get(int headerId)
    {
      return apiClient.Endpoints.ExternalInvoices.Get( headerId);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary>   
    public SearchResponse<ExternalInvoicesModel> Search(string searchValue)
    {
      return apiClient.Endpoints.ExternalInvoices.Search($"{nameof(ExternalInvoicesModel.SupplierNameExt)} eq '{searchValue}'");
    }

    /// <summary>
    /// Import an External Invoice with header and detail lines
    /// </summary>
    /// <param name="model">Optional custom model; if null, uses default example data</param>
    /// <returns>ExternalInvoiceImportResponseModel with created header ID</returns>
    public async Task<ExternalInvoiceImportResponseModel> Import(ExternalInvoiceImportModel model = null)
    {
      if (model == null)
      {
        model = new ExternalInvoiceImportModel
        {
          OrganizationCode = "10",
          AccountSupplier = "0009644",  // Valid supplier account from test data
          Invoice = "TEST-" + DateTime.Now.Ticks,  // Unique invoice number
          Terms = "NB",  // Net Banking terms code
          InvoiceDate = DateTime.Today,
          DueDate = DateTime.Today.AddDays(25),
          InvoiceAmount = 175.00m,  // Sum of detail line amounts
          SupplierNameExt = "Test Supplier",  // Required field - supplier name from external system
          InvoiceDetails = new List<ExternalInvoiceImportDetailModel>
          {
            new ExternalInvoiceImportDetailModel
            {
              Amount = 100.00m,
              Quantity = 1.00m
            },
            new ExternalInvoiceImportDetailModel
            {
              Amount = 50.00m,
              Quantity = 1.00m
            },
            new ExternalInvoiceImportDetailModel
            {
              Amount = 25.00m,
              Quantity = 1.00m
            }
          }
        };
      }

      ExternalInvoiceImportResponseModel response = await apiClient.Endpoints.ExternalInvoices.ImportAsync(model);
      return response;
    }

    /// <summary>
    /// Import an External Invoice with all possible fields populated for advanced testing
    /// </summary>
    /// <param name="model">Optional custom model; if null, uses comprehensive example data</param>
    /// <returns>ExternalInvoiceImportResponseModel with created header ID</returns>
    public async Task<ExternalInvoiceImportResponseModel> ImportAdvanced(ExternalInvoiceImportModel model = null)
    {
      if (model == null)
      {
        model = new ExternalInvoiceImportModel
        {
          // Required/Core Fields
          OrganizationCode = "10",
          AccountSupplier = "0009644",
          Invoice = "ADV-TEST-" + DateTime.Now.Ticks,
          Terms = "NB",
          InvoiceDate = DateTime.Today,
          DueDate = DateTime.Today.AddDays(30),
          InvoiceAmount = 1575.50m,
          SupplierNameExt = "Advanced Test Supplier Inc.",
          
          // Optional Standard Fields
          Currency = "USD",
          Department = "ACCTG",
          Book = "CIVIC",
          
          // External System Fields (Ext)
          InvoiceExt = "EXT-INV-" + DateTime.Now.Ticks,
          DueDateExt = DateTime.Today.AddDays(30),
          DateIssuedExt = DateTime.Today.AddDays(-2),
          CurrencyExt = "USD",
          LanguageExt = "EN",
          TotalAmountExt = 1575.50m,
          TaxBaseTotalExt = 1400.00m,
          TaxTotalExt = 175.50m,
          SupplierAddressExt = "123 Supplier Street, Suite 400, Chicago, IL 60601",
          SupplierCompanyIDExt = "SUP-COMP-12345",
          SupplierTaxNumberExt = "TAX-987654321",
          SupplierVATNumberExt = "VAT-US-123456789",
          RecipientNameExt = "Ungerboeck Systems International",
          RecipientAddressExt = "7101 Fay Ave, Suite 200, La Jolla, CA 92037",
          PurchaseOrderNumberExt = "PO-2024-" + DateTime.Now.Ticks,
          TermsExt = "Net 30 days from invoice date",
          
          // Detail Lines with comprehensive field population
          InvoiceDetails = new List<ExternalInvoiceImportDetailModel>
          {
            new ExternalInvoiceImportDetailModel
            {
              // Core Fields
              Amount = 500.00m,
              Quantity = 10.00m,
              PONumber = "PO-001-2024",
              POLine = 1,
              Reference = "Software License Renewal",
              
              // External Fields
              ItemCodeExternalExt = "SW-LIC-001",
              ItemDescriptionExt = "Annual Software License - Premium Tier",
              ExternalQuantityExt = 10.00m,
              UOMExt = "EA",
              TaxRateExt = 0.0850m,
              ItemTaxExt = 42.50m,
              AmountExt = 500.00m,
              AmountBaseExt = 450.00m,
              AmountTotalExt = 542.50m,
              AmountTotalBaseExt = 492.50m,
              OtherExt = "Priority support included"
            },
            new ExternalInvoiceImportDetailModel
            {
              // Core Fields
              Amount = 750.00m,
              Quantity = 5.00m,
              PONumber = "PO-001-2024",
              POLine = 2,
              Reference = "Professional Services - Consulting",
              
              // External Fields
              ItemCodeExternalExt = "SRV-CON-002",
              ItemDescriptionExt = "Technical Consulting Services - 5 Hours",
              ExternalQuantityExt = 5.00m,
              UOMExt = "HR",
              TaxRateExt = 0.0850m,
              ItemTaxExt = 63.75m,
              AmountExt = 750.00m,
              AmountBaseExt = 750.00m,
              AmountTotalExt = 813.75m,
              AmountTotalBaseExt = 813.75m,
              OtherExt = "Senior consultant rate"
            },
            new ExternalInvoiceImportDetailModel
            {
              // Core Fields
              Amount = 325.50m,
              Quantity = 3.00m,
              PONumber = "PO-002-2024",
              POLine = 1,
              Reference = "Hardware - Network Equipment",
              
              // External Fields
              ItemCodeExternalExt = "HW-NET-003",
              ItemDescriptionExt = "Managed Network Switch - 24 Port Gigabit",
              ExternalQuantityExt = 3.00m,
              UOMExt = "EA",
              TaxRateExt = 0.0850m,
              ItemTaxExt = 27.67m,
              AmountExt = 325.50m,
              AmountBaseExt = 300.00m,
              AmountTotalExt = 353.17m,
              AmountTotalBaseExt = 327.67m,
              OtherExt = "3-year warranty included"
            }
          }
        };
      }

      ExternalInvoiceImportResponseModel response = await apiClient.Endpoints.ExternalInvoices.ImportAsync(model);
      return response;
    }

  }
}
