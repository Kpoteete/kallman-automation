using System.Net.Http;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;
using System.Collections.Generic;

namespace Examples.Operations
{
  public class ServiceOrderItemTaxes : Base
  {
    public ServiceOrderItemTaxes(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary>
    public ServiceOrderItemTaxesModel Get(string orgCode, int orderLine, int orderNumber, int sequenceNumber)
    {
      return apiClient.Endpoints.ServiceOrderItemTaxes.Get(orgCode, orderLine, orderNumber, sequenceNumber);
    }

    /// <summary>
    /// A search example. Check out the 'Search using the API' knowledge base article for more info.
    /// </summary>   
    public SearchResponse<ServiceOrderItemTaxesModel> Search(string orgCode, int searchValue)
    {
      return apiClient.Endpoints.ServiceOrderItemTaxes.Search(orgCode, $"{nameof(ServiceOrderItemTaxesModel.SequenceNumber)} eq {searchValue}");
    }
  }
}
