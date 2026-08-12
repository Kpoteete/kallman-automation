using System;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;
using System.Net.Http;
using System.Threading.Tasks;

namespace Examples.Operations
{
  public class EventOpportunityDates : Base
  {
    public EventOpportunityDates(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary> 
    public EventOpportunityDatesModel Get(int id)
    {
      return apiClient.Endpoints.EventOpportunityDates.Get(id);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<EventOpportunityDatesModel> Search(string propertyToSearchBy, int searchValue)
    {
      return apiClient.Endpoints.EventOpportunityDates.Search(String.Empty, $"{propertyToSearchBy} eq {searchValue}");
    }

    /// <summary>
    /// A basic add example
    /// </summary>  
    public EventOpportunityDatesModel Add(string organization, int eventOpportunity, string description, DateTime meetingStartDate, DateTime meetingEndDate, DateTime housingStartDate, DateTime housingEndDate, int priority)
    {
      var eventOpportunityDatesModel = new EventOpportunityDatesModel
      {
        Description = description,
        EventOpportunity = eventOpportunity,
        OrganizationCode = organization,
        MeetingStartDate = meetingStartDate,
        MeetingEndDate = meetingEndDate,
        HousingStartDate = housingStartDate,
        HousingEndDate = housingEndDate,
        Priority = priority
      };

      return apiClient.Endpoints.EventOpportunityDates.Add(eventOpportunityDatesModel);
    }


    /// <summary>
    /// A basic edit example
    /// </summary> 
    public EventOpportunityDatesModel Edit(int id, string description)
    {
      EventOpportunityDatesModel meetingdates = apiClient.Endpoints.EventOpportunityDates.Get(id);
      meetingdates.Description = description;

      return apiClient.Endpoints.EventOpportunityDates.Update(meetingdates);
    }

    /// <summary>
    /// A basic delete example.
    /// </summary>
    public HttpResponseMessage Delete(int id)
    {
      return apiClient.Endpoints.EventOpportunityDates.Delete(id);
    }

  }
}