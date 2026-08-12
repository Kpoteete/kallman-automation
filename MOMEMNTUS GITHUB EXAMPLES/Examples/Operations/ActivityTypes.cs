using System.Net.Http;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;
using System.Collections.Generic;

namespace Examples.Operations
{
  public class ActivityTypes : Base
  {
    public ActivityTypes(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary> 
    public ActivityTypesModel Get(string orgCode, string code)
    {
      return apiClient.Endpoints.ActivityTypes.Get(orgCode, code);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<ActivityTypesModel> Search(string orgCode, string searchValue)
    {
      return apiClient.Endpoints.ActivityTypes.Search(orgCode, $"{nameof(ActivityTypesModel.Description)} eq '{searchValue}'");
    }

    /// <summary>
    /// A basic add example
    /// </summary>
    /// <param name="orgCode">Organization code CR774_ORG_CODE</param>
    /// <param name="code">Code CR774_DRY_TRC_TYPE</param>
    /// <param name="description">Description CR774_DRY_TRC_DESC</param>   
    public ActivityTypesModel Add(string orgCode, string code, string description)
    {
      var activityTypesOrder = new ActivityTypesModel
      {
        OrganizationCode = orgCode,
        Code = code,
        Description = description
      };

      return apiClient.Endpoints.ActivityTypes.Add(activityTypesOrder);
    }

    /// <summary>
    /// A basic delete example
    /// </summary>  
    public void Delete(string orgCode, string code)
    {
      apiClient.Endpoints.ActivityTypes.Delete(orgCode, code);
    }

    /// <summary>
    /// Basic Edit example.
    /// </summary>
    /// <param name="orgCode">Organization code of activity typet</param>
    /// <param name="code">Code of activity type</param>
    /// <param name="description">new Description of activity type</param>
    /// <returns>Updated activity type object</returns>
    public ActivityTypesModel Edit(string orgCode, string code, string description)
    {
      var activityTypes = apiClient.Endpoints.ActivityTypes.Get(orgCode, code);

      if (activityTypes != null)
      {
        activityTypes.Description = description;
      }

      return apiClient.Endpoints.ActivityTypes.Update(activityTypes);
    }

  }
}
