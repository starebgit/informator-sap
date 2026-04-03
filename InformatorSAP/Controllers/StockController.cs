using System;
using System.Web.Http;
using InformatorSAP.Services;

namespace InformatorSAP.Controllers
{
    [RoutePrefix("api/stock")]
    public class StockController : ApiController
    {


        [HttpPost]
        [Route("snapshots/refresh")]
        public IHttpActionResult RefreshSnapshots([FromUri] bool includePlanned = true)
        {
            try
            {
                var service = new StockSnapshotService();
                var processed = service.RefreshNightlySnapshots(includePlanned);
                return Ok(new { ProcessedTerms = processed, RefreshedAtUtc = DateTime.UtcNow });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet]
        [Route("snapshots/latest")]
        public IHttpActionResult LatestSnapshots([FromUri] string werks = null, [FromUri] string lgort = null, [FromUri] int? unitId = null)
        {
            try
            {
                var service = new StockSnapshotService();
                var rows = service.GetLatestSnapshots(werks, lgort, unitId);
                return Ok(rows);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

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
