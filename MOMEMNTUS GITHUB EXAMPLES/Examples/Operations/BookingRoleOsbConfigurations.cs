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
  public class BookingRoleOsbConfigurations : Base
  {
    public BookingRoleOsbConfigurations(ApiClient apiClient) : base(apiClient)
    {
      
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary>  
    public BookingRoleOsbConfigurationsModel Get( int Id)
    {
      return apiClient.Endpoints.BookingRoleOsbConfigurations.Get(Id);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<BookingRoleOsbConfigurationsModel> Search(int searchValue)
    {
      return apiClient.Endpoints.BookingRoleOsbConfigurations.Search($"{nameof(BookingRoleOsbConfigurationsModel.OsbConfigurationId)} eq {searchValue}");
    }

    /// <summary>
    /// A basic add example for reference 
    /// </summary>  
    public BookingRoleOsbConfigurationsModel Add(int bookingRole,int osbConfig)
    {
      var bookingRoleOsbConfigurationsModel = new BookingRoleOsbConfigurationsModel
      {
       BookingRole = bookingRole,
       OsbConfigurationId = osbConfig
      };

      return apiClient.Endpoints.BookingRoleOsbConfigurations.Add(bookingRoleOsbConfigurationsModel);
    }

    /// <summary>
    /// A basic delete example
    /// </summary>    
    public void Delete(int Id)
    {
      apiClient.Endpoints.BookingRoleOsbConfigurations.Delete(Id);
    }
  }

  
}
