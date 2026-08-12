using System.Net.Http;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;

namespace Examples.Operations
{
  public class EventRegistrationPriceLists : Base
  {
    public EventRegistrationPriceLists(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary>
    public EventRegistrationPriceListsModel Get(string orgCode, string priceList, int eventID)
    {
      return apiClient.Endpoints.EventRegistrationPriceLists.Get(orgCode, priceList, eventID);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary>
    public SearchResponse<EventRegistrationPriceListsModel> Search(string orgCode, int searchValue)
    {
      return apiClient.Endpoints.EventRegistrationPriceLists.Search(orgCode, $"{nameof(EventRegistrationPriceListsModel.EventID)} eq {searchValue}");
    }

    /// <summary>
    /// An add example
    /// </summary>
    public EventRegistrationPriceListsModel Add(string orgCode, string priceList, int eventID)
    {
      EventRegistrationPriceListsModel model = new EventRegistrationPriceListsModel
      {
        OrganizationCode = orgCode,
        PriceList = priceList,
        EventID = eventID
      };

      return apiClient.Endpoints.EventRegistrationPriceLists.Add(model);
    }

    /// <summary>
    /// A delete example
    /// </summary>
    public HttpResponseMessage Delete(string orgCode, string priceList, int eventID)
    {
      return apiClient.Endpoints.EventRegistrationPriceLists.Delete(orgCode, priceList, eventID);
    }
  }
}
