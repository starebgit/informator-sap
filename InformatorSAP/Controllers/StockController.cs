using System;
using System.Web.Http;
using InformatorSAP.Services;

namespace InformatorSAP.Controllers
{
    [RoutePrefix("api/stock")]
    public class StockController : ApiController
    {
        [HttpGet]
        [Route("{werks}/{lgort}/summary")]
        public IHttpActionResult Summary(string werks, string lgort, [FromUri] string query)
        {
            try
            {
                var service = new SapStockService();
                var result = service.GetUnrestrictedStockSummary(werks, lgort, query);
                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}
