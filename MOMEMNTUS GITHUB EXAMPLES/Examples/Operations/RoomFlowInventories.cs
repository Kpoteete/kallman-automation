using System;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace Examples.Operations
{
  public class RoomFlowInventories : Base
  {
    public RoomFlowInventories(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary>
    public RoomFlowInventoriesModel Get(string orgCode, int flowSequenceNumber)
    {
      return apiClient.Endpoints.RoomFlowInventories.Get(orgCode, flowSequenceNumber);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary>
    public SearchResponse<RoomFlowInventoriesModel> Search(string orgCode, string searchValue)
    {
      return apiClient.Endpoints.RoomFlowInventories.Search(orgCode, $"{nameof(RoomFlowInventoriesModel.Block)} eq '{searchValue}'");
    }

    /// <summary>
    /// A basic add example for reference
    /// </summary>
    public RoomFlowInventoriesModel Add(DateTime roomFlowDate,
      string organizationCode,
      int eventID,
      string property,
      string roomType,
      string flowRecordType,
      decimal price,
      int forecasted,
      string useSameRates,
      string blockCode)
    {
      var flowInventoryModel = new RoomFlowInventoriesModel
      {
        OrganizationCode = organizationCode,
        EventID = eventID,
        Block = blockCode,
        Property = property,
        RoomType = roomType,
        FlowRecordType = flowRecordType.ToString(),
        Price = price,
        Forecasted = forecasted,
        UseSameRates = useSameRates,
        RoomFlowDate = roomFlowDate
      };

      return apiClient.Endpoints.RoomFlowInventories.Add(flowInventoryModel);
    }

    /// <summary>
    /// A basic edit example for reference
    /// </summary>
    public RoomFlowInventoriesModel Edit(string organizationCode, int flowSequenceNumber, decimal newPrice)
    {
      var flowInventory = apiClient.Endpoints.RoomFlowInventories.Get(organizationCode, flowSequenceNumber);
      flowInventory.Price = newPrice;
      return apiClient.Endpoints.RoomFlowInventories.Update(flowInventory);
    }
  }
}