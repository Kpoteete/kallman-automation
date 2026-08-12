using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Models.Subjects;

namespace Ungerboeck.Api.Sdk.Endpoints
{
  public class GroupPropertyConfigurations : Base<GroupPropertyConfigurationsModel>
  {
    public GroupPropertyConfigurations(ApiClient api) : base(api) { }

    /// <summary>
    /// Use this endpoint to search for a list of this subject.
    /// </summary>
    ///<param name="orgCode">Fill this with the Organization Code in which the search will take place.</param>
    ///<param name="searchOData">Fill this with OData to query for what you are looking for.  
    /// We highly suggest reading our 'Search Using the API' knowledge base article or Ungerboeck API Github examples to learn how to do this. </param>
    ///<param name="options">This contains optional configurations used for searching.</param>
    /// <returns>A list of this subject's model.</returns>
    public new Models.Search.SearchResponse<GroupPropertyConfigurationsModel> Search(string orgCode, string searchOData, Search options = null)
    {
      return base.Search(orgCode, searchOData, options);
    }

    ///<summary>
    ///Use this endpoint to get a single entry of this subject with parameters.
    ///</summary>
    ///<param name="options">This contains optional configurations.</param>
    /// <returns>A single model for this subject.</returns>
    public GroupPropertyConfigurationsModel Get(string orgCode, int sequenceNumber, string accountCode, Models.Options.Subjects.GroupPropertyConfigurations options = null)
    {
      return base.Get(new { orgCode, sequenceNumber, accountCode }, options);
    }

    /// <summary>
    /// Use this endpoint to edit a single entry of this subject.
    /// </summary>
    /// <param name="model">This should contain a filled model of this subject.  Note that any null model properties will be ignored for the save.</param>
    /// <param name="options">This contains optional configurations.</param>
    /// <returns>An updated, single model for this subject.</returns>
    public GroupPropertyConfigurationsModel Update(GroupPropertyConfigurationsModel model, Models.Options.Subjects.GroupPropertyConfigurations options = null)
    {
      return base.Update(new { model.OrgCode, model.SequenceNumber, model.AccountCode }, model, options);
    }

    /// <summary>
    /// Use this endpoint to add a single entry of this subject.
    /// </summary>
    /// <param name="model">This should contain a filled model of this subject.  Note that any null model properties will be ignored for the save.</param>
    /// <param name="options">This contains optional configurations.</param>
    /// <returns>A newly added, single model for this subject.</returns>
    public GroupPropertyConfigurationsModel Add(GroupPropertyConfigurationsModel model, Models.Options.Subjects.GroupPropertyConfigurations options = null)
    {
      return base.Add(model, options);
    }

    /// <summary>
    /// Use this endpoint to delete a single entry of this subject.
    /// </summary>
    /// <param name="options">This contains optional configurations.</param>
    /// <returns>Nothing if successful.</returns>
    public HttpResponseMessage Delete(string orgCode, int sequenceNumber, string accountCode, Models.Options.Subjects.GroupPropertyConfigurations options = null)
    {
      return base.Delete(new { orgCode, sequenceNumber, accountCode }, options);
    }

    /// <summary>
    /// You can do multiple save operations in one transaction.  See the API Help sandbox for a list of what has Bulk.
    /// </summary>
    /// <param name="bulkRequestModel">This contains the list of bulk items, as well as the choice to continue on failure of a save.</param>
    /// <returns>BuldResponseModel, containing the results of the bulk process</returns>
    public Models.Bulk.BulkResponseModel Bulk(Models.Bulk.BulkRequestModel bulkRequestModel, 
                                              Models.Options.Subjects.GroupPropertyConfigurations options = null)
    {
      return base.BulkSync(bulkRequestModel, options);
    }

    /// <summary>
    /// You can do multiple save operations in one transaction.  See the API Help sandbox for a list of what has Bulk.
    /// </summary>
    /// <param name="bulkRequestModel">This contains the list of bulk items, as well as the choice to continue on failure of a save.</param>
    /// <returns>BuldResponseModel, containing the results of the bulk process</returns>
    public Task<Models.Bulk.BulkResponseModel> BulkAsync(Models.Bulk.BulkRequestModel bulkRequestModel, 
                                                         Models.Options.Subjects.GroupPropertyConfigurations options = null)
    {
      return base.BulkAsync(bulkRequestModel, options);
    }
  }
}
