using System;
using System.Web.Http;
using InformatorSAP.Services;

namespace InformatorSAP.Controllers
{
    [RoutePrefix("api/orders")]
    public class OrdersController : ApiController
    {
        [HttpGet]
        [Route("total-quantity")]
        public IHttpActionResult GetTotalQuantity([FromUri] string orderNumber)
        {
            if (string.IsNullOrWhiteSpace(orderNumber))
                return BadRequest("Missing orderNumber parameter.");

            var service = new SapService();
            var (quantityStr, unit, deliveredStr, deliveredUnit) =
                service.GetTotalOrderQuantityWithUnit(orderNumber);

            if (quantityStr == null) return NotFound();

            if (!decimal.TryParse(quantityStr, out var qtyRaw))
                return InternalServerError(new Exception("Could not parse quantity to number."));

            // parse delivered if present; default to 0
            decimal deliveredRaw = 0m;
            if (!string.IsNullOrWhiteSpace(deliveredStr))
                decimal.TryParse(deliveredStr, out deliveredRaw);

            // SAP QUAN fields are scaled by 1000
            var quantity = qtyRaw / 1000m;
            var delivered = deliveredRaw / 1000m;

            return Ok(new
            {
                Quantity = quantity,
                Unit = unit,
                Delivered = delivered,
                DeliveredUnit = string.IsNullOrWhiteSpace(deliveredUnit) ? unit : deliveredUnit
            });
        }

        [HttpGet]
        [Route("components")]
        public IHttpActionResult GetComponents([FromUri] string orderNumber)
        {
            if (string.IsNullOrWhiteSpace(orderNumber))
                return BadRequest("Missing orderNumber parameter.");

            var service = new SapService();
            var components = service.GetOrderComponents(orderNumber);

            if (components == null || components.Count == 0)
                return NotFound();

            return Ok(components);
        }

        [HttpGet]
        [Route("operations")]
        public IHttpActionResult GetOperations([FromUri] string orderNumber)
        {
            if (string.IsNullOrWhiteSpace(orderNumber))
                return BadRequest("Missing orderNumber parameter.");

            var service = new SapService();
            var list = service.GetOrderOperationsSummary(orderNumber);
            return Ok(list); // empty list = no ops found
        }

    }
}
