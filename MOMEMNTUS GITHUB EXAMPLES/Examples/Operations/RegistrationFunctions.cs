using System;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace Examples.Operations
{
  public class RegistrationFunctions : Base
  {
    public RegistrationFunctions(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary> 
    public RegistrationFunctionsModel Get(string orgCode, int eventID, int functionID)
    {
      return apiClient.Endpoints.RegistrationFunctions.Get(orgCode, eventID, functionID);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<RegistrationFunctionsModel> Search(string orgCode, string searchValue)
    {
      return apiClient.Endpoints.RegistrationFunctions.Search(orgCode, $"{nameof(RegistrationFunctionsModel.Description)} eq '{searchValue}'");
    }

    /// <summary>
    /// A basic add example
    /// </summary>
    /// <param name="orgCode">Organization</param>
    /// <param name="eventID">Event ID</param>
    /// <param name="functionDescription">Description</param>   
    /// <param name="statusCode">Status Code</param>
    public RegistrationFunctionsModel Add(string orgCode, int eventID, string functionDescription, string statusCode, string resource)
    {
      RegistrationFunctionsModel registrationFunctionsOrder = new RegistrationFunctionsModel
      {
        OrganizationCode = orgCode,
        EventID = eventID,
        Description = functionDescription,
        StatusCode = statusCode,
        StartDate = System.DateTime.Now,
        EndDate = System.DateTime.Now.AddDays(1),
        FunctionRecordType = "50",
        ItemRecordType = "80",
        Resource = resource
      };

      return apiClient.Endpoints.RegistrationFunctions.Add(registrationFunctionsOrder);
    }

    /// <summary>
    /// A basic add example
    /// </summary>
    /// <param name="orgCode">Organization</param>
    /// <param name="eventID">Event ID</param>
    /// <param name="functionDescription">Description</param>   
    /// <param name="statusCode">Status Code</param>
    public RegistrationFunctionsModel Add_AddOnlyFields(string orgCode, int eventID, string functionDescription, string statusCode, string resource, int linkedFunctionID, int displayOrder)
    {
      RegistrationFunctionsModel registrationFunctionsOrder = new RegistrationFunctionsModel
      {
        OrganizationCode = orgCode,
        EventID = eventID,
        Description = functionDescription,
        StatusCode = statusCode,
        StartDate = System.DateTime.Now,
        EndDate = System.DateTime.Now.AddDays(1),
        FunctionRecordType = "50",
        ItemRecordType = "80",
        LinkedFunctionID = linkedFunctionID,
        DisplayOrder = displayOrder,
        Resource = resource
      };

      return apiClient.Endpoints.RegistrationFunctions.Add(registrationFunctionsOrder);
    }

    /// <summary>
    /// A basic delete example
    /// </summary>  
    public void Delete(string orgCode, int eventID, int functionID)
    {
      apiClient.Endpoints.RegistrationFunctions.Delete(orgCode, eventID, functionID);
    }

    /// <summary>
    /// Basic Edit example.
    /// </summary>
    /// <param name="orgCode">Organization of Registration Function</param>
    /// <param name="eventID">Event ID of Registration Function</param>
    /// <param name="functionID">Function ID of Registration Function</param>
    /// <param name="description">New Description of Registration Function</param>
    /// <returns>Updated Registration Function object</returns>
    public RegistrationFunctionsModel Edit(string orgCode, int eventID, int functionID, string description)
    {
      RegistrationFunctionsModel registrationFunctions = apiClient.Endpoints.RegistrationFunctions.Get(orgCode, eventID, functionID);

      if (registrationFunctions == null)
      {
        throw new System.Exception($"Registration Function with OrgCode {orgCode} and EventID {eventID} and FunctionID {functionID} not found.");
      }

      registrationFunctions.Description = description;

      return apiClient.Endpoints.RegistrationFunctions.Update(registrationFunctions);
    }

    /// <summary>
    /// Basic Edit example.
    /// </summary>
    /// <param name="orgCode">Organization of Registration Function</param>
    /// <param name="eventID">Event ID of Registration Function</param>
    /// <param name="functionID">Function ID of Registration Function</param>
    /// <param name="description">New Description of Registration Function</param>
    /// <returns>Updated Registration Function object</returns>
    public RegistrationFunctionsModel Edit_FunctionFields(string orgCode, 
                                                          int eventID, 
                                                          int functionID, 
                                                          string description, 
                                                          string functionStatusCode, 
                                                          string functionClass,
                                                          string glAccount,
                                                          DateTime startDate,
                                                          DateTime endDate,
                                                          string checkRegistrationConflicts)
    {
      RegistrationFunctionsModel registrationFunctions = apiClient.Endpoints.RegistrationFunctions.Get(orgCode, eventID, functionID);

      if (registrationFunctions == null)
      {
        throw new System.Exception($"Registration Function with OrgCode {orgCode} and EventID {eventID} and FunctionID {functionID} not found.");
      }

      registrationFunctions.Description = description;
      registrationFunctions.StatusCode = functionStatusCode;
      registrationFunctions.Class = functionClass;
      registrationFunctions.FunctionGLCode = glAccount;
      registrationFunctions.StartDate = startDate;
      registrationFunctions.EndDate = endDate;
      registrationFunctions.CheckRegistrationConflicts = checkRegistrationConflicts;
      registrationFunctions.AlternateDescription2 = description;
      registrationFunctions.AlternateDescription3 = description;
      registrationFunctions.AlternateDescription4 = description;
      registrationFunctions.AlternateDescription5 = description;

      return apiClient.Endpoints.RegistrationFunctions.Update(registrationFunctions);
    }

    /// <summary>
    /// Example editing FunctionItems_ER103 join fields
    /// </summary>
    /// <param name="orgCode"></param>
    /// <param name="eventID"></param>
    /// <param name="functionID"></param>
    /// <param name="functionRecordType"></param>
    /// <param name="functionCapacityLimit"></param>
    /// <param name="functionCapacity"></param>
    /// <param name="functionWaitlistCapacityLimit"></param>
    /// <param name="functionWaitlistCapacity"></param>
    /// <param name="functionMaximumQuantity"></param>
    /// <returns></returns>
    public RegistrationFunctionsModel Edit_FunctionCapacityFields(string orgCode,
                                                                  int eventID,
                                                                  int functionID,
                                                                  string functionRecordType,
                                                                  string maximumCapacityType,
                                                                  int functionCapacityLimit,
                                                                  int functionCapacity,
                                                                  int functionWaitlistCapacityLimit,
                                                                  int functionWaitlistCapacity,
                                                                  int functionMaximumQuantity)
    {
      RegistrationFunctionsModel registrationFunctions = apiClient.Endpoints.RegistrationFunctions.Get(orgCode, eventID, functionID);

      if (registrationFunctions == null)
      {
        throw new System.Exception($"Registration Function with OrgCode {orgCode} and EventID {eventID} and FunctionID {functionID} not found.");
      }

      registrationFunctions.FunctionRecordType = functionRecordType;
      registrationFunctions.MaximumCapacityType = maximumCapacityType;
      registrationFunctions.FunctionCapacityLimit = functionCapacityLimit;
      registrationFunctions.FunctionCapacity = functionCapacity;
      registrationFunctions.FunctionWaitlistCapacity = functionWaitlistCapacity;
      registrationFunctions.FunctionWaitlistCapacityLimit = functionWaitlistCapacityLimit;
      registrationFunctions.FunctionMaximumQuantity = functionMaximumQuantity;

      return apiClient.Endpoints.RegistrationFunctions.Update(registrationFunctions);
    }

    public RegistrationFunctionsModel Edit_ItemCapacityFields(string orgCode,
                                                              int eventID,
                                                              int functionID,
                                                              string itemRecordType,
                                                              string maximumCapacityType,
                                                              int itemCapacityLimit,
                                                              int itemCapacity,
                                                              int itemWaitlistCapacityLimit,
                                                              int itemWaitlistCapacity,
                                                              int itemMaximumQuantity,
                                                              string supplier,
                                                              string availableForOnlineRegistration,
                                                              decimal? credit,
                                                              DateTime availableStartDate,
                                                              DateTime availableEndDate)
    {
      RegistrationFunctionsModel registrationFunctions = apiClient.Endpoints.RegistrationFunctions.Get(orgCode, eventID, functionID);
      if (registrationFunctions == null)
      {
        throw new System.Exception($"Registration Function with OrgCode {orgCode} and EventID {eventID} and FunctionID {functionID} not found.");
      }

      registrationFunctions.ItemRecordType = itemRecordType;
      registrationFunctions.MaximumCapacityType = maximumCapacityType;
      registrationFunctions.ItemCapacityLimit = itemCapacityLimit;
      registrationFunctions.ItemCapacity = itemCapacity;
      registrationFunctions.ItemWaitlistCapacityLimit = itemWaitlistCapacityLimit;
      registrationFunctions.ItemWaitlistCapacity = itemWaitlistCapacity;
      registrationFunctions.ItemMaximumQuantity = itemMaximumQuantity;
      registrationFunctions.Supplier = supplier;
      registrationFunctions.AvailableForOnlineRegistration = availableForOnlineRegistration;
      registrationFunctions.Credit = credit;
      registrationFunctions.AvailableStartDate = availableStartDate;
      registrationFunctions.AvailableEndDate = availableEndDate;

      return apiClient.Endpoints.RegistrationFunctions.Update(registrationFunctions);
    }
  }
}
