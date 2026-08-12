using System;

namespace StartHere
{
  class Program
  {
    static void Main(string[] args)
    {
      string ungerboeckURI;
      string apiUserId;
      string secret;
      string key;

      ungerboeckURI = "https://YourUngerboeckSite.ungerboeck.com";      
      apiUserId = "SOMEAPIID"; //This is the API User ID value found on the API User details window.
      secret = string.Empty; //Supply the API Secret at runtime. Do not commit a real value.
      key = string.Empty; //Supply the API Key at runtime. Do not commit a real value.

      if (ungerboeckURI == "https://YourUngerboeckSite.ungerboeck.com" ||
          string.IsNullOrWhiteSpace(secret) ||
          string.IsNullOrWhiteSpace(key))
        throw new Exception("Please start by supplying the API User info without committing credentials.");

      var auth = new Ungerboeck.Api.Models.Authorization.Jwt
      {
        APIUserID = apiUserId,
        Secret = secret,
        Key = key,
        UngerboeckURI = ungerboeckURI,
        AutoRefresh = new Ungerboeck.Api.Models.Authorization.AutoRefresh()
      };

      var client = new Ungerboeck.Api.Sdk.ApiClient(auth);

      var examples = new Examples.Operations.Accounts(client); //You can and should call the sdk directly in your app, but this is just showing how to use our example demos      

      var account = examples.Get("10", "ACCTCODE");

      Console.WriteLine($"The account name is {account.Name}");
    }
  }
}
