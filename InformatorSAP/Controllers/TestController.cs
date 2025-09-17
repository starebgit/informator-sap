using System;
using System.Collections.Generic;
using System.Web.Http;
using InformatorSAP.Services;

namespace InformatorSAP.Controllers
{
    [RoutePrefix("api/test")]
    public class TestController : ApiController
    {
        // NEW: simple test endpoint → /api/test?msg=hello
        [HttpGet]
        [Route("")]
        public IHttpActionResult Ping([FromUri] string msg = null)
        {
            return Ok(new
            {
                ok = true,
                message = string.IsNullOrWhiteSpace(msg) ? "test successful 🎯" : $"test successful: {msg}",
                serverTimeUtc = DateTime.UtcNow
            });
        }

        // Existing search, now under /api/test/search
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
