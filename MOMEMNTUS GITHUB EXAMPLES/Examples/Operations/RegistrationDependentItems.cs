using System.Net.Http;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;

namespace Examples.Operations
{
  public class RegistrationDependentItems : Base
  {
    public RegistrationDependentItems(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary>
    public RegistrationDependentItemsModel Get(string orgCode, int sequenceNumber)
    {
      return apiClient.Endpoints.RegistrationDependentItems.Get(orgCode, sequenceNumber);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary>
    public SearchResponse<RegistrationDependentItemsModel> Search(string orgCode, int searchValue)
    {
      return apiClient.Endpoints.RegistrationDependentItems.Search(orgCode, $"{nameof(RegistrationDependentItemsModel.EventID)} eq {searchValue}");
    }

    /// <summary>
    /// An add example
    /// </summary>
    public RegistrationDependentItemsModel Add(string orgCode, int eventID, int functionID, string registrantType)
    {
      RegistrationDependentItemsModel model = new RegistrationDependentItemsModel
      {
        OrganizationCode = orgCode,
        EventID = eventID,
        RegistrantType = registrantType,
        Function = functionID
      };

      return apiClient.Endpoints.RegistrationDependentItems.Add(model);
    }

    /// <summary>
    /// An edit example
    /// </summary>
    public RegistrationDependentItemsModel Update(string orgCode, int sequenceNumber, RegistrationDependentItemsModel model)
    {
      return apiClient.Endpoints.RegistrationDependentItems.Update(orgCode, sequenceNumber, model);
    }

    /// <summary>
    /// A delete example
    /// </summary>
    public HttpResponseMessage Delete(string orgCode, int sequenceNumber)
    {
      return apiClient.Endpoints.RegistrationDependentItems.Delete(orgCode, sequenceNumber);
    }
  }
}
