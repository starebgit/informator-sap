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
        public IHttpActionResult Summary(string werks, string lgort, [FromUri] string query, [FromUri] bool includePlanned = true)
        {
            try
            {
                var service = new SapStockService();
                var result = service.GetUnrestrictedStockSummary(werks, lgort, query, includePlanned);
                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}
