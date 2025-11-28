    using System.Web.Http;
    using InformatorSAP.Models;
    using InformatorSAP.Services;
    using System.Linq;

namespace InformatorSAP.Controllers
    {
        // Matches:  https://localhost:44330/api/orderdetails/...
        [RoutePrefix("api/orderdetails")]
        public class OrderDetailsController : ApiController
        {
            private readonly OrderDetailsService _orderDetailsService;

            public OrderDetailsController()
            {
                _orderDetailsService = new OrderDetailsService();
            }

        // Matches:  GET api/orderdetails/details?code=6440776
        [HttpGet]
        [Route("details")]
        public IHttpActionResult GetOrderDetails(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return BadRequest("Parameter 'code' is required.");
            }

            var sap = _orderDetailsService.GetOrderDetails(code);

            if (sap == null)
            {
                // No order found in SAP
                return NotFound();
            }

            // Shape the response so it looks like the Feathers "order" object
            var response = new
            {
                // top-level order fields – same names as Feathers
                id = sap.Code,                 // Feathers "orders.id"
                materialCode = sap.MaterialCode,
                dimension = sap.Dimension,
                name = sap.Name,
                quantity = sap.Quantity,
                code = sap.Code,
                startDate = sap.StartDate,
                scheduledEnddate = sap.ScheduledEnddate,

                // Feathers has createdAt / updatedAt from Sequelize – here we stub them for now
                createdAt = (string)null,
                updatedAt = (string)null,

                // uploads: same shape as Feathers "uploads" relation
                uploads = (sap.Uploads ?? new System.Collections.Generic.List<UploadDto>())
                    .Select(u => new
                    {
                        id = u.Id,
                        description = u.Description,
                        name = u.Name,
                        path = u.Path,
                        size = u.Size
                    })
                    .ToList(),

                // parts: same shape as Feathers "parts"
                parts = (sap.Parts ?? new System.Collections.Generic.List<PartDto>())
                    .Select((p, index) => new
                    {
                        id = index,           // synthetic id; frontend only needs it as a key
                        egoCode = p.EgoCode,
                        name = p.Name,
                        dimension = p.Dimension,
                        quantity = p.Quantity,
                        orderId = sap.Code    // to mimic FK
                    })
                    .ToList(),

                // operations: same shape as Feathers "operations",
                // including nested operation_machines
                operations = (sap.Operations ?? new System.Collections.Generic.List<OperationDto>())
                    .Select((o, opIndex) => new
                    {
                        id = opIndex,         // synthetic id
                        sequence = o.Sequence,
                        label = o.Label,
                        operationKey = o.OperationKey,
                        name = o.Name,
                        norm = o.Norm,
                        confirmation = o.Confirmation,
                        orderId = sap.Code,

                        // NOTE: property name must be exactly "operation_machines"
                        operation_machines = (o.Operation_Machines ?? new System.Collections.Generic.List<OperationMachineDto>())
                            .Select((m, mIndex) => new
                            {
                                id = mIndex,   // synthetic id
                                machineKey = m.MachineKey,
                                machineAltKey = m.MachineAltKey,
                                operationId = opIndex
                            })
                            .ToList()
                    })
                    .ToList()
            };

            return Ok(response);
        }

        // GET api/orderdetails/exists?code=6954963
        [HttpGet]
        [Route("exists")]
        public IHttpActionResult Exists(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return BadRequest("Parameter 'code' is required.");
            }

            var exists = _orderDetailsService.OrderExists(code);

            if (!exists)
                return NotFound();        // 404 → same semantics as before for "not found"

            return Ok(new { exists = true });   // body not really used, but nice to have
        }
    }
}