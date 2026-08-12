using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;

namespace Examples.Operations
{
  public class RegistrationSetups : Base
  {
    public RegistrationSetups(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary> 
    public RegistrationSetupsModel Get(string orgCode, int eventID)
    {
      return apiClient.Endpoints.RegistrationSetups.Get(orgCode, eventID);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<RegistrationSetupsModel> SearchByEvent(string orgCode, int eventID)
    {
      return apiClient.Endpoints.RegistrationSetups.Search(orgCode, $"{nameof(RegistrationSetupsModel.EventID)} eq {eventID}");
    }

    /// <summary>
    /// A search example using registration setup code.
    /// </summary> 
    public SearchResponse<RegistrationSetupsModel> SearchByCustomGuestFields(string orgCode, string customGuestFields)
    {
      return apiClient.Endpoints.RegistrationSetups.Search(orgCode, $"{nameof(RegistrationSetupsModel.CustomGuestFields)} eq '{customGuestFields}'");
    }

    /// <summary>
    /// A basic add example
    /// </summary>
    /// <param name="orgCode">Organization code</param>
    /// <param name="eventID">Event ID the registration setup is associated with</param>
    /// <returns>The newly created registration setup</returns>
    public RegistrationSetupsModel Add(string orgCode, int eventID)
    {
      RegistrationSetupsModel registrationSetup = new RegistrationSetupsModel
      {
        Organization = orgCode,
        EventID = eventID
      };

      return apiClient.Endpoints.RegistrationSetups.Add(registrationSetup);
    }

    /// <summary>
    /// A basic edit example
    /// </summary> 
    public RegistrationSetupsModel Edit(string orgCode, int eventID, int capacity)
    {
      RegistrationSetupsModel registrationSetup = apiClient.Endpoints.RegistrationSetups.Get(orgCode, eventID);
      registrationSetup.Capacity = capacity;

      return apiClient.Endpoints.RegistrationSetups.Update(registrationSetup);
    }
  }
}
