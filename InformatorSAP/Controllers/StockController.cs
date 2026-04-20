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
        [Route("snapshots")]
        public IHttpActionResult Snapshots(
            [FromUri] string werks = null,
            [FromUri] string unitId = null,
            [FromUri] string latestPerTerm = null,
            [FromUri] DateTime? from = null,
            [FromUri] DateTime? to = null,
            [FromUri] string lgort = null)
        {
            if (string.IsNullOrWhiteSpace(werks))
            {
                return BadRequest("Query parameter 'werks' is required.");
            }

            if (string.IsNullOrWhiteSpace(unitId))
            {
                return BadRequest("Query parameter 'unitId' is required.");
            }

            int parsedUnitId;
            if (!int.TryParse(unitId, out parsedUnitId))
            {
                return BadRequest("Query parameter 'unitId' must be a valid integer.");
            }

            bool parsedLatestPerTerm;
            if (string.IsNullOrWhiteSpace(latestPerTerm) || !bool.TryParse(latestPerTerm, out parsedLatestPerTerm))
            {
                return BadRequest("Query parameter 'latestPerTerm' is required and must be 'true' or 'false'.");
            }

            if (from.HasValue && to.HasValue && from.Value > to.Value)
            {
                return BadRequest("Query parameter 'from' must be less than or equal to 'to'.");
            }

            if (!parsedLatestPerTerm && (!from.HasValue || !to.HasValue))
            {
                return BadRequest("Query parameters 'from' and 'to' are required when latestPerTerm=false.");
            }

            try
            {
                var service = new StockSnapshotService();
                var rows = service.GetSnapshots(
                    werks,
                    parsedUnitId,
                    parsedLatestPerTerm,
                    from,
                    to,
                    lgort);

                return Ok(rows);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }


        [HttpGet]
        [Route("snapshots/by-date")]
        public IHttpActionResult SnapshotsByDate([FromUri] DateTime date, [FromUri] int? unitId = null, [FromUri] string werks = null, [FromUri] string lgort = null)
        {
            try
            {
                var service = new StockSnapshotService();
                var rows = service.GetSnapshotsForDate(date, unitId, werks, lgort);
                return Ok(rows);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet]
        [Route("{werks}/{lgort}/summary")]
        public IHttpActionResult Summary(string werks, string lgort, [FromUri] string query, [FromUri] string exactText = null, [FromUri] bool includePlanned = true)
        {
            try
            {
                var service = new SapStockService();
                var result = service.GetUnrestrictedStockSummary(werks, lgort, query, exactText, includePlanned);
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
