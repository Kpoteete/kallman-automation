using System;
using System.Collections.Generic;
using System.Text;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models;
using System.Net.Http;
using Ungerboeck.Api.Models.Options;

namespace Ungerboeck.Api.Sdk.Endpoints
{
  /// <summary>
  /// Find endpoint calls for this subject here.
  /// </summary>
  public class RegistrationSetups : Base<RegistrationSetupsModel>
  {
    protected internal RegistrationSetups(ApiClient api) : base(api) { }

    /// <summary>
    /// Use this endpoint to search for a list of this subject.
    /// </summary>
    /// <param name="orgCode">Fill this with the Organization Code in which the search will take place.</param>
    /// <param name="searchOData">Fill this with OData to query for what you are looking for.  We highly suggest reading our 'Search Using the API' knowledge base article or Ungerboeck API Github examples to learn how to do this.</param>
    /// <param name="options">This contains optional configurations used for searching.</param>
    /// <returns>A list of this subject's model.</returns>
    public new Ungerboeck.Api.Models.Search.SearchResponse<RegistrationSetupsModel> Search(string orgCode, string searchOData, Search options = null)
    {
      return base.Search(orgCode, searchOData, options);
    }

    /// <summary>
    /// Use this endpoint to get a single entry of this subject with parameters.
    /// </summary>
    /// <param name="orgCode">Fill this with the Organization Code of the registration setup.</param>
    /// <param name="eventID">Fill this with the event ID of the registration setup.</param>
    /// <param name="options">This contains optional configurations.</param>
    /// <returns>A single model for this subject.</returns>
    public RegistrationSetupsModel Get(string orgCode, int eventID, Ungerboeck.Api.Models.Options.Subjects.RegistrationSetups options = null)
    {
      return base.Get(new { orgCode, eventID }, options);
    }

    /// <summary>
    /// Use this endpoint to edit a single entry of this subject.
    /// </summary>
    /// <param name="model">This should contain a filled model of this subject.  Note that any null model properties will be ignored for the save.</param>
    /// <param name="options">This contains optional configurations.</param>
    /// <returns>An updated, single model for this subject.</returns>
    public RegistrationSetupsModel Update(RegistrationSetupsModel model, Ungerboeck.Api.Models.Options.Subjects.RegistrationSetups options = null)
    {
      return base.Update(new { model.Organization, model.EventID }, model, options);
    }

    /// <summary>
    /// Use this endpoint to add a single entry of this subject.
    /// </summary>
    /// <param name="model">This should contain a filled model of this subject.  Note that any null model properties will be ignored for the save.</param>
    /// <param name="options">This contains optional configurations.</param>
    /// <returns>A newly added, single model for this subject.</returns>
    public RegistrationSetupsModel Add(RegistrationSetupsModel model, Ungerboeck.Api.Models.Options.Subjects.RegistrationSetups options = null)
    {
      return base.Add(model, options);
    }
  }
}
