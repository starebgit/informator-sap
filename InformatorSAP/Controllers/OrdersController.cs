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
            var (quantityStr, unit) = service.GetTotalOrderQuantityWithUnit(orderNumber);

            if (quantityStr == null)
                return NotFound();

            if (!decimal.TryParse(quantityStr, out var quantity))
                return InternalServerError(new Exception("Could not parse quantity to number."));

            quantity = quantity / 1000;

            return Ok(new
            {
                Quantity = quantity,
                Unit = unit
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

    }
}
