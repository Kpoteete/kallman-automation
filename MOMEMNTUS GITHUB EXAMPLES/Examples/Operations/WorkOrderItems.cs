using System.Net.Http;
using System.Threading.Tasks;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace Examples.Operations
{
  public class WorkOrderItems : Base
  {
    public WorkOrderItems(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary> 
    public WorkOrderItemsModel Get(string orgCode, int orderNbr, int orderLineNbr)
    {
      return apiClient.Endpoints.WorkOrderItems.Get( orgCode, orderNbr, orderLineNbr);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<WorkOrderItemsModel> Search(string orgCode, int searchValue)
    {
      return apiClient.Endpoints.WorkOrderItems.Search(orgCode, $"{nameof(WorkOrderItemsModel.OrderNumber)} eq {searchValue}");
    }

    /// <summary>
    /// An example of completing a work order item with units.
    /// </summary>
    /// <param name="orgCode"></param>
    /// <param name="orderNumber"></param>
    /// <param name="orderLine"></param>
    /// <returns></returns>
    public async Task<HttpResponseMessage> CompleteItemAsync(string orgCode, int orderNumber, int orderLine)
    {
      WorkOrderItemCompleteItemModel data = new WorkOrderItemCompleteItemModel
      {
        OrganizationCode = orgCode,
        OrderNumber = orderNumber,
        OrderLine = orderLine,
        Units = 12.0M // Override the existing units on the work order item during the complete
      };

      return await apiClient.Endpoints.WorkOrderItems.CompleteItemAsync(data);
    }
  }
}
