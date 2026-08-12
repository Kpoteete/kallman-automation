using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Subjects;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ungerboeck.Api.Models.Options;
using System.Net.Http;

namespace Ungerboeck.Api.Sdk.Endpoints
{
  /// <summary>
  /// Find endpoint calls for this subject here.
  /// </summary>
  public class ExternalInvoices : Base<ExternalInvoicesModel>
  {
    protected internal ExternalInvoices(ApiClient api) : base(api) { }

    /// <summary>
    /// Use this endpoint to search for a list of this subject.
    /// </summary>
    /// <param name="searchMetadata">After searching, this will contain search info, such as ResultsTotal.  If your search resulted in more than one page, this will also be filled with API links to navigate pages.</param>
    /// <param name="searchOData">Fill this with OData to query for what you are looking for.  We highly suggest reading our 'Search Using the API' knowledge base article or Ungerboeck API Github examples to learn how to do this. </param>
    /// <param name="options">This contains optional configurations used for searching.</param>
    /// <returns>A list of this subject's model.</returns>
    public Ungerboeck.Api.Models.Search.SearchResponse<ExternalInvoicesModel> Search(string searchOData, Search options = null)
    {
      return base.Search(null, searchOData, options);
    }

    /// <summary>
    /// Use this endpoint to get a single entry of this subject with parameters.
    /// </summary>
    /// <param name="options">This contains optional configurations.</param>
    /// <returns>A single model for this subject.</returns>
    public ExternalInvoicesModel Get(int headerIDExt, Ungerboeck.Api.Models.Options.Subjects.ExternalInvoices options = null)
    {
      return base.Get(new { headerIDExt }, options);
    }

    /// <summary>
    /// Use this endpoint to import an External Invoice with header and detail lines in a single transaction.
    /// </summary>
    /// <param name="model">The External Invoice import model containing header and detail data</param>
    /// <returns>ExternalInvoiceImportResponseModel with the created header ID and operation status</returns>
    public async Task<ExternalInvoiceImportResponseModel> ImportAsync(ExternalInvoiceImportModel model)
    {
      return await PostAsync<ExternalInvoiceImportModel, ExternalInvoiceImportResponseModel>(Client, "ExternalInvoices/Import", model, null);
    }
  }
}
