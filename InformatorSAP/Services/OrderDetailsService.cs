using InformatorSAP.Models;
using SAP.Middleware.Connector;
using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;

namespace InformatorSAP.Services
{
    public class OrderDetailsService
    {
        private readonly SapService _sap;
            
        public OrderDetailsService()
        {
            // later we can change this to DI if needed
            _sap = new SapService();
        }

        private static string GetIsoDate(IRfcStructure row, string fieldName)
        {
            var raw = row.GetString(fieldName);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            // NCo often returns something already like "2023-05-12"
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                                  DateTimeStyles.None, out var dt))
            {
                return dt.ToString("yyyy-MM-dd");
            }

            // Fallback if some system sends "20230512"
            if (DateTime.TryParseExact(raw, "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out dt))
            {
                return dt.ToString("yyyy-MM-dd");
            }

            // Last resort – return whatever SAP sent
            return raw;
        }

        /// <summary>
        /// Will return the full order details (SAP + side tables) for one order.
        /// </summary>
        public OrderDetailsDto GetOrderDetails(string orderNumber)
        {
            if (string.IsNullOrWhiteSpace(orderNumber))
                return null;

            var dest = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            var repo = dest.Repository;

            // 1) === FIRST CALL: BAPI_PRODORD_GET_LIST ===
            var func = repo.CreateFunction("BAPI_PRODORD_GET_LIST");

            var orderRange = func.GetTable("ORDER_NUMBER_RANGE");
            orderRange.Append();
            orderRange.SetValue("SIGN", "I");
            orderRange.SetValue("OPTION", "EQ");
            var order12 = orderNumber.Trim().PadLeft(12, '0');
            orderRange.SetValue("LOW", order12);

            var plantRange = func.GetTable("PRODPLANT_RANGE");
            plantRange.Append();
            plantRange.SetValue("SIGN", "I");
            plantRange.SetValue("OPTION", "EQ");
            plantRange.SetValue("LOW", "1061");

            func.Invoke(dest);

            var header = func.GetTable("ORDER_HEADER");
            if (header == null || header.RowCount == 0)
                return null;

            var row = header[0];

            var material18 = row.GetString("MATERIAL") ?? string.Empty;
            var materialKey = material18.Trim().PadLeft(18, '0');

            // 2) === SECOND CALL: BAPI_PRODORD_GET_DETAIL ===
            var funcDetail = repo.CreateFunction("BAPI_PRODORD_GET_DETAIL");
            funcDetail.SetValue("NUMBER", order12);

            var orderObjects = funcDetail.GetStructure("ORDER_OBJECTS");
            orderObjects.SetValue("HEADER", "X");
            orderObjects.SetValue("COMPONENTS", "X");
            orderObjects.SetValue("OPERATIONS", "X");

            funcDetail.Invoke(dest);

            // 3) === COMPONENTS -> dto.Parts (Delphi-equivalent) ===
            var components = funcDetail.GetTable("COMPONENT");

            // Collect all materials (header + component materials) for bulk lookup
            var allMaterials = new List<string>();
            if (!string.IsNullOrWhiteSpace(material18))
                allMaterials.Add(material18);

            if (components != null && components.RowCount > 0)
            {
                for (int i = 0; i < components.RowCount; i++)
                {
                    var compMat = components[i].GetString("MATERIAL");
                    if (!string.IsNullOrWhiteSpace(compMat))
                        allMaterials.Add(compMat);
                }
            }

            // Bulk fetch dimensions and Slovene names in a few SAP calls
            var dimMap = GetDimensionsBulk(dest, allMaterials);       // MATNR -> GROES
            var nameSlMap = GetMaterialNamesSlBulk(dest, allMaterials); // MATNR -> MAKTX (SL)

            // Now we can build the DTO header, using bulk maps
            dimMap.TryGetValue(materialKey, out var headerDim);
            nameSlMap.TryGetValue(materialKey, out var headerNameSl);

            var dto = new OrderDetailsDto
            {
                MaterialCode = material18?.Trim(),
                Name = !string.IsNullOrWhiteSpace(headerNameSl)
                    ? headerNameSl
                    : row.GetString("MATERIAL_TEXT"),
                Quantity = (int?)row.GetDecimal("TARGET_QUANTITY"),
                Code = int.TryParse(orderNumber, out var c) ? c : 0,
                Dimension = headerDim,
            };

            var header2 = funcDetail.GetTable("HEADER");
            if (header2 != null && header2.RowCount > 0)
            {
                var hrow = header2[0];

                dto.StartDate = GetIsoDate(hrow, "PRODUCTION_START_DATE");
                dto.ScheduledEnddate = GetIsoDate(hrow, "PRODUCTION_FINISH_DATE");
            }

            if (components != null && components.RowCount > 0)
            {
                for (int i = 0; i < components.RowCount; i++)
                {
                    var crow = components[i];

                    var itemNumber = crow.GetString("ITEM_NUMBER");
                    if (string.Compare(itemNumber, "0550", StringComparison.Ordinal) >= 0)
                    {
                        // skip items with ITEM_NUMBER >= '0550'
                        continue;
                    }

                    var compMat18 = crow.GetString("MATERIAL");
                    if (string.IsNullOrWhiteSpace(compMat18))
                        continue;

                    var compKey = compMat18.Trim().PadLeft(18, '0');

                    dimMap.TryGetValue(compKey, out var partDim);
                    nameSlMap.TryGetValue(compKey, out var partNameSl);

                    var part = new PartDto
                    {
                        EgoCode = compMat18?.Trim(),

                        // Use Slovene name from MAKT if available, otherwise BAPI description
                        Name = !string.IsNullOrWhiteSpace(partNameSl)
                            ? partNameSl
                            : crow.GetString("MATERIAL_DESCRIPTION")?.Trim(),

                        Dimension = partDim,

                        Quantity = crow.GetString("COMMITED_QUANTITY")
                    };

                    dto.Parts.Add(part);
                }
            }

            // 4) === OPERATIONS -> dto.Operations (Delphi-equivalent) ===
            var operations = funcDetail.GetTable("OPERATION");

            if (operations != null && operations.RowCount > 0)
            {
                for (int i = 0; i < operations.RowCount; i++)
                {
                    var orow = operations[i];

                    // Delphi: plnnr := tabl.value[i,43];
                    // if copy(plnnr,1,4) <> '4999' then ...
                    // Field 43 in NCo = WORK_CENTER
                    var workCenter = orow.GetString("WORK_CENTER") ?? string.Empty;
                    if (workCenter.StartsWith("4999"))
                    {
                        // skip “4999…” work centers, same as old Delphi logic
                        continue;
                    }

                    var op = new OperationDto
                    {
                        // Delphi: zapoper[ii] := tabl.value[i,11];
                        Sequence = orow.GetString("OPERATION_NUMBER"),

                        // OLD NODE: label = control key (PP14)
                        Label = orow.GetString("OPR_CNTRL_KEY"),

                        // OLD NODE: operationKey = work center (4013-419)
                        OperationKey = workCenter,

                        // Delphi: nazOper[ii] := checkSlovar(tabl.value[i,14]);
                        Name = orow.GetString("DESCRIPTION"),

                        // Delphi: potrOper[ii] := tabl.value[i,4];
                        Confirmation = orow.GetString("CONF_NO")
                    };

                    // === machines from OprStroj: same as Delphi Fpovezi.GetStroj(opr, list) ===
                    try
                    {
                        var oprStrojService = new OprStrojService();
                        var machines = oprStrojService.GetMachinesForOperation(workCenter);

                        foreach (var m in machines)
                        {
                            op.Operation_Machines.Add(new OperationMachineDto
                            {
                                MachineKey = m.MachineAltKey,   // 10001812

                                MachineAltKey = m.MachineKey    // 21051
                            });
                        }
                    }
                    catch (Exception ex)
                    {                    }

                    dto.Operations.Add(op);
                }
            }


            return dto;
        }

        public bool OrderExists(string orderNumber)
        {
            if (string.IsNullOrWhiteSpace(orderNumber))
                return false;

            var dest = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            var repo = dest.Repository;

            var func = repo.CreateFunction("BAPI_PRODORD_GET_LIST");

            var orderRange = func.GetTable("ORDER_NUMBER_RANGE");
            orderRange.Append();
            orderRange.SetValue("SIGN", "I");
            orderRange.SetValue("OPTION", "EQ");
            var order12 = orderNumber.Trim().PadLeft(12, '0');
            orderRange.SetValue("LOW", order12);

            var plantRange = func.GetTable("PRODPLANT_RANGE");
            plantRange.Append();
            plantRange.SetValue("SIGN", "I");
            plantRange.SetValue("OPTION", "EQ");
            plantRange.SetValue("LOW", "1061");

            func.Invoke(dest);

            var header = func.GetTable("ORDER_HEADER");
            return header != null && header.RowCount > 0;
        }
        
        // helpers 

        private Dictionary<string, string> GetDimensionsBulk(RfcDestination dest, IEnumerable<string> materials)
        {
            var result = new Dictionary<string, string>();
            var matList = materials
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim().PadLeft(18, '0'))
                .Distinct()
                .ToList();

            if (matList.Count == 0)
                return result;

            var repo = dest.Repository;
            var func = repo.CreateFunction("RFC_READ_TABLE");

            func.SetValue("QUERY_TABLE", "MARA");
            func.SetValue("DELIMITER", "|");

            var fields = func.GetTable("FIELDS");
            fields.Append();
            fields.SetValue("FIELDNAME", "MATNR");
            fields.Append();
            fields.SetValue("FIELDNAME", "GROES");

            var opts = func.GetTable("OPTIONS");

            // Build WHERE: ( MATNR = '...' OR MATNR = '...' ... )
            bool first = true;
            foreach (var mat in matList)
            {
                var cond = (first ? " ( MATNR = '" : " OR MATNR = '") + mat + "'";
                opts.Append();
                opts.SetValue("TEXT", cond);
                first = false;
            }
            opts.Append();
            opts.SetValue("TEXT", " )");

            func.Invoke(dest);

            var data = func.GetTable("DATA");
            for (int i = 0; i < data.RowCount; i++)
            {
                var line = data[i].GetString("WA");
                var parts = line.Split('|');
                if (parts.Length < 2)
                    continue;

                var matnr = parts[0].Trim();   // 18-char
                var groes = parts[1].Trim();

                if (!string.IsNullOrWhiteSpace(matnr) && !result.ContainsKey(matnr))
                {
                    result[matnr] = string.IsNullOrWhiteSpace(groes) ? null : groes;
                }
            }

            return result;
        }

        private Dictionary<string, string> GetMaterialNamesSlBulk(RfcDestination dest, IEnumerable<string> materials)
        {
            var result = new Dictionary<string, string>();
            var matList = materials
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim().PadLeft(18, '0'))
                .Distinct()
                .ToList();

            if (matList.Count == 0)
                return result;

            var repo = dest.Repository;
            var func = repo.CreateFunction("RFC_READ_TABLE");

            func.SetValue("QUERY_TABLE", "MAKT");
            func.SetValue("DELIMITER", "|");

            var fields = func.GetTable("FIELDS");
            fields.Append();
            fields.SetValue("FIELDNAME", "MATNR");
            fields.Append();
            fields.SetValue("FIELDNAME", "MAKTX");

            var opts = func.GetTable("OPTIONS");

            // WHERE: ( MATNR = '...' OR MATNR = '...' ... ) AND SPRAS = '5'
            bool first = true;
            foreach (var mat in matList)
            {
                var cond = (first ? " ( MATNR = '" : " OR MATNR = '") + mat + "'";
                opts.Append();
                opts.SetValue("TEXT", cond);
                first = false;
            }
            opts.Append();
            opts.SetValue("TEXT", " ) AND SPRAS = '5'");

            func.Invoke(dest);

            var data = func.GetTable("DATA");
            for (int i = 0; i < data.RowCount; i++)
            {
                var line = data[i].GetString("WA");
                var parts = line.Split('|');
                if (parts.Length < 2)
                    continue;

                var matnr = parts[0].Trim();   // 18-char
                var maktx = parts[1].Trim();

                if (!string.IsNullOrWhiteSpace(matnr) && !result.ContainsKey(matnr))
                {
                    result[matnr] = string.IsNullOrWhiteSpace(maktx) ? null : maktx;
                }
            }

            return result;
        }

    }
}
