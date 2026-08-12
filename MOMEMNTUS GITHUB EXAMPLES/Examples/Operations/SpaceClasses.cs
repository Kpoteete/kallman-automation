using System;
using Ungerboeck.Api.Models.Search;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Sdk;

namespace Examples.Operations
{
  public class SpaceClasses : Base
  {
    public SpaceClasses(ApiClient apiClient) : base(apiClient) { }

    /// <summary>
    /// A search example.
    /// </summary>
    /// <returns>Space Class model which matches search criteria.</returns>
    public SearchResponse<SpaceClassesModel> Search(string propertyToSearchBy, string searchValue)
    {
      return apiClient.Endpoints.SpaceClasses.Search(string.Empty, $"{propertyToSearchBy} eq '{searchValue}'");
    }

    /// <summary>
    /// A [GET] example.
    /// </summary>
    /// <returns>Space class model with given Id.</returns>
    public SpaceClassesModel Get(int Id)
    {
      return apiClient.Endpoints.SpaceClasses.Get(Id);
    }

    /// <summary>
    /// A [POST] example with only required properties.
    /// </summary>
    /// <param name="model">Space Class model which must be added.</param>
    /// <returns>Added Space Class model.</returns>
    public SpaceClassesModel Add(SpaceClassesModel model)
    {
      return apiClient.Endpoints.SpaceClasses.Add(model);
    }

    /// <summary>
    /// A [PUT] example.
    /// </summary>
    /// <param name="id">Unique identifier of the entity which must be updated.</param>
    /// <param name="updatedModel">Space Class model with updated properties.</param>
    /// <returns></returns>
    public SpaceClassesModel Edit(int id, SpaceClassesModel updatedModel)
    {
      SpaceClassesModel model = apiClient.Endpoints.SpaceClasses.Get(id);

      model.Description = updatedModel.Description;

      return apiClient.Endpoints.SpaceClasses.Update(model);
    }

    /// <summary>
    /// A [DELETE] example.
    /// </summary>
    /// <param name="id">Unique identifier of the Space Class record which must be deleted.</param>
    public void Delete(int id)
    {
      apiClient.Endpoints.SpaceClasses.Delete(id);
    }

    /// <summary>
    /// Factory method which created Space Class entity with unique ID and Description properties.
    /// </summary>
    /// <returns>Instance of SpaceClassModel.</returns>
    public SpaceClassesModel GenerateNewSpaceClass()
    {
      return new SpaceClassesModel()
      {
        ID = new Random().Next(minValue:0, int.MaxValue),
        Description = "UniqueDescription" + DateTime.Now.Ticks.ToString()
      };
    }
  }
}
