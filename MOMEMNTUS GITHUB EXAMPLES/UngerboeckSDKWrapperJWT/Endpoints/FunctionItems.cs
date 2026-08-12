using System.Net.Http;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Models.Subjects;

namespace Ungerboeck.Api.Sdk.Endpoints
{
  /// <summary>
  /// Find endpoint calls for this subject here.
  /// </summary>
  public class FunctionItems : Base<FunctionItemsModel>
  {
    protected internal FunctionItems(ApiClient api) : base(api) { }

    /// <summary>
    /// Use this endpoint to search for a list of this subject.
    /// </summary>
    /// <param name="searchMetadata">After searching, this will contain search info, such as ResultsTotal.  If your search resulted in more than one page, this will also be filled with API links to navigate pages.</param>
    /// <param name="orgCode">Fill this with the Organization Code in which the search will take place.</param>
    /// <param name="searchOData">Fill this with OData to query for what you are looking for.  We highly suggest reading our 'Search Using the API' knowledge base article or Ungerboeck API Github examples to learn how to do this. </param>
    /// <param name="options">This contains optional configurations used for searching.</param>
    /// <returns>A list of this subject's model.</returns>
    public new Ungerboeck.Api.Models.Search.SearchResponse<FunctionItemsModel> Search(string orgCode, string searchOData, Search options = null)
    {
      return base.Search(orgCode, searchOData, options);
    }

    /// <summary>
    /// use this endpoint to get a single record of this subject.
    /// </summary>
    /// <param name="orgCode">Fill this with the Organization Code in which the search will take place.</param>
    /// <param name="sequenceNumber"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    public FunctionItemsModel Get(string orgCode, int sequenceNumber, Ungerboeck.Api.Models.Options.Subjects.FunctionItems options = null)
    {
      return base.Get(new { orgCode, sequenceNumber }, options);
    }

    /// <summary>
    /// Use this endpoint to add a new record of this subject.
    /// </summary>
    /// <param name="model">This should contain a filled model of this subject. Note that any null model properties will be ignored for the save.</param>
    /// <param name="options"></param>
    /// <returns></returns>
    public FunctionItemsModel Add(FunctionItemsModel model, Ungerboeck.Api.Models.Options.Subjects.FunctionItems options = null)
    {
      return base.Add(model, options);
    }

    /// <summary>
    /// Use this endpoint to edit a record of this subject.
    /// </summary>
    /// <param name="model">This should contain a filled model of this subject. Note that any null model properties will be ignored for the save.</param>
    /// <param name="options"></param>
    /// <returns></returns>
    public FunctionItemsModel Update(FunctionItemsModel model, Ungerboeck.Api.Models.Options.Subjects.FunctionItems options = null)
    {
      return base.Update(new { model.OrganizationCode, model.SequenceNumber }, model, options);
    }

    /// <summary>
    /// Use this endpoint to delete a record of this subject.
    /// </summary>
    /// <param name="orgCode">Fill this with the Organization Code in which the search will take place.</param>
    /// <param name="sequenceNumber"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    public HttpResponseMessage Delete(string orgCode, int sequenceNumber, Ungerboeck.Api.Models.Options.Subjects.FunctionItems options = null)
    {
      return base.Delete(new { orgCode, sequenceNumber }, options);
    }
  }
}
