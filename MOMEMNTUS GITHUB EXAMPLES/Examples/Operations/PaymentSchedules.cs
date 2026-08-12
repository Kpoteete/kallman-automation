using System.Net.Http;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;
using System.Collections.Generic;
using Subjects;

namespace Examples.Operations
{
  public class PaymentSchedules : Base
  {
    public PaymentSchedules(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary>
    public Ungerboeck.Api.Models.Subjects.PaymentSchedulesModel Get(string orgCode, string scheduleCode)
    {
      return apiClient.Endpoints.PaymentSchedules.Get(orgCode, scheduleCode);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary>   
    public SearchResponse<Ungerboeck.Api.Models.Subjects.PaymentSchedulesModel> Search(string orgCode, string searchValue)
    {
      return apiClient.Endpoints.PaymentSchedules.Search(orgCode, $"{nameof(Ungerboeck.Api.Models.Subjects.PaymentSchedulesModel.Code)} eq '{searchValue}'");
    }
  }
}
