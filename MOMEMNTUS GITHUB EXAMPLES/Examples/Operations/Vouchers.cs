using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace Examples.Operations
{
  public class Vouchers : Base
  {
    public Vouchers(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary> 
    public VouchersModel Get(string orgCode, int voucher)
    {
      return apiClient.Endpoints.Vouchers.Get( orgCode, voucher);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<VouchersModel> Search(string orgCode, string searchValue)
    {
      return apiClient.Endpoints.Vouchers.Search(orgCode, $"{nameof(VouchersModel.City)} eq '{searchValue}'");
    }

    /// <summary>
    /// Delete an existing voucher
    /// </summary>  
    public void Delete(string orgCode, int voucher)
    {
      apiClient.Endpoints.Vouchers.Delete(orgCode, voucher);
    }

    /// <summary>
    /// Void an existing voucher
    /// </summary>  
    public HttpResponseMessage Void(string orgCode, int voucher)
    {
      return apiClient.Endpoints.Vouchers.Void(orgCode, voucher);
    }

    /// <summary>
    /// Import vouchers in bulk. If no model is provided, uses a default valid example.
    /// </summary>
    /// <param name="model">Optional model to import. If null, a default example will be used.</param>
    /// <returns>The imported voucher model</returns>
    public Task<VoucherImportBulkResponseModel> Import(VoucherImportBulkModel model = null)
    {
      // If no model provided, use a default valid example
      if (model == null)
      {
        model = new VoucherImportBulkModel
        {
          OrganizationCode = "10",
          BatchDescription = "Test Voucher Import - " + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
          BatchDate = DateTime.Today,
          Vouchers = new List<VoucherImportHeaderModel>
          {
            new VoucherImportHeaderModel
            {
              Invoice = "INV-2025-001",
              Type = "VI",
              InvoiceDate = DateTime.Today.AddDays(-5),
              DueDate = DateTime.Today.AddDays(25),
              InvoiceAmount = 1500.00m,
              Supplier = "0009474",
              BookControl = "ARENA",
              UseDemographics = "Y",
              Reference = "Reference note",
              Department = "AV",
              VoucherDistribution = new List<VoucherImportDistributionModel>
              {
                new VoucherImportDistributionModel
                {
                  GLAccount = "5000",
                  Amount = 1000.00m,
                  Description = "Office Supplies",
                  Event = 11124,
                  Department = "AV",
                  BookControl = "AR1",
                  GLCoreDimension01 = "DIM1",
                  GLCoreDimension02 = "DIM2",
                  Reference = "Detail Reference",
                  ManagementRptCode = "MRC1",
                  TaxCode = "",
                  TaxType = "8AP00",
                  TaxableAmount = 1000,
                  TaxAmount = 0,
                },
                new VoucherImportDistributionModel
                {
                  GLAccount = "5100",
                  Amount = 500.00m,
                  Description = "Equipment"
                }
              }
            },
            new VoucherImportHeaderModel
            {
              Invoice = "INV-2025-002",
              Type = "VI",
              InvoiceDate = DateTime.Today.AddDays(-3),
              DueDate = DateTime.Today.AddDays(27),
              InvoiceAmount = 2400.00m,
              Supplier = "0009474",
              VoucherDistribution = new List<VoucherImportDistributionModel>
              {
                new VoucherImportDistributionModel
                {
                  GLAccount = "6000",
                  Amount = 1200.00m,
                  Description = "Marketing Services"
                },
                new VoucherImportDistributionModel
                {
                  GLAccount = "6100",
                  Amount = 1200.00m,
                  Description = "Consulting Fees",
                  Event = 11125,
                  Department = "DEPT1",
                  BookControl = "ARENA",
                  GLCoreDimension01 = "LC1",
                  GLCoreDimension02 = "UNIT01",
                  Reference = "Detail Reference second",
                  ManagementRptCode = "MRC3",
                  TaxCode = "",
                  TaxType = "8AP10",
                  TaxableAmount = 500,
                  TaxAmount = 50,
                }
              }
            }
          }
        };
      }

      return apiClient.Endpoints.Vouchers.ImportBulk(model);

    }
  }
}


