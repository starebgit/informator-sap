using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.Linq;
using InformatorSAP.Classes;
using SAP.Middleware.Connector;

namespace InformatorSAP.Services
{
    public class SapService : IDestinationConfiguration
    {
        private static bool isRegistered = false;
        private static readonly object _lock = new object();  // add once

        public SapService()
        {
            if (!isRegistered)
            {
                lock (_lock)              // ⇐ forces single-thread entry
                {
                    if (!isRegistered)    // double-check after the lock
                    {
                        RfcDestinationManager.RegisterDestinationConfiguration(this);
                        isRegistered = true;
                    }
                }
            }
        }

        public RfcConfigParameters GetParameters(string destinationName)
        {
            if (destinationName == "INFORMATOR_SAP")
            {
                string sapUser = ConfigurationManager.AppSettings["SapUser"];
                string sapPassword = ConfigurationManager.AppSettings["SapPassword"];

                return new RfcConfigParameters
                {
                    { RfcConfigParameters.Name, "INFORMATOR_SAP" },
                    { RfcConfigParameters.AppServerHost, "sape4p.blanc-fischer.com" },
                    { RfcConfigParameters.SystemNumber, "10" },
                    { RfcConfigParameters.Client, "101" },
                    { RfcConfigParameters.User, sapUser },
                    { RfcConfigParameters.Password, sapPassword },
                    { RfcConfigParameters.Language, "EN" }
                };
            }
            return null;
        }

        public bool ChangeEventsSupported() => false;
        public event RfcDestinationManager.ConfigurationChangeHandler ConfigurationChanged;

        public List<MaterialDto> SearchMaterials(string search, string language = "SL", string plant = "1061")
        {

            var dest = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            var repo = dest.Repository;
            var results = new List<string>();

            // Map language to SAP SPRAS codes
            string sprasCode;
            switch (language.ToUpper())
            {
                case "SL":
                    sprasCode = "5";
                    break;
                case "EN":
                    sprasCode = "E";
                    break;
                default:
                    sprasCode = language.ToUpper();
                    break;
            }

            // Search MAKT (material descriptions)
            var readTable = repo.CreateFunction("RFC_READ_TABLE");
            readTable.SetValue("QUERY_TABLE", "MAKT");
            readTable.SetValue("DELIMITER", "|");
            readTable.SetValue("ROWCOUNT", 100);

            var fields = readTable.GetTable("FIELDS");
            fields.Append(); fields.SetValue("FIELDNAME", "MATNR");
            fields.Append(); fields.SetValue("FIELDNAME", "MAKTX");
            fields.Append(); fields.SetValue("FIELDNAME", "SPRAS");

            string up = search.ToUpperInvariant();
            string lo = search.ToLowerInvariant();

            var options = readTable.GetTable("OPTIONS");
            options.Append(); options.SetValue("TEXT", $"MATNR LIKE  '%{search}%'");
            options.Append(); options.SetValue("TEXT", $"OR MAKTX LIKE '%{up}%'");
            options.Append(); options.SetValue("TEXT", $"OR MAKTX LIKE '%{lo}%'");

            readTable.Invoke(dest);

            var data = readTable.GetTable("DATA");

            var matchedLines = new List<MaterialDto>();

            foreach (IRfcStructure row in data)
            {
                string line = row.GetString("WA");

                // Language is always the last field because of field order
                var parts = line.Split('|');
                if (parts.Length < 3) continue;

                string matnr = parts[0].Trim();
                string maktx = parts[1].Trim();
                string spras = parts[2].Trim();

                if (matnr.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    maktx.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matchedLines.Add(new MaterialDto
                    {
                        Code = matnr,
                        Description = maktx,
                        Language = spras
                    });
                }
            }

            // Try filtering by requested language
            var filtered = matchedLines
                .Where(x => x.Language.Equals(sprasCode, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Fallback to English only if Slovenian not found
            if (filtered.Count == 0 && language.ToUpper() != "EN")
            {
                return SearchMaterials(search, "EN", plant);
            }

            return filtered;
        }
        // CHANGE SIGNATURE (add Delivered + DeliveredUnit, keep strings for minimal controller churn)
        public (string Quantity, string Unit, string Delivered, string DeliveredUnit)
            GetTotalOrderQuantityWithUnit(string orderNumber)
        {
            var dest = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            var repo = dest.Repository;
            string aufnr = orderNumber.PadLeft(12, '0');

            // --- AFKO: total order quantity + unit ---
            var fAfko = repo.CreateFunction("RFC_READ_TABLE");
            fAfko.SetValue("QUERY_TABLE", "AFKO");
            fAfko.SetValue("DELIMITER", "|");
            fAfko.SetValue("ROWCOUNT", 1);

            var fldsAfko = fAfko.GetTable("FIELDS");
            fldsAfko.Append(); fldsAfko.SetValue("FIELDNAME", "GAMNG");
            fldsAfko.Append(); fldsAfko.SetValue("FIELDNAME", "GMEIN");

            var optAfko = fAfko.GetTable("OPTIONS");
            optAfko.Append(); optAfko.SetValue("TEXT", $"AUFNR = '{aufnr}'");

            fAfko.Invoke(dest);

            var dAfko = fAfko.GetTable("DATA");
            if (dAfko.Count == 0) return (null, null, null, null);

            var afkoLine = dAfko[0].GetString("WA").Split('|');
            var qtyStr = afkoLine.Length > 0 ? afkoLine[0].Trim() : null;
            var unitStr = afkoLine.Length > 1 ? afkoLine[1].Trim() : null;

            // --- AFPO: delivered (goods receipt) quantity + unit ---
            var fAfpo = repo.CreateFunction("RFC_READ_TABLE");
            fAfpo.SetValue("QUERY_TABLE", "AFPO");
            fAfpo.SetValue("DELIMITER", "|");
            fAfpo.SetValue("ROWCOUNT", 1);

            var fldsAfpo = fAfpo.GetTable("FIELDS");
            fldsAfpo.Append(); fldsAfpo.SetValue("FIELDNAME", "WEMNG"); // delivered qty
            fldsAfpo.Append(); fldsAfpo.SetValue("FIELDNAME", "MEINS"); // unit

            var optAfpo = fAfpo.GetTable("OPTIONS");
            optAfpo.Append(); optAfpo.SetValue("TEXT", $"AUFNR = '{aufnr}'");

            fAfpo.Invoke(dest);

            var dAfpo = fAfpo.GetTable("DATA");
            string deliveredStr = null, deliveredUnitStr = null;
            if (dAfpo.Count > 0)
            {
                var afpoLine = dAfpo[0].GetString("WA").Split('|');
                deliveredStr = afpoLine.Length > 0 ? afpoLine[0].Trim() : null;
                deliveredUnitStr = afpoLine.Length > 1 ? afpoLine[1].Trim() : null;
            }

            return (qtyStr, unitStr, deliveredStr, deliveredUnitStr);
        }

        public List<ComponentDto> GetOrderComponents(string orderNumber)
        {
            var dest = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            var repo = dest.Repository;

            var readTable = repo.CreateFunction("RFC_READ_TABLE");
            readTable.SetValue("QUERY_TABLE", "RESB");
            readTable.SetValue("DELIMITER", "|");

            var fields = readTable.GetTable("FIELDS");
            fields.Append(); fields.SetValue("FIELDNAME", "MATNR");  // Component
            fields.Append(); fields.SetValue("FIELDNAME", "BDMNG");  // Quantity
            fields.Append(); fields.SetValue("FIELDNAME", "MEINS");  // Unit
            fields.Append(); fields.SetValue("FIELDNAME", "LGORT");  // Storage Location

            var options = readTable.GetTable("OPTIONS");
            options.Append(); options.SetValue("TEXT", $"AUFNR = '{orderNumber.PadLeft(12, '0')}'");

            readTable.Invoke(dest);

            var data = readTable.GetTable("DATA");
            var results = new List<ComponentDto>();

            foreach (IRfcStructure row in data)
            {
                try
                {
                    var line = row.GetString("WA");
                    var parts = line.Split('|');
                    if (parts.Length < 4) continue;

                    var matnr = parts[0].Trim();
                    var quantityStr = parts[1].Trim();
                    var unit = parts[2].Trim();
                    var lgort = parts[3].Trim();

                    if (!decimal.TryParse(quantityStr, out var quantity))
                        continue;

                    // Fetch available quantity from MARD (LABST)
                    decimal? availableQuantity = null;

                    var stockRead = repo.CreateFunction("RFC_READ_TABLE");
                    stockRead.SetValue("QUERY_TABLE", "MARD");
                    stockRead.SetValue("DELIMITER", "|");
                    stockRead.SetValue("ROWCOUNT", 1);

                    var stockFields = stockRead.GetTable("FIELDS");
                    stockFields.Append(); stockFields.SetValue("FIELDNAME", "LABST");

                    var stockOptions = stockRead.GetTable("OPTIONS");
                    stockOptions.Append(); stockOptions.SetValue("TEXT", $"MATNR = '{matnr}'");
                    stockOptions.Append(); stockOptions.SetValue("TEXT", $"AND WERKS = '1061'");
                    stockOptions.Append(); stockOptions.SetValue("TEXT", $"AND LGORT = '{lgort}'");

                    stockRead.Invoke(dest);

                    var stockData = stockRead.GetTable("DATA");
                    if (stockData.Count > 0)
                    {
                        var stockLine = stockData[0].GetString("WA");
                        if (decimal.TryParse(stockLine.Trim(), out var parsedQty))
                        {
                            availableQuantity = parsedQty;
                        }
                    }

                    results.Add(new ComponentDto
                    {
                        Component = matnr,
                        QuantityRequired = quantity / 1000m,
                        Unit = unit,
                        StorageLocation = lgort,
                        AvailableQuantity = availableQuantity / 1000m
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Error parsing row: " + ex.Message);
                    continue;
                }
            }

            return results;
        }
        // TODO: Consider optimizing stock lookup with batch RFC_READ_TABLE if performance becomes an issue

        public List<OperationSummaryDto> GetOrderOperationsSummary(string orderNumber)
        {
            var dest = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            var repo = dest.Repository;
            string aufnr = orderNumber.PadLeft(12, '0');

            // AFKO → AUFPL + order quantity (used as "Količina postopka" per operation)
            var fAfko = repo.CreateFunction("RFC_READ_TABLE");
            fAfko.SetValue("QUERY_TABLE", "AFKO");
            fAfko.SetValue("DELIMITER", "|");
            var fldsAfko = fAfko.GetTable("FIELDS");
            fldsAfko.Append(); fldsAfko.SetValue("FIELDNAME", "AUFPL");
            fldsAfko.Append(); fldsAfko.SetValue("FIELDNAME", "GAMNG"); // total order qty
            var optAfko = fAfko.GetTable("OPTIONS");
            optAfko.Append(); optAfko.SetValue("TEXT", $"AUFNR = '{aufnr}'");
            fAfko.Invoke(dest);

            var dataAfko = fAfko.GetTable("DATA");
            if (dataAfko.Count == 0) return new List<OperationSummaryDto>();

            var afkoParts = dataAfko[0].GetString("WA").Split('|');
            string aufpl = afkoParts[0].Trim();

            decimal orderQty = 0m; // this is what COOIS shows as "Klč. postopka"
            if (afkoParts.Length > 1 && decimal.TryParse(afkoParts[1].Trim(), out var qRaw))
                orderQty = qRaw / 1000m; // QUAN(3) → 12.000 is sent as 000000000012000

            // AFVC → operations list
            var fAfvc = repo.CreateFunction("RFC_READ_TABLE");
            fAfvc.SetValue("QUERY_TABLE", "AFVC");
            fAfvc.SetValue("DELIMITER", "|");
            var fldsAfvc = fAfvc.GetTable("FIELDS");
            foreach (var fn in new[] { "AUFPL", "APLZL", "VORNR" })
            {
                fldsAfvc.Append(); fldsAfvc.SetValue("FIELDNAME", fn);
            }
            var optAfvc = fAfvc.GetTable("OPTIONS");
            optAfvc.Append(); optAfvc.SetValue("TEXT", $"AUFPL = '{aufpl}'");
            fAfvc.Invoke(dest);

            var results = new List<OperationSummaryDto>();

            foreach (IRfcStructure r in fAfvc.GetTable("DATA"))
            {
                var parts = r.GetString("WA").Split('|');
                if (parts.Length < 3) continue;

                string vornr = parts[2].Trim();

                // AFRU → sum confirmed yield (LMNGA) for this operation
                decimal sumYield = 0m;
                var fAfru = repo.CreateFunction("RFC_READ_TABLE");
                fAfru.SetValue("QUERY_TABLE", "AFRU");
                fAfru.SetValue("DELIMITER", "|");
                var fldsAfru = fAfru.GetTable("FIELDS");
                fldsAfru.Append(); fldsAfru.SetValue("FIELDNAME", "LMNGA");
                var optAfru = fAfru.GetTable("OPTIONS");
                optAfru.Append(); optAfru.SetValue("TEXT", $"AUFNR = '{aufnr}'");
                optAfru.Append(); optAfru.SetValue("TEXT", $"AND VORNR = '{vornr}'");
                fAfru.Invoke(dest);

                foreach (IRfcStructure y in fAfru.GetTable("DATA"))
                {
                    var p = y.GetString("WA").Split('|');
                    if (p.Length > 0 && decimal.TryParse(p[0].Trim(), out var yv)) sumYield += yv;
                }

                results.Add(new OperationSummaryDto
                {
                    Operation = vornr,
                    OperationAmount = orderQty,          // ← "Količina postopka" (same 12 for each op)
                    ConfirmedYield = sumYield / 1000m   // ← "Potrjen donos"
                                                        // BaseQuantity / ConfirmedScrap removed
                });
            }

            return results
                .OrderBy(o => int.TryParse(o.Operation, out var n) ? n : int.MaxValue)
                .ThenBy(o => o.Operation)
                .ToList();
        }
        public List<MaterialInfoDto> GetMaterialsInfoBulk(IEnumerable<string> codes, string language = "SL", string plant = "1061")
        {
            var result = new List<MaterialInfoDto>();
            if (codes == null) return result;

            // Map language -> SPRAS
            string spras = "E";
            if (!string.IsNullOrEmpty(language))
            {
                var up = language.ToUpperInvariant();
                if (up == "SL") spras = "5";
                else if (up == "EN") spras = "E";
                else spras = up;
            }

            var dest = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            var repo = dest.Repository;

            foreach (var raw in codes)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var info = new MaterialInfoDto { Code = raw, Name = null, OrderNumber = null };

                try
                {
                    var code18 = raw.Trim().PadLeft(18, '0'); // MATNR is 18

                    // --- MAKT: name (same logic you had before; single WHERE line with AND) ---
                    try
                    {
                        string name = null;

                        var f = repo.CreateFunction("RFC_READ_TABLE");
                        f.SetValue("QUERY_TABLE", "MAKT");
                        f.SetValue("DELIMITER", "|");
                        f.SetValue("ROWCOUNT", 1);

                        var fields = f.GetTable("FIELDS");
                        fields.Append(); fields.SetValue("FIELDNAME", "MAKTX");

                        var opts = f.GetTable("OPTIONS");
                        // single syntactically-valid WHERE line
                        opts.Append(); opts.SetValue("TEXT", $"MATNR = '{code18}' AND SPRAS = '{spras}'");

                        f.Invoke(dest);
                        var data = f.GetTable("DATA");
                        if (data != null && data.Count > 0)
                        {
                            var parts = data[0].GetString("WA").Split('|');
                            if (parts.Length > 0) name = parts[0].Trim();
                        }

                        // fallback to English
                        if (string.IsNullOrEmpty(name) && spras != "E")
                        {
                            var fEn = repo.CreateFunction("RFC_READ_TABLE");
                            fEn.SetValue("QUERY_TABLE", "MAKT");
                            fEn.SetValue("DELIMITER", "|");
                            fEn.SetValue("ROWCOUNT", 1);
                            var fieldsEn = fEn.GetTable("FIELDS");
                            fieldsEn.Append(); fieldsEn.SetValue("FIELDNAME", "MAKTX");
                            var optsEn = fEn.GetTable("OPTIONS");
                            optsEn.Append(); optsEn.SetValue("TEXT", $"MATNR = '{code18}' AND SPRAS = 'E'");
                            fEn.Invoke(dest);
                            var dataEn = fEn.GetTable("DATA");
                            if (dataEn != null && dataEn.Count > 0)
                            {
                                var parts = dataEn[0].GetString("WA").Split('|');
                                if (parts.Length > 0) name = parts[0].Trim();
                            }
                        }

                        info.Name = name;
                    }
                    catch { /* keep going even if MAKT fails */ }

                    // --- COOIS-like: MATNR + WERKS directly on AFPO (single WHERE line) ---
                    info.OrderNumber = GetOrderForMaterialPlant(dest, code18, plant, 10); // small, but enough
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[GetMaterialsInfoBulk] error for " + raw + ": " + ex.Message);
                    System.Diagnostics.Trace.WriteLine("[GetMaterialsInfoBulk] error for " + raw + ": " + ex.Message);
                }

                result.Add(info);
            }

            return result;
        }

        private static string GetOrderForMaterialPlant(RfcDestination dest, string code18, string plant, int rowCount = 100)
        {
            var repo = dest.Repository;

            try
            {
                var f = repo.CreateFunction("RFC_READ_TABLE");
                f.SetValue("QUERY_TABLE", "AFPO");
                f.SetValue("DELIMITER", "|");
                f.SetValue("ROWCOUNT", rowCount > 0 ? rowCount : 100); // keep more than 10 so we don't miss newer orders

                // Only AUFNR is needed
                var fields = f.GetTable("FIELDS");
                fields.Append(); fields.SetValue("FIELDNAME", "AUFNR");

                // Single valid WHERE line — this is the *only* path we use
                var opts = f.GetTable("OPTIONS");
                opts.Append(); opts.SetValue("TEXT", $"MATNR = '{code18}' AND DWERK = '{plant}'");

                f.Invoke(dest);

                var data = f.GetTable("DATA");
                if (data == null || data.Count == 0) return null;

                // Pick the lexicographically highest AUFNR (works for zero-padded 12-char numbers)
                string best = null;
                for (int i = 0; i < data.Count; i++)
                {
                    var wa = data[i].GetString("WA");
                    if (string.IsNullOrWhiteSpace(wa)) continue;

                    var aufnr = wa.Trim();
                    if (string.IsNullOrEmpty(best) || string.CompareOrdinal(aufnr, best) > 0)
                        best = aufnr;
                }

                return best;
            }
            catch
            {
                // Do not surface ABAP/NCO exceptions
                return null;
            }
        }

        /// <summary>
        /// CR05-like search in CRHD by Plant (WERKS) and Work Center pattern (ARBPL).
        /// Optionally filter by term (on ARBPL or KTEXT) in .NET.
        /// </summary>
        public List<WorkCenterDto> FindWorkCenters(
            string plant = "1061",
            string ted = null,
            string term = null,
            int rowCount = 100)
        {
            var dest = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            var repo = dest.Repository;
            string werks = (plant ?? "1061").Trim().Replace("'", "''");

            // ---------- tiny RFC_READ_TABLE helper (handles 72-char OPTION split) ----------
            List<string> ReadTable(string table, IEnumerable<string> whereLines, int rc, params string[] fieldnames)
            {
                var f = repo.CreateFunction("RFC_READ_TABLE");
                f.SetValue("QUERY_TABLE", table);
                f.SetValue("DELIMITER", "|");
                if (rc > 0) f.SetValue("ROWCOUNT", rc);

                var tf = f.GetTable("FIELDS");
                foreach (var fn in fieldnames) { tf.Append(); tf.SetValue("FIELDNAME", fn); }

                var opts = f.GetTable("OPTIONS");
                if (whereLines != null)
                {
                    foreach (var raw in whereLines)
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        var line = raw.StartsWith(" ") ? raw : " " + raw; // ensure a space before each piece

                        int i = 0;
                        while (i < line.Length)
                        {
                            var part = line.Substring(i, Math.Min(72, line.Length - i));
                            opts.Append(); opts.SetValue("TEXT", part);
                            i += part.Length;
                        }
                    }
                }

                try { f.Invoke(dest); }
                catch { return new List<string>(); } // treat any read error as "no rows"

                var data = f.GetTable("DATA");
                var rows = new List<string>(data.Count);
                for (int i = 0; i < data.Count; i++) rows.Add(data[i].GetString("WA"));
                return rows;
            }

            // ---------- CRHD WHERE ----------
            string tedTrim = string.IsNullOrWhiteSpace(ted) ? null : ted.Trim().Trim('*').TrimStart('0').Replace("'", "''");
            string likeStrict = tedTrim != null ? ("%-" + tedTrim) : null;
            string likeLoose = tedTrim != null ? ("%" + tedTrim) : null;

            IEnumerable<string> CrhdWhere(string like)
            {
                var w = new List<string> { $"WERKS = '{werks}'", "AND OBJTY = 'A'" };
                if (!string.IsNullOrEmpty(like)) w.Add($"AND ARBPL LIKE '{like}'");
                return w;
            }

            // IMPORTANT: When a term is provided, pull more CRHD rows so the later text filter has data to work with.
            int targetTake = rowCount > 0 ? rowCount : 2000;
            int crhdFetch = string.IsNullOrWhiteSpace(term)
                ? targetTake
                : Math.Min(2000, Math.Max(targetTake * 10, 200));  // over-fetch, then filter

            // CRHD: only ARBPL + OBJID
            var crhd = ReadTable("CRHD", CrhdWhere(likeStrict), crhdFetch, "ARBPL", "OBJID");
            if (crhd.Count == 0 && tedTrim != null)
                crhd = ReadTable("CRHD", CrhdWhere(likeLoose), crhdFetch, "ARBPL", "OBJID");

            var provisional = crhd
                .Select(wa => wa.Split('|'))
                .Where(p => p.Length >= 2)
                .Select(p => (arbpl: p[0].Trim(), objid: p[1].Trim()))
                .Where(x => x.arbpl.Length > 0 && x.objid.Length > 0)
                .ToList();

            if (provisional.Count == 0) return new List<WorkCenterDto>();

            // NOTE: we intentionally DO NOT pre-filter by term on ARBPL here.
            // We’ll filter by ARBPL or KTEXT AFTER we fetch texts.

            // ---------- CRTX texts via OR-chunks (no IN), 72-char safe ----------
            var textByObj = new Dictionary<string, string>(StringComparer.Ordinal);

            List<string> BuildCrtxWhere(string spras, IEnumerable<string> ids)
            {
                var lines = new List<string>();
                bool first = true;
                foreach (var id in ids)
                {
                    var sid = id.Replace("'", "''");
                    var cond = $"( SPRAS = '{spras}' AND OBJTY = 'A' AND OBJID = '{sid}' )";
                    lines.Add(first ? cond : "OR " + cond);
                    first = false;
                }
                return lines;
            }

            void FetchTexts(string spras)
            {
                // conservative chunk so each WHERE line stays well under 72 chars
                const int chunk = 6;
                var ids = provisional.Select(x => x.objid).Distinct().ToList();
                for (int i = 0; i < ids.Count; i += chunk)
                {
                    var where = BuildCrtxWhere(spras, ids.Skip(i).Take(chunk));
                    var rows = ReadTable("CRTX", where, 0, "OBJID", "KTEXT");
                    foreach (var wa in rows)
                    {
                        var p = wa.Split('|');
                        if (p.Length >= 2)
                        {
                            var id = p[0].Trim();
                            var txt = p[1].Trim();
                            if (id.Length > 0 && txt.Length > 0 && !textByObj.ContainsKey(id))
                                textByObj[id] = txt;
                        }
                    }
                }
            }

            // try SL (5) then EN (E) for the remaining ones
            FetchTexts("5");
            if (textByObj.Count < provisional.Select(x => x.objid).Distinct().Count())
                FetchTexts("E");

            // ---------- compose + final filter (by ARBPL OR KTEXT) ----------
            var results = provisional.Select(x => new WorkCenterDto
            {
                DelovnoMesto = x.arbpl,
                Opis = textByObj.TryGetValue(x.objid, out var t) ? t : null
            });

            if (!string.IsNullOrWhiteSpace(term))
            {
                var t = term.Trim();
                results = results.Where(x =>
                    (x.DelovnoMesto?.IndexOf(t, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                    (x.Opis?.IndexOf(t, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                );
            }

            return results
                .GroupBy(x => new { x.DelovnoMesto, x.Opis })
                .Select(g => g.First())
                .OrderBy(x => x.DelovnoMesto)
                .ThenBy(x => x.Opis)
                .Take(targetTake)
                .ToList();
        }



































        // === OPERATION STATUS (COOIS: PPIO000 Postopki) ==============================
        public string GetOperationStatusForOrderWorkCenter(
    string orderNumber,
    string workCenter,
    string plant = "1061",
    string language = "SL",
    string material = null, // intentionally unused
    string vornr = null)
        {
            if (string.IsNullOrWhiteSpace(orderNumber) || string.IsNullOrWhiteSpace(workCenter))
                return null;

            var dest = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            var repo = dest.Repository;

            string aufnr = orderNumber.Trim().PadLeft(12, '0');
            string spras = (language ?? "SL").ToUpperInvariant() == "EN" ? "E" : "5";
            string arbpl = workCenter.Trim(); // SAP stores ARBPL uppercase typically

            IRfcTable RT(string table, int rowCount, Action<IRfcTable> fields, string where)
            {
                var f = repo.CreateFunction("RFC_READ_TABLE");
                f.SetValue("QUERY_TABLE", table);
                f.SetValue("DELIMITER", "|");
                if (rowCount > 0) f.SetValue("ROWCOUNT", rowCount);
                fields?.Invoke(f.GetTable("FIELDS"));
                var opts = f.GetTable("OPTIONS");
                if (!string.IsNullOrWhiteSpace(where))
                {
                    var line = where.StartsWith(" ") ? where : " " + where;
                    for (int i = 0; i < line.Length; i += 72)
                    {
                        var part = line.Substring(i, Math.Min(72, line.Length - i));
                        opts.Append(); opts.SetValue("TEXT", part);
                    }
                }
                try { f.Invoke(dest); } catch { return null; }
                return f.GetTable("DATA");
            }

            // 1) CRHD exact → OBJID
            var crhd = RT("CRHD", 1,
                f => { f.Append(); f.SetValue("FIELDNAME", "OBJID"); },
                $"WERKS = '{plant}' AND OBJTY = 'A' AND ARBPL = '{arbpl}'");
            if (crhd == null || crhd.RowCount == 0) return null;
            var objid = crhd[0].GetString("WA").Trim();
            if (string.IsNullOrEmpty(objid)) return null;

            // 2) AFKO → AUFPL
            var afko = RT("AFKO", 1,
                f => { f.Append(); f.SetValue("FIELDNAME", "AUFPL"); },
                $"AUFNR = '{aufnr}'");
            if (afko == null || afko.RowCount == 0) return null;
            var aufpl = afko[0].GetString("WA").Trim();
            if (string.IsNullOrEmpty(aufpl)) return null;

            // 3) AFVC (AUFPL+ARBID) → VORNR+OBJNR
            var afvc = RT("AFVC", 0,
                f => { f.Append(); f.SetValue("FIELDNAME", "VORNR"); f.Append(); f.SetValue("FIELDNAME", "OBJNR"); },
                $"AUFPL = '{aufpl}' AND ARBID = '{objid}'");
            if (afvc == null || afvc.RowCount == 0) return null;

            var ops = new List<(string VORNR, string OBJNR)>(afvc.RowCount);
            for (int i = 0; i < afvc.RowCount; i++)
            {
                var p = afvc[i].GetString("WA").Split('|');
                if (p.Length >= 2)
                {
                    var v = p[0].Trim(); var o = p[1].Trim();
                    if (!string.IsNullOrEmpty(v) && !string.IsNullOrEmpty(o)) ops.Add((v, o));
                }
            }
            if (ops.Count == 0) return null;

            (string VORNR, string OBJNR) chosen = default;
            if (!string.IsNullOrWhiteSpace(vornr))
                chosen = ops.FirstOrDefault(x => x.VORNR == vornr.Trim());
            if (string.IsNullOrEmpty(chosen.VORNR))
                chosen = ops.OrderBy(x => x.VORNR, StringComparer.Ordinal).First();

            // 4) JEST active → I**** codes
            var jest = RT("JEST", 0,
                f => { f.Append(); f.SetValue("FIELDNAME", "STAT"); f.Append(); f.SetValue("FIELDNAME", "INACT"); },
                $"OBJNR = '{chosen.OBJNR}' AND INACT = ' '");
            if (jest == null) return "";

            var codes = new HashSet<String>(StringComparer.Ordinal);
            for (int i = 0; i < jest.RowCount; i++)
            {
                var s = jest[i].GetString("WA").Split('|');
                if (s.Length >= 1)
                {
                    var c = s[0].Trim();
                    if (c.Length > 0 && c[0] == 'I') codes.Add(c);
                }
            }
            if (codes.Count == 0) return "";

            // 5) TJ02T ISTAT->TXT04 map (preferred lang, then EN for any missing)
            List<string> sorted = codes.OrderBy(c =>
            {
                int n; return int.TryParse(c.Length > 1 ? c.Substring(1) : "0", out n) ? n : int.MaxValue;
            }).ToList();

            var txt = new Dictionary<string, string>(StringComparer.Ordinal);

            void MapTxt04(IEnumerable<string> istats, string lang)
            {
                const int chunk = 60;
                var bloc = istats.ToList();
                for (int i = 0; i < bloc.Count; i += chunk)
                {
                    var take = bloc.Skip(i).Take(chunk).ToList();
                    var where = "(" + string.Join(" OR ", take.Select(c => $"ISTAT = '{c}'")) + $") AND SPRAS = '{lang}'";
                    var t = RT("TJ02T", 0,
                        f => { f.Append(); f.SetValue("FIELDNAME", "ISTAT"); f.Append(); f.SetValue("FIELDNAME", "TXT04"); },
                        where);
                    if (t == null) continue;
                    for (int r = 0; r < t.RowCount; r++)
                    {
                        var p = t[r].GetString("WA").Split('|');
                        if (p.Length >= 2 && !txt.ContainsKey(p[0].Trim()))
                            txt[p[0].Trim()] = p[1].Trim();
                    }
                }
            }

            MapTxt04(sorted, spras);
            var missing = sorted.Where(c => !txt.ContainsKey(c)).ToList();
            if (missing.Count > 0) MapTxt04(missing, "E");

            var parts = sorted.Select(c => txt.TryGetValue(c, out var t) && !string.IsNullOrWhiteSpace(t) ? t : c);
            return string.Join(" ", parts).Trim();
        }














































        public List<CooisOrderRowDto> GetOrdersByWorkCenter(
            string workCenter,
            string plant = "1061",
            string language = "SL",
            int take = 200,
            string mrpController = null,     // NEW: AFKO.DISPO (e.g. "647")
            string headerStatus = null       // NEW: header system status filter (e.g. "I0002" or "LANS")
        )
        {
            var result = new List<CooisOrderRowDto>();
            if (string.IsNullOrWhiteSpace(workCenter)) return result;

            var dest = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            var repo = dest.Repository;

            string spras = (language ?? "SL").ToUpperInvariant() == "EN" ? "E" : "5";

            // ---------- helpers ----------
            IRfcTable ReadTable(string table, int rowCount,
                                Action<IRfcTable> addFields, Action<IRfcTable> addOptions)
            {
                var f = repo.CreateFunction("RFC_READ_TABLE");
                f.SetValue("QUERY_TABLE", table);
                f.SetValue("DELIMITER", "|");
                if (rowCount > 0) f.SetValue("ROWCOUNT", rowCount);
                addFields?.Invoke(f.GetTable("FIELDS"));
                addOptions?.Invoke(f.GetTable("OPTIONS"));
                try { f.Invoke(dest); } catch { return null; }
                return f.GetTable("DATA");
            }
            void AppendWhere(IRfcTable opts, string where)
            {
                if (string.IsNullOrWhiteSpace(where)) return;
                var line = where.StartsWith(" ") ? where : " " + where;
                for (int i = 0; i < line.Length; i += 72)
                {
                    var part = line.Substring(i, Math.Min(72, line.Length - i));
                    opts.Append(); opts.SetValue("TEXT", part);
                }
            }
            decimal ParseQuan(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return 0m;
                if (s.IndexOf('.') < 0 && s.IndexOf(',') < 0 &&
                    decimal.TryParse(s, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out var iv))
                    return iv / 1000m; // QUAN(3)
                decimal.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out var dv);
                return dv;
            }

            // ---------- 1) CRHD -> OBJID (work center internal id) ----------
            string objid = null;
            {
                var data = ReadTable("CRHD", 1,
                    f => { f.Append(); f.SetValue("FIELDNAME", "OBJID"); },
                    o =>
                    {
                        o.Append(); o.SetValue("TEXT", "OBJTY = 'A'");
                        o.Append(); o.SetValue("TEXT", "AND WERKS = '" + plant + "'");
                        o.Append(); o.SetValue("TEXT", "AND ARBPL = '" + workCenter.Trim() + "'");
                    });
                if (data == null || data.RowCount == 0) return result;
                objid = data[0].GetString("WA").Trim();
                if (string.IsNullOrEmpty(objid)) return result;
            }

            // ---------- 2) AFVC -> AUFPL, VORNR, OBJNR (ops at this WC) ----------
            var afvcOps = new List<Tuple<string, string, string>>(); // (AUFPL, VORNR, OBJNR)
            {
                var data = ReadTable("AFVC", 8000,
                    f =>
                    {
                        f.Append(); f.SetValue("FIELDNAME", "AUFPL");
                        f.Append(); f.SetValue("FIELDNAME", "VORNR");
                        f.Append(); f.SetValue("FIELDNAME", "OBJNR");
                    },
                    o => { o.Append(); o.SetValue("TEXT", "ARBID = '" + objid + "'"); });
                if (data == null) return result;

                for (int i = 0; i < data.RowCount; i++)
                {
                    var p = data[i].GetString("WA").Split('|');
                    if (p.Length < 3) continue;
                    var aufpl = p[0].Trim();
                    var vornr = p[1].Trim();
                    var objnrOp = p[2].Trim();
                    if (!string.IsNullOrEmpty(aufpl) && !string.IsNullOrEmpty(vornr) && !string.IsNullOrEmpty(objnrOp))
                        afvcOps.Add(Tuple.Create(aufpl, vornr, objnrOp));
                    if (take > 0 && afvcOps.Count >= take * 5) break;
                }
            }
            if (afvcOps.Count == 0) return result;

            // ---------- 3) AFKO -> AUFNR (+DISPO for optional MRP filter) ----------
            var orderNumbers = new List<string>();
            var aufnrByAufpl = new Dictionary<string, string>(StringComparer.Ordinal);
            var dispoByAufnr = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var g in afvcOps.Select(x => x.Item1).Distinct())
            {
                var data = ReadTable("AFKO", 20,
                    f =>
                    {
                        f.Append(); f.SetValue("FIELDNAME", "AUFNR");
                        f.Append(); f.SetValue("FIELDNAME", "DISPO"); // MRP Controller
                        f.Append(); f.SetValue("FIELDNAME", "AUFPL");
                    },
                    o => { o.Append(); o.SetValue("TEXT", "AUFPL = '" + g + "'"); });
                if (data == null) continue;

                for (int i = 0; i < data.RowCount; i++)
                {
                    var p = data[i].GetString("WA").Split('|');
                    if (p.Length < 3) continue;
                    var aufnr = p[0].Trim().PadLeft(12, '0');
                    var dispo = p[1].Trim();
                    var aufpl = p[2].Trim();

                    // optional MRP controller filter (COOIS header)
                    if (!string.IsNullOrWhiteSpace(mrpController) &&
                        !string.Equals(dispo, mrpController.Trim(), StringComparison.Ordinal))
                        continue;

                    if (!aufnrByAufpl.ContainsKey(aufpl)) aufnrByAufpl[aufpl] = aufnr;
                    if (!dispoByAufnr.ContainsKey(aufnr)) dispoByAufnr[aufnr] = dispo;

                    if (!orderNumbers.Contains(aufnr))
                    {
                        orderNumbers.Add(aufnr);
                        if (take > 0 && orderNumbers.Count >= take) break;
                    }
                }
                if (take > 0 && orderNumbers.Count >= take) break;
            }
            if (orderNumbers.Count == 0) return result;

            // ---------- 4) optional HEADER system-status include (COOIS header “Stat. sis.”) ----------
            if (!string.IsNullOrWhiteSpace(headerStatus))
            {
                // accept either ISTAT (Ixxxx) or TXT04 (e.g., LANS/REL/DPOT…)
                string wantedIstat = null;

                if (headerStatus.Trim().StartsWith("I", StringComparison.OrdinalIgnoreCase))
                {
                    wantedIstat = headerStatus.Trim().ToUpperInvariant();
                }
                else
                {
                    // map TXT04 -> ISTAT in requested language, fallback EN
                    string txt = headerStatus.Trim();
                    string MapTxtToIstat(string lang)
                    {
                        var t = ReadTable("TJ02T", 0,
                            f => { f.Append(); f.SetValue("FIELDNAME", "ISTAT"); f.Append(); f.SetValue("FIELDNAME", "TXT04"); },
                            o => { AppendWhere(o, $"TXT04 = '{txt}' AND SPRAS = '{lang}'"); });
                        if (t != null && t.RowCount > 0)
                        {
                            var p = t[0].GetString("WA").Split('|');
                            if (p.Length >= 1) return p[0].Trim();
                        }
                        return null;
                    }
                    wantedIstat = MapTxtToIstat(spras) ?? MapTxtToIstat("E");
                }

                if (!string.IsNullOrEmpty(wantedIstat))
                {
                    // AUFK -> OBJNR for these orders
                    var objnrs = new Dictionary<string, string>(StringComparer.Ordinal); // AUFNR->OBJNR
                    const int chunk = 90;
                    for (int i = 0; i < orderNumbers.Count; i += chunk)
                    {
                        var blk = orderNumbers.Skip(i).Take(chunk).ToList();
                        var t = ReadTable("AUFK", 0,
                            f => { f.Append(); f.SetValue("FIELDNAME", "AUFNR"); f.Append(); f.SetValue("FIELDNAME", "OBJNR"); },
                            o => { AppendWhere(o, "(" + string.Join(" OR ", blk.Select(a => $"AUFNR = '{a}'")) + ")"); });
                        if (t != null)
                        {
                            for (int r = 0; r < t.RowCount; r++)
                            {
                                var p = t[r].GetString("WA").Split('|');
                                if (p.Length >= 2) objnrs[p[0].Trim().PadLeft(12, '0')] = p[1].Trim();
                            }
                        }
                    }

                    // JEST (active) filter
                    var keep = new HashSet<string>(StringComparer.Ordinal);
                    var objList = objnrs.Values.Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();

                    for (int i = 0; i < objList.Count; i += 40)
                    {
                        var blk = objList.Skip(i).Take(40).ToList();
                        var t = ReadTable("JEST", 0,
                            f => { f.Append(); f.SetValue("FIELDNAME", "OBJNR"); f.Append(); f.SetValue("FIELDNAME", "STAT"); f.Append(); f.SetValue("FIELDNAME", "INACT"); },
                            o => {
                                AppendWhere(o, "(" + string.Join(" OR ", blk.Select(o2 => $"OBJNR = '{o2}'")) + ")");
                                o.Append(); o.SetValue("TEXT", "AND INACT = ' '");
                            });
                        if (t != null)
                        {
                            for (int r = 0; r < t.RowCount; r++)
                            {
                                var p = t[r].GetString("WA").Split('|');
                                if (p.Length >= 3 && p[1].Trim().Equals(wantedIstat, StringComparison.Ordinal))
                                {
                                    var obj = p[0].Trim();
                                    foreach (var kv in objnrs.Where(kv => kv.Value == obj))
                                        keep.Add(kv.Key);
                                }
                            }
                        }
                    }

                    orderNumbers = orderNumbers.Where(o => keep.Contains(o)).ToList();
                    if (orderNumbers.Count == 0) return result;
                }
            }

            // ---------- 5) AFRU -> confirmed yield at THIS WC (per order) ----------
            var donos = new Dictionary<string, decimal>();
            foreach (var aufnr in orderNumbers)
            {
                decimal sum = 0m;
                var data = ReadTable("AFRU", 0,
                    f => { f.Append(); f.SetValue("FIELDNAME", "LMNGA"); },
                    o => { o.Append(); o.SetValue("TEXT", "AUFNR = '" + aufnr + "' AND ARBID = '" + objid + "'"); });
                if (data != null)
                {
                    for (int i = 0; i < data.RowCount; i++)
                        sum += ParseQuan(data[i].GetString("WA").Trim());
                }
                donos[aufnr] = sum;
            }

            // ---------- 6) BAPI_PRODORD_GET_LIST -> header info (qty/unit/material/text) ----------
            IRfcTable orderHeader = null;
            try
            {
                var funcList = repo.CreateFunction("BAPI_PRODORD_GET_LIST");
                var orderRange = funcList.GetTable("ORDER_NUMBER_RANGE");
                foreach (var ord in orderNumbers)
                {
                    orderRange.Append();
                    orderRange.SetValue("SIGN", "I");
                    orderRange.SetValue("OPTION", "EQ");
                    orderRange.SetValue("LOW", ord);
                    orderRange.SetValue("HIGH", "");
                }
                var plantRange = funcList.GetTable("PRODPLANT_RANGE");
                plantRange.Append();
                plantRange.SetValue("SIGN", "I");
                plantRange.SetValue("OPTION", "EQ");
                plantRange.SetValue("LOW", plant);
                plantRange.SetValue("HIGH", "");
                funcList.Invoke(dest);
                orderHeader = funcList.GetTable("ORDER_HEADER");
            }
            catch { orderHeader = null; }
            if (orderHeader == null) return result;

            // ---------- 7) MAKT -> material text (as before) ----------
            var mtexts = new Dictionary<string, string>();
            var materials = new HashSet<string>();
            for (int i = 0; i < orderHeader.RowCount; i++)
            {
                var m = orderHeader[i].GetString("MATERIAL") ?? "";
                m = m.Trim().PadLeft(18, '0');
                if (!string.IsNullOrEmpty(m)) materials.Add(m);
            }
            foreach (var mat in materials)
            {
                string text = null;
                var d1 = ReadTable("MAKT", 1,
                    f => { f.Append(); f.SetValue("FIELDNAME", "MAKTX"); },
                    o => { o.Append(); o.SetValue("TEXT", "MATNR = '" + mat + "' AND SPRAS = '" + spras + "'"); });
                if (d1 != null && d1.RowCount > 0) text = d1[0].GetString("WA").Trim();
                if (string.IsNullOrEmpty(text) && spras != "E")
                {
                    var d2 = ReadTable("MAKT", 1,
                        f => { f.Append(); f.SetValue("FIELDNAME", "MAKTX"); },
                        o => { o.Append(); o.SetValue("TEXT", "MATNR = '" + mat + "' AND SPRAS = 'E'"); });
                    if (d2 != null && d2.RowCount > 0) text = d2[0].GetString("WA").Trim();
                }
                mtexts[mat] = text ?? "";
            }

            // ---------- 8) Build result rows (Status via GetOperationStatusForOrderWorkCenter) ----------
            for (int i = 0; i < orderHeader.RowCount; i++)
            {
                var row = orderHeader[i];

                string aufnr = (row.GetString("ORDER_NUMBER") ?? "").Trim().PadLeft(12, '0');
                if (!orderNumbers.Contains(aufnr)) continue;

                string matnr = (row.GetString("MATERIAL") ?? "").Trim().PadLeft(18, '0');

                decimal stdQty = 0m;
                try { stdQty = row.GetDecimal("TARGET_QUANTITY"); }
                catch
                {
                    if (decimal.TryParse(row.GetString("TARGET_QUANTITY"),
                        System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var tmp))
                        stdQty = tmp;
                }
                string unit = (row.GetString("UNIT") ?? "").Trim();

                if (!mtexts.TryGetValue(matnr, out var matText)) matText = (row.GetString("MATERIAL_TEXT") ?? "").Trim();

                // NEW: status from the dedicated method (operation-level, earliest op at WC)
                string statusText = GetOperationStatusForOrderWorkCenter(aufnr, workCenter, plant, language);

                result.Add(new CooisOrderRowDto
                {
                    Nalog = aufnr,
                    Material = matnr,
                    StdKolicina = stdQty,
                    Donos = donos.ContainsKey(aufnr) ? donos[aufnr] : 0m,
                    EM = unit,
                    KratkiTekstMateriala = matText,
                    StatusSistema = statusText ?? ""
                });

                if (take > 0 && result.Count >= take) break;
            }

            return (take > 0) ? result.Take(take).ToList() : result;
        }







        private static string AlphaOut(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            var t = s.TrimStart('0');
            return t.Length == 0 ? "0" : t;
        }

        public List<OpenOrderId> GetOpenOrdersForMaterial(
            string materialCode,
            string plant = "1061",
            string language = "SL",
            int take = 50)
        {
            var result = new List<OpenOrderId>();
            if (string.IsNullOrWhiteSpace(materialCode)) return result;

            var dest = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            var repo = dest.Repository;

            string matnr18 = materialCode.Trim().PadLeft(18, '0');
            string spras = (language ?? "SL").ToUpperInvariant() == "EN" ? "E" : "5";

            // ---------- RFC helpers ----------
            IRfcTable ReadTable(string table, int rowCount,
                                Action<IRfcTable> addFields,
                                Action<IRfcTable> addOptions)
            {
                var f = repo.CreateFunction("RFC_READ_TABLE");
                f.SetValue("QUERY_TABLE", table);
                f.SetValue("DELIMITER", "|");
                if (rowCount > 0) f.SetValue("ROWCOUNT", rowCount);
                addFields?.Invoke(f.GetTable("FIELDS"));
                addOptions?.Invoke(f.GetTable("OPTIONS"));
                try { f.Invoke(dest); } catch { return null; }
                return f.GetTable("DATA");
            }
            void AppendWhere(IRfcTable opts, string where)
            {
                if (string.IsNullOrWhiteSpace(where)) return;
                var line = where.StartsWith(" ") ? where : " " + where;
                for (int i = 0; i < line.Length; i += 72)
                {
                    var part = line.Substring(i, Math.Min(72, line.Length - i));
                    opts.Append(); opts.SetValue("TEXT", part);
                }
            }
            List<string> RowsToAufnr(IRfcTable data)
            {
                var list = new List<string>();
                if (data == null) return list;
                for (int i = 0; i < data.RowCount; i++)
                {
                    var a = data[i].GetString("WA")?.Trim();
                    if (!string.IsNullOrEmpty(a))
                    {
                        a = a.PadLeft(12, '0');
                        if (a.Length == 12 && !list.Contains(a)) list.Add(a);
                    }
                }
                return list;
            }

            // ---------- 1) Candidates (AFPO primary; CAUFV fallback if empty) ----------
            var candidateOrders = new List<string>();
            int rowcap = take > 0 ? take * 5 : 0;

            // AFPO: MATNR(18) + DWERK
            {
                var t = ReadTable("AFPO", rowcap,
                    f => { f.Append(); f.SetValue("FIELDNAME", "AUFNR"); },
                    o => { AppendWhere(o, $"MATNR = '{matnr18}' AND DWERK = '{plant}'"); });
                candidateOrders = RowsToAufnr(t);
            }

            // Fallback only if AFPO path is empty: CAUFV: MATNR(18)+DWERK
            if (candidateOrders.Count == 0)
            {
                var t = ReadTable("CAUFV", rowcap,
                    f => { f.Append(); f.SetValue("FIELDNAME", "AUFNR"); },
                    o => { AppendWhere(o, $"MATNR = '{matnr18}' AND DWERK = '{plant}'"); });
                candidateOrders = RowsToAufnr(t);
            }

            if (candidateOrders.Count == 0) return result;

            // ---------- 2) Build accepted 'Released' ISTAT set ----------
            var wantIstat = new HashSet<string>(StringComparer.Ordinal) { "I0002" }; // always include I0002
            string MapTxtToIstat(string txt, string lang)
            {
                var t = ReadTable("TJ02T", 1,
                    f => { f.Append(); f.SetValue("FIELDNAME", "ISTAT"); f.Append(); f.SetValue("FIELDNAME", "TXT04"); },
                    o => { AppendWhere(o, $"TXT04 = '{txt}' AND SPRAS = '{lang}'"); });
                if (t != null && t.RowCount > 0)
                {
                    var p = t[0].GetString("WA").Split('|');
                    if (p.Length >= 1) return p[0].Trim();
                }
                return null;
            }
            foreach (var lbl in new[] { "LANS", "REL", "RES" })
            {
                var i1 = MapTxtToIstat(lbl, spras);
                var i2 = MapTxtToIstat(lbl, "E");
                if (!string.IsNullOrEmpty(i1)) wantIstat.Add(i1);
                if (!string.IsNullOrEmpty(i2)) wantIstat.Add(i2);
            }

            // ---------- 3) Map CAUFV.OBJNR for candidates ----------
            var objnrByAufnr = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var a in candidateOrders)
            {
                var t = ReadTable("CAUFV", 1,
                    f => { f.Append(); f.SetValue("FIELDNAME", "AUFNR"); f.Append(); f.SetValue("FIELDNAME", "OBJNR"); },
                    o => { AppendWhere(o, $"AUFNR = '{a}'"); });
                if (t != null && t.RowCount > 0)
                {
                    var obj = t[0].GetString("WA").Split('|').ElementAtOrDefault(1)?.Trim();
                    if (!string.IsNullOrEmpty(obj)) objnrByAufnr[a] = obj;
                }
            }

            // ---------- 4) Filter: CAUFV.OBJNR → JEST active → any ISTAT in wantIstat ----------
            List<string> GetActiveStats(string objnr)
            {
                var t = ReadTable("JEST", 0,
                    f => { f.Append(); f.SetValue("FIELDNAME", "STAT"); f.Append(); f.SetValue("FIELDNAME", "INACT"); },
                    o => { AppendWhere(o, $"OBJNR = '{objnr}' AND INACT = ' '"); });
                var res = new List<string>();
                if (t == null) return res;
                for (int i = 0; i < t.RowCount; i++)
                {
                    var s = (t[i].GetString("WA").Split('|').FirstOrDefault() ?? "").Trim();
                    if (!string.IsNullOrEmpty(s)) res.Add(s);
                }
                return res.Distinct().ToList();
            }

            var kept = new List<string>();
            foreach (var a in candidateOrders)
            {
                if (!objnrByAufnr.TryGetValue(a, out var obj) || string.IsNullOrEmpty(obj))
                    continue;

                var active = GetActiveStats(obj);
                if (active.Any(s => wantIstat.Contains(s)))
                    kept.Add(a);
            }

            if (take > 0 && kept.Count > take) kept = kept.Take(take).ToList();

            var final = (take > 0 && kept.Count > take) ? kept.Take(take).ToList() : kept;

            // Project to DTOs (Aufnr + trimmed AufnrDisplay)
            return final.Select(a => new OpenOrderId
            {
                Aufnr = a,
                AufnrDisplay = AlphaOut(a)
            }).ToList();
        }

    }
}
