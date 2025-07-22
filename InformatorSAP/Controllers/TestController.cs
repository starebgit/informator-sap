using System.Collections.Generic;
using System.Linq;
using System.Web.Http;
using InformatorSAP.Services;

namespace InformatorSAP.Controllers
{
    public class TestController : ApiController
    {
        [HttpGet]
        [Route("api/test/search")]
        public IHttpActionResult Search([FromUri] string term)
        {
            var service = new SapService();
            List<MaterialDto> results = service.SearchMaterials(term, "SL", "1061");
            return Ok(results);

        }
    }
}
