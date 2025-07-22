using System.Collections.Generic;
using System.Web.Http;
using InformatorSAP.Services;

namespace InformatorSAP.Controllers
{
    [RoutePrefix("api/materials")]
    public class MaterialsController : ApiController
    {
        [HttpGet]
        [Route("search")]
        public IHttpActionResult Search([FromUri] string term)
        {
            var service = new SapService();
            List<MaterialDto> results = service.SearchMaterials(term, "SL", "1061");
            return Ok(results);
        }
    }
}
