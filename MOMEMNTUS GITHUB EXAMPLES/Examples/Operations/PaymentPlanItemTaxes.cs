using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;

namespace Examples.Operations
{
  public class PaymentPlanItemTaxes : Base
  {
    public PaymentPlanItemTaxes(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary>
    public PaymentPlanItemTaxesModel Get(int id)
    {
      return apiClient.Endpoints.PaymentPlanItemTaxes.Get(id);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary>   
    public SearchResponse<PaymentPlanItemTaxesModel> SearchByPaymentPlanId(int paymentPlanId)
    {
      return apiClient.Endpoints.PaymentPlanItemTaxes.Search($"{nameof(PaymentPlanItemTaxesModel.PaymentPlanId)} eq {paymentPlanId}");
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary>   
    public SearchResponse<PaymentPlanItemTaxesModel> SearchByPaymentPlanIdAndPayNumber(int paymentPlanId, int payNumber)
    {
      return apiClient.Endpoints.PaymentPlanItemTaxes.Search($"{nameof(PaymentPlanItemTaxesModel.PaymentPlanId)} eq {paymentPlanId} and {nameof(PaymentPlanItemTaxesModel.PayNumber)} eq {payNumber}");
    }

  }
}
