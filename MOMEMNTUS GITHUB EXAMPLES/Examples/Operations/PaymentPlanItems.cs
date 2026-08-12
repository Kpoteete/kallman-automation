using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;

namespace Examples.Operations
{
  public class PaymentPlanItems : Base
  {
    public PaymentPlanItems(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary>
    public PaymentPlanItemsModel Get(int id)
    {
      return apiClient.Endpoints.PaymentPlanItems.Get(id);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary>   
    public SearchResponse<PaymentPlanItemsModel> SearchByPaymentPlanId(int paymentPlanId)
    {
      return apiClient.Endpoints.PaymentPlanItems.Search($"{nameof(PaymentPlanItemsModel.PayPlanID)} eq {paymentPlanId}");
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary>   
    public SearchResponse<PaymentPlanItemsModel> SearchByPaymentPlanIdAndPayNumber(int paymentPlanId, int payNumber)
    {
      return apiClient.Endpoints.PaymentPlanItems.Search($"{nameof(PaymentPlanItemsModel.PayPlanID)} eq {paymentPlanId} and {nameof(PaymentPlanItemsModel.PayNumber)} eq {payNumber}");
    }

  }
}