using System.Net.Http;
using Ungerboeck.Api.Sdk;
using Ungerboeck.Api.Models;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;
using System.Collections.Generic;

namespace Examples.Operations
{
  public class RegistrantTypes : Base
  {
    public RegistrantTypes(ApiClient apiClient) : base(apiClient)
    {
    }

    /// <summary>
    /// A basic retrieve example
    /// </summary> 
    public RegistrantTypesModel Get(string orgCode, string regTypeCode)
    {
      return apiClient.Endpoints.RegistrantTypes.Get(orgCode, regTypeCode);
    }

    /// <summary>
    /// A search example.  Check out the 'Search using the API' knowledge base article for more info.
    /// </summary> 
    public SearchResponse<RegistrantTypesModel> Search(string orgCode, string searchValue)
    {
      return apiClient.Endpoints.RegistrantTypes.Search(orgCode, $"{nameof(RegistrantTypesModel.Description)} eq '{searchValue}'");
    }

    /// <summary>
    /// A basic add example
    /// </summary>
    /// <param name="orgCode">Organization</param>
    /// <param name="regTypeCode">Code</param>
    /// <param name="description">Description</param>   
    public RegistrantTypesModel Add(string orgCode, string regTypeCode, string description)
    {
      RegistrantTypesModel registrantTypesOrder = new RegistrantTypesModel
      {
        OrganizationCode = orgCode,
        Code = regTypeCode,
        Description = description
      };

      return apiClient.Endpoints.RegistrantTypes.Add(registrantTypesOrder);
    }

    /// <summary>
    /// A basic delete example
    /// </summary>  
    public void Delete(string orgCode, string regTypeCode)
    {
      apiClient.Endpoints.RegistrantTypes.Delete(orgCode, regTypeCode);
    }

    /// <summary>
    /// Basic Edit example.
    /// </summary>
    /// <param name="orgCode">Organization of Registrant Type</param>
    /// <param name="regTypeCode">Code of Registrant Type</param>
    /// <param name="description">New Description of Registrant Type</param>
    /// <returns>Updated Registrant type object</returns>
    public RegistrantTypesModel Edit(string orgCode, string regTypeCode, string description)
    {
      RegistrantTypesModel registrantTypes = apiClient.Endpoints.RegistrantTypes.Get(orgCode, regTypeCode);

      if (registrantTypes == null)
      {
        throw new System.Exception($"Registrant Type with OrgCode {orgCode} and Code {regTypeCode} not found.");
      }
      
      registrantTypes.Description = description;   

      return apiClient.Endpoints.RegistrantTypes.Update(registrantTypes);
    }

    /// <summary>
    /// Edit Alternate Descriptions of Registrant Type example.
    /// </summary>
    /// <param name="orgCode">Organization of Registrant Type</param>
    /// <param name="regTypeCode">Code of Registrant Type</param>
    /// <param name="description1">AlternateDescription1 of Registrant Type</param>
    /// <param name="description2">AlternateDescription2 of Registrant Type</param>
    /// <param name="description3">AlternateDescription3 of Registrant Type</param>
    /// <param name="description4">AlternateDescription4 of Registrant Type</param>
    /// <param name="description5">AlternateDescription5 of Registrant Type</param>
    /// <returns>Updated Registrant type object</returns>
    public RegistrantTypesModel EditAlternateDescriptions(string orgCode, string regTypeCode, string description1, string description2, string description3, string description4, string description5)
    {
      RegistrantTypesModel registrantTypes = apiClient.Endpoints.RegistrantTypes.Get(orgCode, regTypeCode);

      if (registrantTypes == null)
      {
        throw new System.Exception($"Registrant Type with OrgCode {orgCode} and Code {regTypeCode} not found.");
      }

      registrantTypes.AlternateDescription1 = description1;
      registrantTypes.AlternateDescription2 = description2;
      registrantTypes.AlternateDescription3 = description3;
      registrantTypes.AlternateDescription4 = description4;
      registrantTypes.AlternateDescription5 = description5;

      return apiClient.Endpoints.RegistrantTypes.Update(registrantTypes);
    }

    /// <summary>
    /// Edit Retire of Registrant Type example.
    /// </summary>
    /// <param name="orgCode">Organization of Registrant Type </param>
    /// <param name="regTypeCode">Code of Registrant Type</param>
    /// <param name="retire">Retired. 'Y' for Yes, 'N' for No </param>
    /// <returns></returns>
    /// <exception cref="System.Exception"></exception>
    public RegistrantTypesModel EditRetired(string orgCode, string regTypeCode, string retire)
    {
      RegistrantTypesModel registrantTypes = apiClient.Endpoints.RegistrantTypes.Get(orgCode, regTypeCode);
      if (registrantTypes == null)
      {
        throw new System.Exception($"Registrant Type with OrgCode {orgCode} and Code {regTypeCode} not found.");
      }
      registrantTypes.Retired = retire;
      return apiClient.Endpoints.RegistrantTypes.Update(registrantTypes);
    }

    /// <summary>
    /// Edit IsGuest of Registrant Type example.
    /// </summary>
    /// <param name="orgCode">Organization of Registrant Type </param>
    /// <param name="regTypeCode">Code of Registrant Type</param>
    /// <param name="isGuest">IsGuest. 1 for Yes, 0 for No </param>
    /// <returns></returns>
    /// <exception cref="System.Exception"></exception>
    public RegistrantTypesModel EditIsGuest(string orgCode, string regTypeCode, int isGuest)
    {
      RegistrantTypesModel registrantTypes = apiClient.Endpoints.RegistrantTypes.Get(orgCode, regTypeCode);
      if (registrantTypes == null)
      {
        throw new System.Exception($"Registrant Type with OrgCode {orgCode} and Code {regTypeCode} not found.");
      }
      registrantTypes.IsGuest = isGuest;
      return apiClient.Endpoints.RegistrantTypes.Update(registrantTypes);
    }

    /// <summary>
    /// Edit Resource of Registrant Type example.
    /// </summary>
    /// <param name="orgCode">Organization of Registrant Type </param>
    /// <param name="regTypeCode">Code of Registrant Type</param>
    /// <param name="resourceCode">Resource Code of Registrant Type</param>
    /// <param name="resourceType">ResourceType of Registrant Type</param>
    /// <returns></returns>
    /// <exception cref="System.Exception"></exception>
    public RegistrantTypesModel EditResource(string orgCode, string regTypeCode, string resourceCode, string resourceType)
    {
      RegistrantTypesModel registrantTypes = apiClient.Endpoints.RegistrantTypes.Get(orgCode, regTypeCode);
      if (registrantTypes == null)
      {
        throw new System.Exception($"Registrant Type with OrgCode {orgCode} and Code {regTypeCode} not found.");
      }
      registrantTypes.ResourceCode = resourceCode;
      registrantTypes.ResourceType = resourceType;
      return apiClient.Endpoints.RegistrantTypes.Update(registrantTypes);
    }

  }
}
