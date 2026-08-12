using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;
using System;
using System.Threading.Tasks;
using Ungerboeck.Api.Models.Bulk;

namespace Examples.Operations
{
  public class GroupPropertyConfigurations : Base
  {
    public GroupPropertyConfigurations(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    ///</summary>
    public GroupPropertyConfigurationsModel Get(string orgCode, int sequenceNumber, string accountCode)
    {
      return apiClient.Endpoints.GroupPropertyConfigurations.Get(orgCode, sequenceNumber, accountCode);
    }

    /// <summary>
    /// A basic add example with the minimal required fields
    /// </summary> 
    public GroupPropertyConfigurationsModel Add(string orgCode, string account, DateTime groupCeilingDate, int groupCeiling)
    {
      var groupPropertyConfig = new GroupPropertyConfigurationsModel
      {
        OrgCode = orgCode,
        AccountCode = account,
        GroupCeilingDate = groupCeilingDate,
        GroupCeiling = groupCeiling
      };

      return apiClient.Endpoints.GroupPropertyConfigurations.Add(groupPropertyConfig);
    }

    /// <summary>
    /// A basic add example with a constructed Group Property Configuration Model object
    /// </summary> 
    public GroupPropertyConfigurationsModel Add(GroupPropertyConfigurationsModel groupPropertyConfig)
    {
      return apiClient.Endpoints.GroupPropertyConfigurations.Add(groupPropertyConfig);
    }

    /// <summary>
    /// A basic edit example with a constructed Group Property Configuration object
    /// </summary> 
    public GroupPropertyConfigurationsModel Edit(GroupPropertyConfigurationsModel groupPropertyConfigModel)
    {
      return apiClient.Endpoints.GroupPropertyConfigurations.Update(groupPropertyConfigModel);
    }

    /// <summary>
    /// A basic delete group property configuration example
    /// </summary>    
    public void Delete(string orgCode, int sequenceNumber, string accountCode)
    {
      apiClient.Endpoints.GroupPropertyConfigurations.Delete(orgCode, sequenceNumber, accountCode);
    }

    /// <summary>
    ///A search example.  Check out the 'Search using the API' knowledge base article for more info.
    ///</summary> 
    public SearchResponse<GroupPropertyConfigurationsModel> Search(string orgCode, string searchValue)
    {
      return apiClient.Endpoints.GroupPropertyConfigurations.Search(orgCode, $"AccountCode eq '{searchValue}'");
    }

    /// <summary>
    /// Example showing how to save multiple items of the same model at a time.  
    /// </summary>
    /// <param name="groupPropertyConfigurationsModel1">This contains a standard GroupPropertyConfigurationsModel object.  
    ///                                         A partial model mith missing properties is allowed.</param>
    /// <param name="bulkOperation1">Contains "create" or "update"</param>
    /// <param name="groupPropertyConfigurationsModel2">This contains a standard GroupPropertyConfigurationsModel object.  
    ///                                        A partial model mith missing properties is allowed.</param>
    /// <param name="bulkOperation2">Contains "create" or "update"</param>
    /// <param name="continueOnError">The bulk process is transactional and will save nothing if an item errors.  
    ///                               If you are submitting a list of unrelated items to save, set this as false and the bulk save process 
    ///                               will attempt to continue saving if an item fails to save.  Note that certain errors will always result 
    ///                               in the bulk operation halting.</param>
    /// <returns></returns>
    public BulkResponseModel Bulk(GroupPropertyConfigurationsModel groupPropertyConfigurationsModel1,
                                  string bulkOperation1,
                                  GroupPropertyConfigurationsModel groupPropertyConfigurationsModel2,
                                  string bulkOperation2,
                                  bool continueOnError)
    {
      /* If one item fails, you have the option to continue without it (see BulkRequestModel.ContinueOnError).  
       * Use this for large updates of unrelated items.
          -You can track items via the BulkItemIndex tracker.*/

      var myBulkRequest = new BulkRequestModel
      {
        ContinueOnError = continueOnError
      };

      var mybulkRequestItem = new BulkRequestItem()
      {
        UngerboeckModel = groupPropertyConfigurationsModel1, //This is a standard Ungerboeck Model found in our SDK
        Action = bulkOperation1, //contains "create" or "update".  Every item action can be independent.
        BulkItemIndex = 1 //Use this index to track items in the response.  Whether an item succeeds or fails, it will preserve the index you assign it.
      };

      myBulkRequest.BulkRequestItems.Add(mybulkRequestItem);

      mybulkRequestItem = new BulkRequestItem()
      {
        UngerboeckModel = groupPropertyConfigurationsModel2,
        Action = bulkOperation2,
        BulkItemIndex = 2
      };

      myBulkRequest.BulkRequestItems.Add(mybulkRequestItem);

      return apiClient.Endpoints.GroupPropertyConfigurations.Bulk(myBulkRequest);
    }

    /// <summary>
    /// You can do multiple save operations in one transaction.  See the API Help sandbox for a list of what has Bulk.
    /// </summary>
    /// <param name="bulkRequestModel">This contains the list of bulk items, as well as the choice to continue on failure of a save.</param>
    /// <returns>BuldResponseModel, containing the results of the bulk process</returns>
    public Task<BulkResponseModel> BulkAsync(GroupPropertyConfigurationsModel groupPropertyConfigurationsModel1,
                                             string bulkOperation1,
                                             GroupPropertyConfigurationsModel groupPropertyConfigurationsModel2,
                                             string bulkOperation2,
                                             bool continueOnError)
    {
      /* If one item fails, you have the option to continue without it (see BulkRequestModel.ContinueOnError).  
       * Use this for large updates of unrelated items.
          -You can track items via the BulkItemIndex tracker.*/

      var myBulkRequest = new BulkRequestModel
      {
        ContinueOnError = continueOnError
      };

      var mybulkRequestItem = new BulkRequestItem()
      {
        UngerboeckModel = groupPropertyConfigurationsModel1, //This is a standard Ungerboeck Model found in our SDK
        Action = bulkOperation1, //contains "create" or "update".  Every item action can be independent.
        BulkItemIndex = 1 //Use this index to track items in the response.  Whether an item succeeds or fails, it will preserve the index you assign it.
      };

      myBulkRequest.BulkRequestItems.Add(mybulkRequestItem);

      mybulkRequestItem = new BulkRequestItem()
      {
        UngerboeckModel = groupPropertyConfigurationsModel2,
        Action = bulkOperation2,
        BulkItemIndex = 2
      };

      myBulkRequest.BulkRequestItems.Add(mybulkRequestItem);

      return apiClient.Endpoints.GroupPropertyConfigurations.BulkAsync(myBulkRequest);

    }

  }
}
