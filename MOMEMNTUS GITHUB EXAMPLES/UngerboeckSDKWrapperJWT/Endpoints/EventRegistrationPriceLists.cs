using System.Net.Http;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Models.Subjects;

namespace Ungerboeck.Api.Sdk.Endpoints
{
  /// <summary>
  /// Find endpoint calls for this subject here.
  /// </summary>
  public class EventRegistrationPriceLists : Base<EventRegistrationPriceListsModel>
  {
    protected internal EventRegistrationPriceLists(ApiClient api) : base(api) { }

    /// <summary>
    /// Use this endpoint to search for a list of this subject.
    /// </summary>
    /// <param name="orgCode">Fill this with the Organization Code in which the search will take place.</param>
    /// <param name="searchOData">Fill this with OData to query for what you are looking for.  We highly suggest reading our 'Search Using the API' knowledge base article or Ungerboeck API Github examples to learn how to do this. </param>
    /// <param name="options">This contains optional configurations used for searching.</param>
    /// <returns>A list of this subject's model.</returns>
    public new Ungerboeck.Api.Models.Search.SearchResponse<EventRegistrationPriceListsModel> Search(string orgCode, string searchOData, Search options = null)
    {
      return base.Search(orgCode, searchOData, options);
    }

    /// <summary>
    /// Use this endpoint to get a single record of this subject.
    /// </summary>
    /// <param name="orgCode">Fill this with the Organization Code in which the search will take place.</param>
    /// <param name="priceList">The price list code of the Event Registration Price List.</param>
    /// <param name="eventID">The Event ID of the Event Registration Price List.</param>
    /// <param name="options">This contains optional configurations.</param>
    /// <returns>A single model for this subject.</returns>
    public EventRegistrationPriceListsModel Get(string orgCode, string priceList, int eventID, Ungerboeck.Api.Models.Options.Subjects.EventRegistrationPriceLists options = null)
    {
      return base.Get(new { orgCode, priceList, eventID }, options);
    }

    /// <summary>
    /// Use this endpoint to add a new record of this subject.
    /// </summary>
    /// <param name="model">This should contain a filled model of this subject. Note that any null model properties will be ignored for the save.</param>
    /// <param name="options">This contains optional configurations.</param>
    /// <returns>The added model for this subject.</returns>
    public EventRegistrationPriceListsModel Add(EventRegistrationPriceListsModel model, Ungerboeck.Api.Models.Options.Subjects.EventRegistrationPriceLists options = null)
    {
      return base.Add(model, options);
    }

    /// <summary>
    /// Use this endpoint to delete a record of this subject.
    /// </summary>
    /// <param name="orgCode">Fill this with the Organization Code in which the search will take place.</param>
    /// <param name="priceList">The price list code of the Event Registration Price List.</param>
    /// <param name="eventID">The Event ID of the Event Registration Price List.</param>
    /// <param name="options">This contains optional configurations.</param>
    /// <returns>An HttpResponseMessage.</returns>
    public HttpResponseMessage Delete(string orgCode, string priceList, int eventID, Ungerboeck.Api.Models.Options.Subjects.EventRegistrationPriceLists options = null)
    {
      return base.Delete(new { orgCode, priceList, eventID }, options);
    }
  }
}
