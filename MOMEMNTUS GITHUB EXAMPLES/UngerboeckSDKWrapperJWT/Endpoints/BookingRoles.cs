using System;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;
using System.Net.Http;
using Ungerboeck.Api.Models.Options;


namespace Ungerboeck.Api.Sdk.Endpoints
{
  public class BookingRoles : Base<BookingRolesModel>
  {
    public BookingRoles(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// Use this endpoint to search for a list of this subject.
    /// </summary>
    /// <param name="searchMetadata">After searching, this will contain search info, such as ResultsTotal.  If your search resulted in more than one page, this will also be filled with API links to navigate pages.</param>
    /// <param name="searchOData">Fill this with OData to query for what you are looking for.  We highly suggest reading our 'Search Using the API' knowledge base article or Ungerboeck API Github examples to learn how to do this. </param>
    /// <param name="options">This contains optional configurations used for searching.</param>
    /// <returns>A list of this subject's model.</returns>
    public new Ungerboeck.Api.Models.Search.SearchResponse<BookingRolesModel> Search( string searchOData, Search options = null)
    {
      return base.Search(null, searchOData);
    }

    /// <summary>
    /// Use this endpoint to get a single entry of this subject with parameters.
    /// </summary>
    /// <param name="options">This contains optional configurations.</param>
    /// <returns>A single model for this subject.</returns>
    public BookingRolesModel Get(int Id, Ungerboeck.Api.Models.Options.Subjects.BookingRoles options = null)
    {
      return base.Get(new { Id }, options);
    }

    /// <summary>
    /// Use this endpoint to add a single entry of this subject.
    /// </summary>
    /// <param name="model">This should contain a filled model of this subject.  Note that any null model properties will be ignored for the save.</param>
    /// <param name="options">This contains optional configurations.</param>
    /// <returns>A newly added, single model for this subject.</returns>
    public BookingRolesModel Add(BookingRolesModel model, Ungerboeck.Api.Models.Options.Subjects.BookingRoles options = null)
    {
      return base.Add(model, options);
    }

    /// <summary>
    /// Use this endpoint to delete a single entry of this subject.
    /// </summary>
    /// <param name="options">This contains optional configurations.</param>
    /// <returns>Nothing if successful.</returns>
    public HttpResponseMessage Delete(int Id, Ungerboeck.Api.Models.Options.Subjects.BookingRoles options = null)
    {
      return base.Delete(new { Id }, options);
    }

    /// <summary>
    /// Use this endpoint to edit a single entry of this subject.
    /// </summary>
    /// <param name="model">This should contain a filled model of this subject.  Note that any null model properties will be ignored for the save.</param>
    /// <param name="options">This contains optional configurations.</param>
    /// <returns>An updated, single model for this subject.</returns>
    
    public BookingRolesModel Update(BookingRolesModel model, Ungerboeck.Api.Models.Options.Subjects.BookingRoles options = null)
    {
      return base.Update(new { model.ID, }, model, options);
    }

  }
}
