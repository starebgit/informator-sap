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

        public class BulkCodesRequest { public List<string> Codes { get; set; } }

        [HttpPost]
        [Route("bulk-info")]
        public IHttpActionResult BulkInfo([FromBody] BulkCodesRequest req)
        {
            if (req?.Codes == null || req.Codes.Count == 0)
                return BadRequest("Body must contain { codes: string[] }.");

            var service = new SapService();
            var list = service.GetMaterialsInfoBulk(req.Codes, "SL", "1061");
            return Ok(list);
        }
    }
}
