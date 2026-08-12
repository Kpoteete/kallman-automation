using System.Net.Http;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;
using System.Collections.Generic;
using System;

namespace Examples.Operations
{
  public class InventoryTransactions : Base
  {
    public InventoryTransactions(ApiClient apiClient) : base(apiClient)
    {
    }
    /// <summary>
    /// A basic retrieve example
    /// </summary>
    public InventoryTransactionsModel Get(int id)
    {
      return apiClient.Endpoints.InventoryTransactions.Get( id);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary>   
    public SearchResponse<InventoryTransactionsModel> Search(int searchValue)
    {
      return apiClient.Endpoints.InventoryTransactions.Search($"{nameof(InventoryTransactionsModel.FiscalYear)} eq {searchValue}");
    }

     
    public InventoryTransactionsModel CreateInventoryTransaction()
    {
      InventoryTransactionsModel model = new InventoryTransactionsModel()
      {
        OrganizationCode = "10",
        Date = Convert.ToDateTime("2024-04-11 00:00:00.000"),
        Item = "COKE24",
        TransactionType = "ISS",
        Units = 1,
        UM = "EA",
        Space = "EATERY",
        UnitCost = 50,
        Event = 21310
      };

      return apiClient.Endpoints.InventoryTransactions.Add(model);
    }
  }
}
