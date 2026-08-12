using System.Net.Http;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Models.Subjects;

namespace Ungerboeck.Api.Sdk.Endpoints
{
  /// <summary>
  /// Find endpoint calls for this subject here.
  /// </summary>
  public class RegistrationDependentItems : Base<RegistrationDependentItemsModel>
  {
    protected internal RegistrationDependentItems(ApiClient api) : base(api) { }

    /// <summary>
    /// Use this endpoint to search for a list of this subject.
    /// </summary>
    /// <param name="orgCode">Fill this with the Organization Code in which the search will take place.</param>
    /// <param name="searchOData">Fill this with OData to query for what you are looking for.  We highly suggest reading our 'Search Using the API' knowledge base article or Ungerboeck API Github examples to learn how to do this. </param>
    /// <param name="options">This contains optional configurations used for searching.</param>
    /// <returns>A list of this subject's model.</returns>
    public new Ungerboeck.Api.Models.Search.SearchResponse<RegistrationDependentItemsModel> Search(string orgCode, string searchOData, Search options = null)
    {
      return base.Search(orgCode, searchOData, options);
    }

    /// <summary>
    /// Use this endpoint to get a single record of this subject.
    /// </summary>
    /// <param name="orgCode">Fill this with the Organization Code in which the search will take place.</param>
    /// <param name="sequenceNumber">The sequence number of the Registration Dependent Item.</param>
    /// <returns>A single model for this subject.</returns>
    public RegistrationDependentItemsModel Get(string orgCode, int sequenceNumber)
    {
      return base.Get(new { orgCode, sequenceNumber });
    }

    /// <summary>
    /// Use this endpoint to add a single record of this subject.
    /// </summary>
    /// <param name="data">Set the properties for this model object.</param>
    /// <returns>A single model for this subject.</returns>
    public RegistrationDependentItemsModel Add(RegistrationDependentItemsModel data)
    {
      return base.Add(data);
    }

    /// <summary>
    /// Use this endpoint to edit a single record of this subject.
    /// </summary>
    /// <param name="orgCode">Fill this with the Organization Code.</param>
    /// <param name="sequenceNumber">The sequence number of the Registration Dependent Item.</param>
    /// <param name="data">Set the properties for this model object.</param>
    /// <returns>A single model for this subject.</returns>
    public RegistrationDependentItemsModel Update(string orgCode, int sequenceNumber, RegistrationDependentItemsModel data)
    {
      return base.Update(new { orgCode, sequenceNumber }, data);
    }

    /// <summary>
    /// Use this endpoint to delete a single record of this subject.
    /// </summary>
    /// <param name="orgCode">Fill this with the Organization Code.</param>
    /// <param name="sequenceNumber">The sequence number of the Registration Dependent Item.</param>
    /// <returns>Nothing if successful.</returns>
    public HttpResponseMessage Delete(string orgCode, int sequenceNumber)
    {
      return base.Delete(new { orgCode, sequenceNumber });
    }
  }
}
