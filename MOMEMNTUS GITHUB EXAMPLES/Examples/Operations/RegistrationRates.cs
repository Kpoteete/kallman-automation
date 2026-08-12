using System.Net.Http;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;
using System.Collections.Generic;
using System.Data.SqlTypes;

namespace Examples.Operations
{
  public class RegistrationRates : Base
  {
    public RegistrationRates(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary> 
    public RegistrationRatesModel Get(string orgCode, int seqNbr, int minMaxSeqNbr)
    {
      return apiClient.Endpoints.RegistrationRates.Get(orgCode, seqNbr, minMaxSeqNbr);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<RegistrationRatesModel> Search(string orgCode, string searchValue)
    {
      return apiClient.Endpoints.RegistrationRates.Search(orgCode, $"{nameof(RegistrationRatesModel.PriceList)} eq '{searchValue}'");
    }

    /// <summary>
    /// Basic Edit example.
    /// </summary>
    /// <param name="orgCode">Organization code of registration rate</param>
    /// <param name="seqNbr">Sequence number of registration rate</param>
    /// <param name="minMaxSeqNbr">Item capacity sequence number of registration rate</param>
    /// <param name="standardPrice">New standard price of registration rate</param>
    /// <returns>Updated registration rate object</returns>
    public RegistrationRatesModel Edit(string orgCode, int seqNbr, int minMaxSeqNbr, decimal standardPrice)
    {
      RegistrationRatesModel registrationRates = apiClient.Endpoints.RegistrationRates.Get(orgCode, seqNbr, minMaxSeqNbr);

      if (registrationRates != null)
      {
        registrationRates.StandardPrice = standardPrice;
      }

      return apiClient.Endpoints.RegistrationRates.Update(registrationRates);
    }

  }
}
