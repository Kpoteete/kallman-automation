using System;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace Examples.Operations
{
  public class FunctionItems : Base
  {
    public FunctionItems(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary> 
    public FunctionItemsModel Get(string orgCode, int sequenceNumber)
    {
      return apiClient.Endpoints.FunctionItems.Get(orgCode, sequenceNumber);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<FunctionItemsModel> Search(string orgCode, string searchValue)
    {
      return apiClient.Endpoints.FunctionItems.Search(orgCode, $"{nameof(FunctionItemsModel.ItemDescription)} eq '{searchValue}'");
    }

    /// <summary>
    /// A basic add example
    /// </summary>
    /// <param name="orgCode"></param>
    /// <param name="eventId"></param>
    /// <param name="functionId"></param>
    /// <param name="itemDescription"></param>
    /// <param name="resourceType"></param>
    /// <param name="resourceCode"></param>
    /// <returns></returns>
    public FunctionItemsModel Add(string orgCode, int eventId, int functionId, string itemDescription, string resourceType, string resourceCode)
    {
      FunctionItemsModel registrationFunctionsOrder = new FunctionItemsModel
      {
        OrganizationCode = orgCode,
        EventID = eventId,
        Function = functionId,
        ItemDescription = itemDescription,
        ResourceType = resourceType,
        ResourceCode = resourceCode
      };

      return apiClient.Endpoints.FunctionItems.Add(registrationFunctionsOrder);
    }

    /// <summary>
    /// A basic delete example
    /// </summary>
    /// <param name="orgCode"></param>
    /// <param name="sequenceNumber"></param>
    public void Delete(string orgCode, int sequenceNumber)
    {
      apiClient.Endpoints.FunctionItems.Delete(orgCode, sequenceNumber);
    }

    /// <summary>
    /// A basic edit example
    /// </summary>
    /// <param name="orgCode"></param>
    /// <param name="sequenceNumber"></param>
    /// <param name="description"></param>
    /// <returns></returns>
    /// <exception cref="System.Exception"></exception>
    public FunctionItemsModel Edit(string orgCode, int sequenceNumber, string description)
    {
      FunctionItemsModel registrationFunctions = apiClient.Endpoints.FunctionItems.Get(orgCode, sequenceNumber);

      if (registrationFunctions == null)
      {
        throw new System.Exception($"Registration Function with OrgCode {orgCode} and EventID {sequenceNumber}.");
      }

      registrationFunctions.ItemDescription = description;

      return apiClient.Endpoints.FunctionItems.Update(registrationFunctions);
    }

    /// <summary>
    /// A basic edit example with more fields
    /// </summary>
    /// <param name="orgCode"></param>
    /// <param name="sequenceNumber"></param>
    /// <param name="itemDescription"></param>
    /// <returns></returns>
    /// <exception cref="System.Exception"></exception>
    public FunctionItemsModel Edit_FunctionItemFields(string orgCode,
                                                         int sequenceNumber,
                                                         string itemDescription,
                                                         DateTime availableStartDate,
                                                         DateTime availableEndDate,
                                                         string availableForOnlineRegistration)
    {
      FunctionItemsModel registrationFunctions = apiClient.Endpoints.FunctionItems.Get(orgCode, sequenceNumber);

      if (registrationFunctions == null)
      {
        throw new System.Exception($"Registration Function with OrgCode {orgCode} and EventID {sequenceNumber}.");
      }

      registrationFunctions.ItemDescription = itemDescription;
      registrationFunctions.AvailableStartDate = availableStartDate;
      registrationFunctions.AvailableEndDate = availableEndDate;
      registrationFunctions.AvailableForOnlineRegistration = availableForOnlineRegistration;

      return apiClient.Endpoints.FunctionItems.Update(registrationFunctions);
    }

    /// <summary>
    /// A basic edit example with capacity fields
    /// </summary>
    /// <param name="orgCode"></param>
    /// <param name="sequenceNumber"></param>
    /// <param name="recordType"></param>
    /// <param name="maximumCapacityType"></param>
    /// <param name="capacityLimit"></param>
    /// <param name="capacity"></param>
    /// <param name="waitlistCapacityLimit"></param>
    /// <param name="waitlistCapacity"></param>
    /// <param name="maximumQuantity"></param>
    /// <returns></returns>
    /// <exception cref="System.Exception"></exception>
    public FunctionItemsModel Edit_FunctionItemCapacityFields(string orgCode,
                                                                 int sequenceNumber,
                                                                 string maximumCapacityType,
                                                                 int capacityLimit,
                                                                 int capacity,
                                                                 int waitlistCapacityLimit,
                                                                 int waitlistCapacity,
                                                                 int maximumQuantity)
    {
      FunctionItemsModel registrationFunctions = apiClient.Endpoints.FunctionItems.Get(orgCode, sequenceNumber);

      if (registrationFunctions == null)
      {
        throw new System.Exception($"Registration Function with OrgCode {orgCode} and EventID {sequenceNumber}.");
      }

      registrationFunctions.MaximumCapacityType = maximumCapacityType;
      registrationFunctions.CapacityLimit = capacityLimit;
      registrationFunctions.Capacity = capacity;
      registrationFunctions.WaitlistCapacity = waitlistCapacity;
      registrationFunctions.WaitlistCapacityLimit = waitlistCapacityLimit;
      registrationFunctions.ItemMaximumQuantity = maximumQuantity;

      return apiClient.Endpoints.FunctionItems.Update(registrationFunctions);
    }
  }
}
