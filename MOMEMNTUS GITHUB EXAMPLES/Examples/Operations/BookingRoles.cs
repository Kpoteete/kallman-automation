using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace Examples.Operations
{
  public class BookingRoles : Base
  {
    public BookingRoles(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary>  
    public BookingRolesModel Get(int Id)
    {
      return apiClient.Endpoints.BookingRoles.Get(Id);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<BookingRolesModel> Search( int searchValue)
    {
      return apiClient.Endpoints.BookingRoles.Search($"{nameof(BookingRolesModel.RoleType)} eq {searchValue}");
    }

    /// <summary>
    /// A basic add example for reference 
    /// </summary>  
    public BookingRolesModel Add(string description, string active, int roleType, string organizationCode)
    {
      var bookingRolesModel = new BookingRolesModel
      {
       Description = description,
       Active = "Y",
       RoleType = roleType,
       OrganizationCode = organizationCode
      };

      return apiClient.Endpoints.BookingRoles.Add(bookingRolesModel);
    }

    /// <summary>
    /// A basic delete example
    /// </summary>    
    public void Delete(int Id)
    {
      apiClient.Endpoints.BookingRoles.Delete(Id);
    }

    /// <summary>
    /// A basic edit example for Numeric ID
    /// </summary> 
    public BookingRolesModel Edit(int id, int roleId)
    {
      BookingRolesModel bookingRolesModel = apiClient.Endpoints.BookingRoles.Get(id);
      bookingRolesModel.RoleType = roleId;

      return apiClient.Endpoints.BookingRoles.Update(bookingRolesModel);
    }
  }
}
