using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace Examples.Operations
{
  public class EventRegistrantTypes : Base
  {
    public EventRegistrantTypes(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary> 
    public EventRegistrantTypesModel Get(string orgCode, string type, int eventId)
    {
      return apiClient.Endpoints.EventRegistrantTypes.Get(orgCode, type, eventId);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<EventRegistrantTypesModel> Search(string orgCode, string searchValue)
    {
      return apiClient.Endpoints.EventRegistrantTypes.Search(orgCode, $"{nameof(EventRegistrantTypesModel.Description)} eq '{searchValue}'");
    }

    /// <summary>
    /// A basic add example
    /// </summary>
    /// <param name="orgCode">Organization Code ER131_ORG_CODE</param>
    /// <param name="type">Registrant Type ER131_REG_TYPE</param>
    /// <param name="eventId">Event Id ER131_EVT_ID</param>
    /// <param name="description"></param>
    /// <param name="resourceType"></param>
    /// <param name="resourceCode"></param>
    /// <param name="functionId"></param>
    /// <returns></returns>
    public EventRegistrantTypesModel Add(string orgCode, string type, int eventId, string description, string resourceType, string resourceCode, int functionId)
    {
      var EventRegistrantTypesOrder = new EventRegistrantTypesModel
      {
        OrganizationCode = orgCode,
        Type = type,
        EventID = eventId,
        Description = description,
        ResourceOrgCode = orgCode,
        ResourceCode = resourceCode,
        ResourceType = resourceType,
        Function = functionId,
        ItemDescription = description
      };

      return apiClient.Endpoints.EventRegistrantTypes.Add(EventRegistrantTypesOrder);
    }

    /// <summary>
    /// a basic delete example
    /// </summary>
    /// <param name="orgCode">Organization Code ER131_ORG_CODE</param>
    /// <param name="type">Registrant Type ER131_REG_TYPE</param>
    /// <param name="eventId">Event Id ER131_EVT_ID</param>
    public void Delete(string orgCode, string type, int eventId)
    {
      apiClient.Endpoints.EventRegistrantTypes.Delete(orgCode, type, eventId);
    }

    /// <summary>
    /// a basic edit example
    /// </summary>
    /// <param name="orgCode">Organization Code ER131_ORG_CODE</param>
    /// <param name="type">Registrant Type ER131_REG_TYPE</param>
    /// <param name="eventId">Event Id ER131_EVT_ID</param>
    /// <param name="description"></param>
    /// <returns></returns>
    public EventRegistrantTypesModel Edit(string orgCode, string type, int eventId, string description)
    {
      var EventRegistrantTypes = apiClient.Endpoints.EventRegistrantTypes.Get(orgCode, type, eventId);

      if (EventRegistrantTypes != null)
      {
        EventRegistrantTypes.Description = description;
      }

      return apiClient.Endpoints.EventRegistrantTypes.Update(EventRegistrantTypes);
    }

  }
}
