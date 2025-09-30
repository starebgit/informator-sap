using SAP.Middleware.Connector;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Configuration;
using InformatorSAP.Classes;

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

    }
}
