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
        public (string Quantity, string Unit) GetTotalOrderQuantityWithUnit(string orderNumber)
        {
            var dest = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            var repo = dest.Repository;

            var readTable = repo.CreateFunction("RFC_READ_TABLE");
            readTable.SetValue("QUERY_TABLE", "AFKO");
            readTable.SetValue("DELIMITER", "|");
            readTable.SetValue("ROWCOUNT", 1);

            var fields = readTable.GetTable("FIELDS");
            fields.Append(); fields.SetValue("FIELDNAME", "GAMNG");
            fields.Append(); fields.SetValue("FIELDNAME", "GMEIN");

            var options = readTable.GetTable("OPTIONS");
            options.Append(); options.SetValue("TEXT", $"AUFNR = '{orderNumber.PadLeft(12, '0')}'");

            readTable.Invoke(dest);

            var data = readTable.GetTable("DATA");
            if (data.Count == 0) return (null, null);

            var line = data[0].GetString("WA");
            var parts = line.Split('|');

            var quantity = parts.Length > 0 ? parts[0].Trim() : null;
            var unit = parts.Length > 1 ? parts[1].Trim() : null;

            return (quantity, unit);
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
    }
}
