using System;
using System.Web.Http;
using InformatorSAP.Classes;
using InformatorSAP.Services;

namespace InformatorSAP.Controllers
{
    /// <summary>
    /// ZPP_0117 "Summary material document list" (MB51-style goods-movement report).
    /// </summary>
    [RoutePrefix("api/material-docs")]
    public class MaterialDocsController : ApiController
    {
        [HttpGet]
        [Route("")]
        public IHttpActionResult Get(
            [FromUri] string mjahr = null,
            [FromUri] string werks = "1061",
            [FromUri] string bukrs = "1060",
            [FromUri] string budatFrom = null,
            [FromUri] string budatTo = null,
            [FromUri] string matnr = null,
            [FromUri] string bwart = null,
            [FromUri] string lgort = null,
            [FromUri] string aufnr = null,
            [FromUri] string lifnr = null,
            [FromUri] string sobkz = null,
            [FromUri] string ebeln = null,
            [FromUri] string bklas = null,
            [FromUri] string prctr = null,
            [FromUri] string sobsl = null,
            [FromUri] bool summary = false,
            [FromUri] string lang = "SL")
        {
            if (string.IsNullOrWhiteSpace(mjahr))
                return BadRequest("Query parameter 'mjahr' is required (material document year).");

            var query = new MaterialDocQuery
            {
                Mjahr = mjahr,
                Werks = werks,
                Bukrs = bukrs,
                BudatFrom = budatFrom,
                BudatTo = budatTo,
                Matnr = matnr,
                Bwart = bwart,
                Lgort = lgort,
                Aufnr = aufnr,
                Lifnr = lifnr,
                Sobkz = sobkz,
                Ebeln = ebeln,
                Bklas = bklas,
                Prctr = prctr,
                Sobsl = sobsl,
                Summary = summary,
                Lang = lang
            };

            try
            {
                var service = new SapMaterialDocService();
                var result = service.GetMaterialMovements(query);
                return Ok(result);
            }
            catch (MaterialDocTooLargeException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }

        /// <summary>
        /// Izmet (scrap) vs SO (shop output) ratio per posting day, replicating the two
        /// pivot tables in "Izmet HP.xlsx" but daily instead of weekly. Classification is
        /// fixed: SO = BKLAS 4100 / BWART 101,102 / LGORT 0027; Izmet = BKLAS 3100 /
        /// BWART 551,552 / LGORT 0013,0016. Ratio = Izmet / SO.
        /// </summary>
        [HttpGet]
        [Route("izmet")]
        public IHttpActionResult GetIzmet(
            [FromUri] string mjahr = null,
            [FromUri] string werks = "1061",
            [FromUri] string bukrs = "1060",
            [FromUri] string budatFrom = null,
            [FromUri] string budatTo = null,
            [FromUri] string prctr = null,
            [FromUri] string lang = "SL")
        {
            if (string.IsNullOrWhiteSpace(mjahr))
                return BadRequest("Query parameter 'mjahr' is required (material document year).");

            var query = new IzmetRatioQuery
            {
                Mjahr = mjahr,
                Werks = werks,
                Bukrs = bukrs,
                BudatFrom = budatFrom,
                BudatTo = budatTo,
                Prctr = prctr,
                Lang = lang
            };

            try
            {
                var service = new SapMaterialDocService();
                var result = service.GetIzmetRatio(query);
                return Ok(result);
            }
            catch (MaterialDocTooLargeException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }
    }
}
