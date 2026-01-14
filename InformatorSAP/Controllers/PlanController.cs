using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Http;
using InformatorSAP.Classes;
using InformatorSAP.Services;

namespace InformatorSAP.Controllers
{
    [RoutePrefix("api/plan")]
    public class PlanController : ApiController
    {
        /// GET /api/plan/workcenters?plant=1061&ted=401&term=navijanje&take=200
        [HttpGet]
        [Route("workcenters")]
        public IHttpActionResult GetWorkCenters(
            [FromUri] string plant = "1061",
            [FromUri] string ted = null,
            [FromUri] string term = null,
            [FromUri] int take = 500)
        {
            try
            {
                var service = new SapService();
                var list = service.FindWorkCenters(plant, ted, term, rowCount: Math.Max(take * 5, 1000));
                if (take > 0) list = list.Take(take).ToList();
                return Ok(list);
            }
            catch (Exception ex)
            {
                return InternalServerError(new Exception("Failed to fetch work centers: " + ex.Message));
            }
        }

        [HttpGet]
        [Route("orders-by-workcenter")]
        public IHttpActionResult GetOrdersByWorkCenter(
            [FromUri] string plant = "1061",
            [FromUri] string workCenter = null,   // e.g., 2144V201
            [FromUri] string language = "SL",
            [FromUri] int take = 500)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(workCenter))
                    return BadRequest("Missing required parameter: workCenter");

                var service = new SapService();
                var rows = service.GetOrdersByWorkCenter(workCenter, plant, language, take);
                return Ok(rows);
            }
            catch (Exception ex)
            {
                return InternalServerError(new Exception("Failed to fetch orders by work center: " + ex.Message));
            }
        }

        // GET /api/plan/order-operation-status?order=6935105&workCenter=2144V201&plant=1061&language=SL&material=000011224542370005&vornr=0001
        [HttpGet]
        [Route("order-operation-status")]
        public IHttpActionResult GetOrderOperationStatus(
            [FromUri] string order,
            [FromUri] string workCenter,
            [FromUri] string plant = "1061",
            [FromUri] string language = "SL",
            [FromUri] string material = null,
            [FromUri] string vornr = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(order))
                    return BadRequest("Missing required parameter: order");
                if (string.IsNullOrWhiteSpace(workCenter))
                    return BadRequest("Missing required parameter: workCenter");

                var service = new SapService();
                var status = service.GetOperationStatusForOrderWorkCenter(order, workCenter, plant, language, material, vornr);

                if (status == null) return NotFound();
                return Ok(status);
            }
            catch (Exception ex)
            {
                return InternalServerError(new Exception("Failed to fetch operation status: " + ex.Message));
            }
        }
        // GET /api/plan/open-orders-by-material?material=...&plant=1061&language=SL&take=50&includeDisplayInfo=true
        [HttpGet]
        [Route("open-orders-by-material")]
        public IHttpActionResult GetOpenOrdersByMaterial(
            [FromUri] string material,
            [FromUri] string plant = "1061",
            [FromUri] string language = "SL",
            [FromUri] int take = 50,
            [FromUri] bool includeDisplayInfo = false)   // ← NEW
        {
            try
            {
                if (string.IsNullOrWhiteSpace(material))
                    return BadRequest("Missing required parameter: material");

                plant = string.IsNullOrWhiteSpace(plant) ? "1061" : plant.Trim();
                language = string.IsNullOrWhiteSpace(language) ? "SL" : language.Trim();

                var service = new SapService();
                List<OpenOrderId> orders =
                    service.GetOpenOrdersForMaterial(material, plant, language, take, includeDisplayInfo); // ← pass flag
                return Ok(orders ?? new List<OpenOrderId>());
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }
    }
}




