using System;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;
using System.Net.Http;

namespace Examples.Operations
{
  public class BookingRoleTypes : Base
  {
    public BookingRoleTypes(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<BookingRoleTypesModel> Search(string searchValue)
    {
      return apiClient.Endpoints.BookingRoleTypes.Search($"{nameof(BookingRoleTypesModel.ID)} eq {searchValue}");
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary> 
    public BookingRoleTypesModel Get(int id)
    {
      return apiClient.Endpoints.BookingRoleTypes.Get(id);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<BookingRoleTypesModel> Search(string propertyToSearchBy, int searchValue)
    {
      return apiClient.Endpoints.BookingRoleTypes.Search($"{propertyToSearchBy} eq {searchValue}");
    }

    /// <summary>
    /// A basic add example
    /// </summary>  
    public BookingRoleTypesModel Add(string description)
    {
      var bookingRoleType = new BookingRoleTypesModel
      {
        Description = description
      };

      return apiClient.Endpoints.BookingRoleTypes.Add(bookingRoleType);
    }

    /// <summary>
    /// A basic edit example
    /// </summary> 
    public BookingRoleTypesModel Edit(int id, string newDescription)
    {
      var bookingRoleType = apiClient.Endpoints.BookingRoleTypes.Get(id);
      bookingRoleType.Description = newDescription;

      return apiClient.Endpoints.BookingRoleTypes.Update(bookingRoleType);
    }
  }
}
