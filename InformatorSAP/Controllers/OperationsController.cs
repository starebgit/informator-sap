using System;
using System.Collections.Generic;
using System.Web.Http;
using InformatorSAP.Services;


namespace InformatorSAP.Controllers
{
    [RoutePrefix("api/operations")]
    public class OperationsController : ApiController  
    {
        [HttpGet]
        [Route("{orderNumber}/operation-confirmations")]
        public IHttpActionResult GetOperationConfirmations(string orderNumber)
        {
            try
            {
                var svc = new SapService();
                var data = svc.GetOperationConfirmations(orderNumber)
                           ?? new List<OperationConfirmationDto>();
                return Ok(data);
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }
    }
}
