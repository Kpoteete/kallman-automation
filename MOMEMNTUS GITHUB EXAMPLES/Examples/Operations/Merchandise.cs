using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Net.Http;
using System.Threading.Tasks;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace Examples.Operations
{
  public class Merchandise : Base
  {
    public Merchandise(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary> 
    public MerchandiseModel Get(string orgCode, int seqNbr)
    {
      return apiClient.Endpoints.Merchandise.Get(orgCode, seqNbr);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<MerchandiseModel> Search(string orgCode, string searchValue)
    {
      return apiClient.Endpoints.Merchandise.Search(orgCode, $"{nameof(MerchandiseModel.EventID)} eq {searchValue}");
    }


    /// <summary>
    /// A basic delete example
    /// </summary>  
    public void Delete(string orgCode, int sequenceNumber)
    {
      apiClient.Endpoints.Merchandise.Delete(orgCode, sequenceNumber);
    }

    /// <summary>
    /// Basic Edit example.
    /// </summary>
    /// <param name="orgCode">Organization code of merchandise</param>
    /// <param name="seqNbr">Sequence number of merchandise</param>        
    /// <returns>Updated merchandise object</returns>
    public MerchandiseModel Edit(string orgCode, int seqNbr, decimal capacity)
    {
      MerchandiseModel Merchandise = apiClient.Endpoints.Merchandise.Get(orgCode, seqNbr);

      if (Merchandise != null)
      {
        Merchandise.Capacity = capacity;
      }

      return apiClient.Endpoints.Merchandise.Update(Merchandise);
    }

    /// <summary>
    /// Adding merchandise items from resources
    /// </summary>
    /// <param name="orgCode">Organization Code</param>
    /// <param name="eventID">The event id for the merchandise</param>
    /// <param name="sequenceNumbers">The resource sequence numbers to add as merchandise items</param>  
    public HttpResponseMessage AddMerchandise(string orgCode, string eventID, string sequenceNumbers)
    {
      AddMerchandiseModel addMerchandiseModel = new AddMerchandiseModel
      {
        OrganizationCode = orgCode,
        EventID = eventID,
        SequenceNumbers = sequenceNumbers,
      };

      return apiClient.Endpoints.Merchandise.AddMerchandise(addMerchandiseModel);
    }

  }
}
