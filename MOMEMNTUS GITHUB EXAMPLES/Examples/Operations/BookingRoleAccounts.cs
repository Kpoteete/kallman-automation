using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace Examples.Operations
{
  public class BookingRoleAccounts : Base
  {
    public BookingRoleAccounts(ApiClient apiClient) : base(apiClient) { }

    public BookingRoleAccountsModel Get(int id)
    {
      return apiClient.Endpoints.BookingRoleAccounts.Get(id);
    }

    /// <summary>
    /// A search example.
    /// </summary>
    /// <returns></returns>
    public SearchResponse<BookingRoleAccountsModel> Search(int searchValue)
    {
      return apiClient.Endpoints.BookingRoleAccounts.Search($"{nameof(BookingRoleAccountsModel.BookingRole)} eq {searchValue} ");
    }

    /// <summary>
    /// A basic add example
    /// </summary>
    /// <param name="orgCode">Organization's code </param>
    /// <param name="bookingRole">Booking role id</param>
    /// <param name="account">Account code</param>
    /// <returns></returns>
    public BookingRoleAccountsModel Add(string orgCode, int bookingRole, string account)
    {
      BookingRoleAccountsModel model = new BookingRoleAccountsModel
      {
        BookingRole = bookingRole,
        Organization = orgCode,
        Account = account
      };

      return apiClient.Endpoints.BookingRoleAccounts.Add(model);
    }

    /// <summary>
    /// A basic delete example
    /// </summary>
    /// <param name="id">id of booking role account</param>
    public void Delete(int id)
    {
      apiClient.Endpoints.BookingRoleAccounts.Delete(id);
    }
  }
}