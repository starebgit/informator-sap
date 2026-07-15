using System;
using System.Web.Http;
using InformatorSAP.Models;
using InformatorSAP.Services;

namespace InformatorSAP.Controllers
{
    /// <summary>
    /// Per-SAP-graph targets ("cilji"). One current value per graph key. Read by every
    /// client to draw the target line; written only when Robi (zorjanr) sets a goal from the
    /// graph card (the "only Robi" gating is enforced in the frontend, matching the app's
    /// existing username-based gating).
    /// </summary>
    [RoutePrefix("api/graph-goals")]
    public class GraphGoalsController : ApiController
    {
        [HttpGet]
        [Route("")]
        public IHttpActionResult Get([FromUri] string key = null)
        {
            try
            {
                var service = new SapGraphGoalService();
                if (string.IsNullOrWhiteSpace(key))
                    return Ok(service.GetGoals());

                var goal = service.GetGoal(key.Trim());
                return Ok(goal); // null => no goal set yet
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }

        [HttpPost]
        [Route("")]
        public IHttpActionResult Set([FromBody] SetSapGraphGoalRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.GraphKey))
                return BadRequest("Field 'graphKey' is required.");
            if (!request.GoalValue.HasValue)
                return BadRequest("Field 'goalValue' is required.");

            try
            {
                var service = new SapGraphGoalService();
                var saved = service.UpsertGoal(
                    request.GraphKey.Trim(),
                    request.GoalValue.Value,
                    string.IsNullOrWhiteSpace(request.UpdatedBy) ? null : request.UpdatedBy.Trim());
                return Ok(saved);
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }
    }
}
