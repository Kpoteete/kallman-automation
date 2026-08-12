using System;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace Examples.Operations
{
  public class EventHousingBlocks : Base
  {
    public EventHousingBlocks(ApiClient apiClient) : base(apiClient) { }

    public EventHousingBlocksModel Get(string organizationCode, int transactionSeq)
    {
      return apiClient.Endpoints.EventHousingBlocks.Get(organizationCode, transactionSeq);
    }

    /// <summary>
    /// A search example.
    /// </summary>
    /// <returns></returns>
    public SearchResponse<EventHousingBlocksModel> Search(string orgCode, int searchValue)
    {
      return apiClient.Endpoints.EventHousingBlocks.Search(orgCode, $"{nameof(EventHousingBlocksModel.TransactionSequenceNbr)} eq {searchValue} ");
    }

    /// <summary>
    /// An add example
    /// </summary>
    /// <param name="orgCode">Organization's code</param>
    /// <param name="transDate">Date of the room flow transaction.</param>
    /// <param name="flowDate">Date of the room flow entry the transaction has affected</param>
    /// <param name="acctCode">Account code of the account the block is associated with.</param>
    /// <param name="roomType">Type code of the room type the transaction is associated with.</param>
    /// <param name="eventId">Event ID of the event the block is associated with</param>
    /// <param name="block">Block ID of the block the transaction is associated with.</param>
    /// <param name="units">Amount of the change affecting the room flow entry.</param>
    /// <returns>EventHousingBlocksModel</returns>
    public EventHousingBlocksModel Add(string orgCode, DateTime transDate, DateTime flowDate, string acctCode, string roomType, int eventId, string block, int units)
    {
      EventHousingBlocksModel model = new EventHousingBlocksModel
      {
        OrganizationCode = orgCode,
        TransactionDate = transDate,
        Date = flowDate,
        Property = acctCode,
        RoomType = roomType,
        Event = eventId,
        Block = block,
        RoomUnits = units,
      };

      return apiClient.Endpoints.EventHousingBlocks.Add(model);
    }

    /// <summary>
    /// A basic delete example
    /// </summary>
    /// <param name="orgCode">Organization's code</param>
    /// <param name="transactionSeq">Sequence number of transaction flow</param>
    public void Delete(string orgCode, int transactionSeq)
    {
      apiClient.Endpoints.EventHousingBlocks.Delete(orgCode, transactionSeq);
    }
  }
}