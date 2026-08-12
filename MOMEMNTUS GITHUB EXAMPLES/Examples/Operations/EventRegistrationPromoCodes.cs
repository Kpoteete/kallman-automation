using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;


namespace Examples.Operations
{
  public class EventRegistrationPromoCodes : Base
  {
    public EventRegistrationPromoCodes(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary> 
    public EventRegistrationPromoCodesModel Get(string orgCode, int sequenceNumber)
    {
      return apiClient.Endpoints.EventRegistrationPromoCodes.Get(orgCode, sequenceNumber);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<EventRegistrationPromoCodesModel> Search(string orgCode, string searchValue)
    {
      // Example: search by promo code value
      return apiClient.Endpoints.EventRegistrationPromoCodes.Search(
        orgCode,
        $"{nameof(EventRegistrationPromoCodesModel.Description)} eq '{searchValue}'");
    }

    /// <summary>
    /// A basic add example
    /// </summary>
    /// <param name="orgCode">Organization code</param>
    /// <param name="promoCode">The promo code value users will enter</param>
    /// <param name="eventId">The ID of the event the promo code is associated with</param>
    /// <param name="description">Description of the promo code</param>
    /// <param name="trackingResourceType">Tracking resource type</param>
    /// <param name="trackingResourceCode">Tracking resource code</param>
    /// <param name="discountType">The type of discount. Percent = 0, Fixed Amount = 1</param>
    /// <param name="amount">The discount percent amount</param>
    /// <returns>The newly created promo code</returns>
    public EventRegistrationPromoCodesModel Add(string orgCode, 
                                                string promoCode,
                                                string description, 
                                                int eventId,
                                                string trackingResourceType, 
                                                string trackingResourceCode, 
                                                int discountType, 
                                                decimal amount)
    {
      EventRegistrationPromoCodesModel model = new EventRegistrationPromoCodesModel
      {
        OrganizationCode = orgCode,
        PromotionalCode = promoCode,
        Description = description,
        EventID = eventId,
        TrackingResourceType = trackingResourceType,
        TrackingResourceCode = trackingResourceCode,
        DiscountType = discountType,
        DiscountPercent = amount,
        DiscountAmount = amount
      };

      return apiClient.Endpoints.EventRegistrationPromoCodes.Add(model);
    }

    /// <summary>
    /// A basic edit example
    /// </summary>
    /// <param name="orgCode">Organization code</param>
    /// <param name="sequenceNumber">Sequence number of the promo code</param>
    /// <param name="newDescription">New description for the promo code</param>
    /// <returns>Updated promo code object</returns>
    public EventRegistrationPromoCodesModel Edit(string orgCode, int sequenceNumber, string newDescription)
    {
      EventRegistrationPromoCodesModel model = apiClient.Endpoints.EventRegistrationPromoCodes.Get(orgCode, sequenceNumber);
      model.Description = newDescription;

      return apiClient.Endpoints.EventRegistrationPromoCodes.Update(model);
    }

    /// <summary>
    /// A basic edit example
    /// </summary>
    /// <param name="orgCode">Organization code</param>
    /// <param name="sequenceNumber">Sequence number of the promo code</param>
    /// <param name="newApplicableFunctions">New applicable functions for the promo code. Each should be a pipe separted pair of Resource Type and Resource Code</param>
    /// <returns>Updated promo code object</returns>
    public EventRegistrationPromoCodesModel EditAdvanced(string orgCode, int sequenceNumber)
    {
      EventRegistrationPromoCodesModel model = apiClient.Endpoints.EventRegistrationPromoCodes.Get(orgCode, sequenceNumber);

      model.Description = "Updated Description";
      model.DiscountType = 1; // 1 = Percentage, 2 = Amount
      model.DiscountPercent = 15.00m; // 15% discount
      model.DiscountAmount = 0.00m; // Clear out any existing amount discount
      model.ApplicableFunctions = "1500|HALL-A,1550|HALL-B"; // Comma separated list of Resource Type and Resource Code pairs on the registration functions promo code applies to
      model.ApplicableRegistrantTypes = ""; // Use a empty string to clear out existing values
      model.TrackingResourceType = "2000"; // Resource Type for tracking
      model.TrackingResourceCode = "ITEM"; // Resource Code for tracking

      return apiClient.Endpoints.EventRegistrationPromoCodes.Update(model);
    }

    /// <summary>
    /// A basic delete example
    /// </summary>
    public void Delete(string orgCode, int sequenceNumber)
    {
      apiClient.Endpoints.EventRegistrationPromoCodes.Delete(orgCode, sequenceNumber);
    }

    /// <summary>
    /// Adding existing master promo codes to an event
    /// </summary>
    /// <param name="orgCode">Organization Code</param>
    /// <param name="eventID">The event id to add the promo codes to</param>
    /// <param name="promoCodeSequenceNumbers">Comma delimited list of master promo code sequence numbers to add</param>
    public HttpResponseMessage AddExistingPromoCode(string orgCode, int eventID, string promoCodeSequenceNumbers)
    {
      AddExistingPromoCodeModel addExistingPromoCodeModel = new AddExistingPromoCodeModel
      {
        OrganizationCode = orgCode,
        EventID = eventID,
        PromoCodeSequenceNumbers = promoCodeSequenceNumbers,
      };

      return apiClient.Endpoints.EventRegistrationPromoCodes.AddExistingPromoCode(addExistingPromoCodeModel);
    }
  }
}