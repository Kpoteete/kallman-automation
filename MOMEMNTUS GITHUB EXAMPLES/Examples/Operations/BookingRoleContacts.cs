using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace Examples.Operations
{
  public class BookingRoleContacts : Base
  {
    public BookingRoleContacts(ApiClient apiClient) : base(apiClient) { }

    public BookingRoleContactsModel Get(int id)
    {
      return apiClient.Endpoints.BookingRoleContacts.Get(id);
    }

    /// <summary>
    /// A search example.
    /// </summary>
    /// <returns></returns>
    public SearchResponse<BookingRoleContactsModel> Search(int searchValue)
    {
      return apiClient.Endpoints.BookingRoleContacts.Search($"{nameof(BookingRoleContactsModel.BookingRole)} eq {searchValue} ");
    }

    /// <summary>
    /// A basic add example
    /// </summary>
    /// <param name="orgCode">Organization's code </param>
    /// <param name="bookingRole">Booking role id</param>
    /// <param name="account">Account code</param>
    /// <returns></returns>
    public BookingRoleContactsModel Add(string orgCode, int bookingRole, string account)
    {
      BookingRoleContactsModel model = new BookingRoleContactsModel
      {
        BookingRole = bookingRole,
        Organization = orgCode,
        Account = account
      };

      return apiClient.Endpoints.BookingRoleContacts.Add(model);
    }

    /// <summary>
    /// A basic delete example
    /// </summary>
    /// <param name="id">id of booking role contact</param>
    public void Delete(int id)
    {
      apiClient.Endpoints.BookingRoleContacts.Delete(id);
    }
  }
}