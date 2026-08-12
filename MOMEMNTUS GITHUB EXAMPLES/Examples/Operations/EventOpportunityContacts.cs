using System;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;
using System.Net.Http;
using System.Threading.Tasks;

namespace Examples.Operations
{
  public class EventOpportunityContacts : Base
  {
    public EventOpportunityContacts(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary> 
    public EventOpportunityContactsModel Get(int id)
    {
      return apiClient.Endpoints.EventOpportunityContacts.Get(id);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<EventOpportunityContactsModel> Search(string propertyToSearchBy, int searchValue)
    {
      return apiClient.Endpoints.EventOpportunityContacts.Search(String.Empty, $"{propertyToSearchBy} eq {searchValue}");
    }

    /// <summary>
    /// A basic add example
    /// </summary>  
    public EventOpportunityContactsModel Add(string organizationCode, int eventOpportunity, string account, string contact, int type)
    {
      var myEventOpportunityContact = new EventOpportunityContactsModel
      {
        OrganizationCode = organizationCode,
        EventOpportunity = eventOpportunity,
        Account = account,
        Type = type,
        Contact= contact
      };

      return apiClient.Endpoints.EventOpportunityContacts.Add(myEventOpportunityContact);
    }

    /// <summary>
    /// A basic edit example
    /// </summary> 
    public EventOpportunityContactsModel Edit(int id, string account)
    {
      var myEventOpportunityContact = apiClient.Endpoints.EventOpportunityContacts.Get(id);
      myEventOpportunityContact.Account = account;

      return apiClient.Endpoints.EventOpportunityContacts.Update(myEventOpportunityContact);
    }

    /// <summary>
    /// A basic delete example.
    /// </summary>
    public HttpResponseMessage Delete(int id)
    {
      return apiClient.Endpoints.EventOpportunityContacts.Delete(id);
    }

    public EventOpportunityContactsModel GenerateEditEventOpportunityContact(EventOpportunityContactsModel editEventOpportunityContact)
    {
      //All these fields are editable.  See existing entries for example values and codes.
      var newEditedEventOpportunityContact = new EventOpportunityContactsModel
      {
        ID = editEventOpportunityContact.ID,
        Account = "00017379",
        Contact = "00019667",
        EventOpportunity = 1462,
        Type = 13,
      };

      return newEditedEventOpportunityContact;
    }

    /// <summary>
    /// This changes all possible writable fields on edit
    /// </summary>
    public EventOpportunityContactsModel EditAdvanced(EventOpportunityContactsModel editEventOpportunityContact)
    {
      editEventOpportunityContact = GenerateEditEventOpportunityContact(editEventOpportunityContact);

      return apiClient.Endpoints.EventOpportunityContacts.Update(editEventOpportunityContact);
    }
  }
}
