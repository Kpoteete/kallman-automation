using System.Net.Http;
using Ungerboeck.Api.Models.Options;
using Ungerboeck.Api.Models.Subjects;
using Ungerboeck.Api.Models.Search;

namespace Ungerboeck.Api.Sdk.Endpoints
{
  public class SpaceClasses : Base<SpaceClassesModel>
  {
    protected internal SpaceClasses(ApiClient api) : base(api) { }

    public new SearchResponse<SpaceClassesModel> Search(string orgCode, string searchOData, Search options = null)
    {
      return base.Search(orgCode, searchOData, options);
    }

    public SpaceClassesModel Get(int id, Ungerboeck.Api.Models.Options.Subjects.SpaceClasses options = null)
    {
      return base.Get(new { id }, options);
    }

    public SpaceClassesModel Update(SpaceClassesModel model, Ungerboeck.Api.Models.Options.Subjects.SpaceClasses options = null)
    {
      return base.Update(new { model.ID }, model, options);
    }

    public SpaceClassesModel Add(SpaceClassesModel model, Ungerboeck.Api.Models.Options.Subjects.SpaceClasses options = null)
    {
      return base.Add(model, options);
    }
  
    public HttpResponseMessage Delete(int id, Ungerboeck.Api.Models.Options.Subjects.SpaceClasses options = null)
    {
      return base.Delete(new { id }, options);
    }
  }
}
