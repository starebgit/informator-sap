using System;
using System.Web.Http;

namespace InformatorSAP.Controllers
{
    [RoutePrefix("api/test")]
    public class TestController : ApiController
    {
        // Minimal test endpoint: GET /api/test/version
        [HttpGet]
        [Route("version")]
        public IHttpActionResult GetVersion()
        {
            return Ok(new
            {
                ok = true,
                message = "InformatorSAP deploy test v1",
                serverTimeUtc = DateTime.UtcNow
            });
        }
    }
}
