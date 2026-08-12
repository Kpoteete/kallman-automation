using System.Net.Http;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;
using System.Collections.Generic;
using System.Xml.Linq;

namespace Examples.Operations
{
  public class AlternateAddresses : Base
  {
    public AlternateAddresses(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<AlternateAddressesModel> Search(string orgCode, string searchValue)
    {
      return apiClient.Endpoints.AlternateAddresses.Search(orgCode, $"{nameof(AlternateAddressesModel.Account)} eq '{searchValue}'");
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary>
    public AlternateAddressesModel Get(string orgCode, string account, int sequenceNumber)
    {
      return apiClient.Endpoints.AlternateAddresses.Get(orgCode, account, sequenceNumber);
    }

    /// <summary>
    /// A basic add example
    /// </summary>    
    /// <param name="addressType">This will be the address type code that categorizes the type of Alternate Address being entered</param>
    /// <param name="city">This example will add just a city as an alternate address</param>    
    /// <returns></returns>
    public AlternateAddressesModel Add(string orgCode, string account, string city, string addressType)
    {
      var model = new AlternateAddressesModel
      {
        OrganizationCode = orgCode,
        Account = account,
        AddressType = addressType,
        City = city,
        Country = "***"
      };

      return apiClient.Endpoints.AlternateAddresses.Add(model);
    }

    /// <summary>
    /// A basic edit example
    /// </summary>
    /// <returns>The updated alternate address</returns>
    public AlternateAddressesModel Edit(string orgCode, string account, int sequenceNumber, string city)
    {
      var model = apiClient.Endpoints.AlternateAddresses.Get(orgCode, account, sequenceNumber);

      model.City = city;

      return apiClient.Endpoints.AlternateAddresses.Update(model);
    }

    /// <summary>
    /// A basic delete example
    /// </summary>    
    public void Delete(string orgCode, string account, int sequenceNumber)
    {
      apiClient.Endpoints.AlternateAddresses.Delete(orgCode, account, sequenceNumber);
    }

    /// <summary>
    /// If you wish to keep the address label and the address separate,
    /// you can use this option when calling the API for add or edit Alternate Addresses.
    /// This example will force an unsynced change between the address label and the address for fields 1 and 2.
    /// This is uncommon.
    /// </summary>
    /// <returns>The updated alternate address</returns>
    public AlternateAddressesModel EditWithoutSynchronization(string orgCode, string account, int sequenceNumber, string addressLine1, string addressLabel01, string addressLine2, string addressLabel02)
    {
      var model = apiClient.Endpoints.AlternateAddresses.Get(orgCode, account, sequenceNumber);

      model.AddressLine1 = addressLine1;
      model.AddressLabel01 = addressLabel01;
      model.AddressLine2 = addressLine2;
      model.AddressLabel02 = addressLabel02;

      var options = new Ungerboeck.Api.Models.Options.Subjects.AlternateAddresses() { KeepLabelsUnsynchronized = true };

      return apiClient.Endpoints.AlternateAddresses.Update(model, options);
    }

    /// <summary>
    /// This shows example values of every editable field for AlternateAddresses
    /// </summary>
    /// <param name="sequenceNumber">This is the identifying sequence number for the alternateaddress</param>
    /// <returns>The newly updated alternate address</returns>
    public AlternateAddressesModel EditAdvanced(bool individualAccount, string orgCode, string account, int sequenceNumber)
    {
      var alternateaddress = apiClient.Endpoints.AlternateAddresses.Get(orgCode, account, sequenceNumber);

      if (individualAccount)
      {
        alternateaddress = GenerateEditAlternateAddressForIndividual(alternateaddress);
      }
      else
      {
        alternateaddress = GenerateEditAlternateAddressForOrganization(alternateaddress);
      }

      return apiClient.Endpoints.AlternateAddresses.Update(alternateaddress);
    }

    /// <summary>
    /// This changes all possible writable fields on add.  
    /// Note the fields are different based on the account class.
    /// </summary>    
    public AlternateAddressesModel AddAdvanced(bool individualAccount, string organizationCode, string accountCode)
    {
      AlternateAddressesModel model;

      if (individualAccount)
      {
        model = GenerateAddAlternateAddressForIndividual(organizationCode, accountCode);
      }
      else
      {
        model = GenerateAddAlternateAddressForOrganization(organizationCode, accountCode);
      }

      return apiClient.Endpoints.AlternateAddresses.Add(model);
    }

    /// <summary>
    /// This function will make a model with all the fields filled in for an organization account.
    /// The only organization-only field is Name.
    /// </summary>
    /// <param name="organizationCode">The organization code of the account owning the alternate address.  This is usually 10 unless it was customized.</param>
    /// <param name="accountCode">The account code of the account owning the alternate address.  This is a code value up to 8 characters.</param>
    /// <returns>Filled in Alternate Address, ready to save</returns>
    public AlternateAddressesModel GenerateAddAlternateAddressForOrganization(string organizationCode, string accountCode)
    {
      AlternateAddressesModel model = GenerateAddAlternateAddress(organizationCode, accountCode);

      model.Name = "ACME Inc.";

      return model;
    }

    /// <summary>
    /// This function will make a model with all the fields filled in for an individual account
    /// </summary>
    /// <param name="organizationCode">The organization code of the account owning the alternate address.  This is usually 10 unless it was customized.</param>
    /// <param name="accountCode">The account code of the account owning the alternate address.  This is a code value up to 8 characters.</param>
    /// <returns>Filled in AlternateAddress, ready to save</returns>
    public AlternateAddressesModel GenerateAddAlternateAddressForIndividual(string organizationCode, string accountCode)
    {
      AlternateAddressesModel model = GenerateAddAlternateAddress(organizationCode, accountCode);

      model.FirstName = "John";
      model.CompanyName = "Acme Corp";
      model.LastName = "Doe";
      model.LastName2 = "Smith";
      model.MiddleInitial = "A";
      model.Title = "Architect";
      model.SecondTitle = "Chief";
      return model;
    }

    /// <summary>
    /// Example of how you fill in the various fields when adding an account
    /// </summary>
    /// <param name="organizationCode">The organization code of the account owning the alternate address.  This is usually 10 unless it was customized.</param>
    /// <param name="accountCode">The account code of the account owning the alternate address.  This is a code value up to 8 characters.</param>
    /// <returns>Filled in AlternateAddress, ready to save</returns>
    public AlternateAddressesModel GenerateAddAlternateAddress(string organizationCode, string accountCode)
    {
      return new AlternateAddressesModel
      {
        //All these fields are editable.  See existing entries for example values and codes.

        OrganizationCode = organizationCode,
        Account = accountCode,
        AddressType = "FWRD",
        Language = "FRA",
        AddressLabel01 = "123 Main St",
        AddressLabel02 = "Apt 4B",
        AddressLabel03 = "Springfield",
        AddressLabel04 = "IL",
        AddressLabel05 = "62704",
        AddressLabel06 = "USA",
        AddressLabel07 = "Office",
        AddressLabel08 = "456 Corporate Blvd",
        AddressLabel09 = "Suite 300",
        AddressLabel10 = "Business District",
        AddressLine1 = "123 Main St",
        AddressLine2 = "Apt 4B",
        AddressLine3 = "Springfield",
        AddressLine4 = "IL",
        AddressLine5 = "62704",
        AddressLine6 = "USA",
        City = "Springfield",
        Country = "***",
        PostalCode = "62704",
        State = "IL"
      };
    }

    /// <summary>
    /// This function will make a model with all the fields filled in for an organization account.
    /// The only organization-only field is Name.
    /// </summary>
    /// <param name="organizationCode">The organization code of the account owning the alternate address.  This is usually 10 unless it was customized.</param>
    /// <param name="accountCode">The account code of the account owning the alternate address.  This is a code value up to 8 characters.</param>
    /// <returns>Filled in AlternateAddress, ready to save</returns>
    public AlternateAddressesModel GenerateEditAlternateAddressForOrganization(AlternateAddressesModel model)
    {
      model = GenerateEditAlternateAddress(model);

      model.Name = "Global Co.";

      return model;
    }

    /// <summary>
    /// This function will make a model with all the fields filled in for an individual account.
    /// </summary>
    /// <param name="organizationCode">The organization code of the account owning the alternate address.  This is usually 10 unless it was customized.</param>
    /// <param name="accountCode">The account code of the account owning the alternate address.  This is a code value up to 8 characters.</param>
    /// <returns>Filled in Alternate Address, ready to save</returns>
    public AlternateAddressesModel GenerateEditAlternateAddressForIndividual(AlternateAddressesModel model)
    {
      model = GenerateEditAlternateAddress(model);

      model.FirstName = "Jane";
      model.CompanyName = "Global Corp";
      model.LastName = "Kay";
      model.LastName2 = "lowle";
      model.MiddleInitial = "R";
      model.Title = "Captain";
      model.SecondTitle = "Lead";
      return model;
    }

    /// <summary>
    /// This function will edit a model by changing all editable fields as an example.
    /// See existing entries for example values and codes.
    /// This function targets an individual account.
    /// </summary>
    public AlternateAddressesModel GenerateEditAlternateAddress(AlternateAddressesModel editAlternateAddress)
    {
      editAlternateAddress.AddressLine1 = "456 Oak St";
      editAlternateAddress.AddressLine2 = "Unit 2A";
      editAlternateAddress.AddressLine3 = "Metropolis";
      editAlternateAddress.AddressLine4 = "CA";
      editAlternateAddress.AddressLine5 = "90210";
      editAlternateAddress.AddressLine6 = "USA";
      editAlternateAddress.City = "Metropolis";
      editAlternateAddress.Country = "CHI";
      editAlternateAddress.PostalCode = "90210";
      editAlternateAddress.State = "CA";

      return editAlternateAddress;
    }


  }
}
