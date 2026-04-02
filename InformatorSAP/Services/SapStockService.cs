using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InformatorSAP.Classes;
using SAP.Middleware.Connector;

namespace InformatorSAP.Services
{
    public class SapStockService
    {
        private readonly RfcDestination _destination;
        private readonly RfcRepository _repository;
        private readonly SapService _sapService;

        public SapStockService()
        {
            // Reuse existing destination configuration/auth setup.
            _sapService = new SapService();
            _destination = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            _repository = _destination.Repository;
        }

        public StockSummaryDto GetUnrestrictedStockSummary(string werks, string lgort, string query)
        {
            var normalizedQuery = (query ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(werks)) throw new ArgumentException("WERKS is required.");
            if (string.IsNullOrWhiteSpace(lgort)) throw new ArgumentException("LGORT is required.");
            if (string.IsNullOrWhiteSpace(normalizedQuery)) throw new ArgumentException("query is required.");

            var matchingMaterials = GetMatchingMaterialsByShortText(normalizedQuery);
            if (matchingMaterials.Count == 0)
            {
                return new StockSummaryDto
                {
                    WERKS = werks,
                    LGORT = lgort,
                    Query = query,
                    Total = 0m,
                    Unit = null,
                    PlannedTotal = 0m,
                    PlannedUnit = null
                };
            }

            decimal total = 0m;
            var contributingMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var stockRows = ReadTable(
                "MARD",
                new[] { "MATNR", "LABST" },
                new[]
                {
                    "WERKS = '" + EscapeForWhere(werks) + "'",
                    "AND LGORT = '" + EscapeForWhere(lgort) + "'"
                });

            foreach (var row in stockRows)
            {
                var matnr = SafeGet(row, 0);
                var labstRaw = SafeGet(row, 1);

                if (string.IsNullOrWhiteSpace(matnr) || !matchingMaterials.Contains(matnr))
                    continue;

                var qty = ParseSapDecimal(labstRaw);
                if (qty <= 0m)
                    continue;

                total += qty;
                contributingMaterials.Add(matnr);
            }

            var unit = ResolveBaseUnit(contributingMaterials);
            var planned = CalculatePlannedTotal(matchingMaterials, werks);

            return new StockSummaryDto
            {
                WERKS = werks,
                LGORT = lgort,
                Query = query,
                Total = total,
                Unit = unit,
                PlannedTotal = planned.Total,
                PlannedUnit = planned.Unit
            };
        }

        private HashSet<string> GetMatchingMaterialsByShortText(string query)
        {
            var up = query.ToUpperInvariant();
            var lo = query.ToLowerInvariant();

            var rows = ReadTable(
                "MAKT",
                new[] { "MATNR", "MAKTX" },
                new[]
                {
                    "MAKTX LIKE '%" + EscapeForLike(up) + "%'",
                    "OR MAKTX LIKE '%" + EscapeForLike(lo) + "%'"
                });

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var matnr = SafeGet(row, 0);
                var shortText = SafeGet(row, 1);

                if (string.IsNullOrWhiteSpace(matnr) || string.IsNullOrWhiteSpace(shortText))
                    continue;

                if (shortText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    set.Add(matnr);
            }

            return set;
        }

        private string ResolveBaseUnit(HashSet<string> materialNumbers)
        {
            if (materialNumbers == null || materialNumbers.Count == 0)
                return null;

            var unitCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var matnr in materialNumbers)
            {
                var rows = ReadTable(
                    "MARA",
                    new[] { "MEINS" },
                    new[] { "MATNR = '" + EscapeForWhere(matnr) + "'" },
                    rowCount: 1);

                if (rows.Count == 0)
                    continue;

                var unit = SafeGet(rows[0], 0);
                if (string.IsNullOrWhiteSpace(unit))
                    continue;

                if (!unitCount.ContainsKey(unit)) unitCount[unit] = 0;
                unitCount[unit]++;
            }

            if (unitCount.Count == 0)
                return null;

            return unitCount.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First().Key;
        }


        private (decimal Total, string Unit) CalculatePlannedTotal(HashSet<string> materialNumbers, string werks)
        {
            if (materialNumbers == null || materialNumbers.Count == 0)
                return (0m, null);

            decimal plannedTotal = 0m;
            string plannedUnit = null;

            foreach (var matnr in materialNumbers)
            {
                var orders = _sapService.GetOpenOrdersForMaterial(matnr, werks, "SL", 0, includeDisplayInfo: true)
                             ?? new List<OpenOrderId>();

                foreach (var order in orders)
                {
                    var qty = order.Quantity ?? 0m;
                    var delivered = order.Delivered ?? 0m;
                    var remaining = qty - delivered;
                    if (remaining < 0m) remaining = 0m;

                    plannedTotal += remaining;

                    var candidateUnit = string.IsNullOrWhiteSpace(order.Unit)
                        ? order.DeliveredUnit
                        : order.Unit;

                    if (string.IsNullOrWhiteSpace(candidateUnit))
                        continue;

                    if (string.IsNullOrWhiteSpace(plannedUnit))
                    {
                        plannedUnit = candidateUnit;
                    }
                    else if (!string.Equals(plannedUnit, candidateUnit, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Planned quantity has mixed units for selected materials; cannot return a single PlannedUnit.");
                    }
                }
            }

            return (plannedTotal, plannedUnit);
        }

        private List<string[]> ReadTable(string table, string[] fields, string[] options, int rowCount = 0)
        {
            var fn = _repository.CreateFunction("RFC_READ_TABLE");
            fn.SetValue("QUERY_TABLE", table);
            fn.SetValue("DELIMITER", "|");
            if (rowCount > 0)
                fn.SetValue("ROWCOUNT", rowCount);

            var f = fn.GetTable("FIELDS");
            foreach (var field in fields)
            {
                f.Append();
                f.SetValue("FIELDNAME", field);
            }

            var o = fn.GetTable("OPTIONS");
            foreach (var option in options)
            {
                if (string.IsNullOrWhiteSpace(option)) continue;
                o.Append();
                o.SetValue("TEXT", option);
            }

            fn.Invoke(_destination);

            var data = fn.GetTable("DATA");
            var rows = new List<string[]>();
            foreach (IRfcStructure row in data)
            {
                rows.Add(row.GetString("WA").Split('|'));
            }

            return rows;
        }

        private static string SafeGet(string[] row, int index)
        {
            if (row == null || index < 0 || index >= row.Length) return null;
            return row[index].Trim();
        }

        private static decimal ParseSapDecimal(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0m;

            var v = value.Trim();
            decimal parsed;

            if (decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                return parsed;

            if (decimal.TryParse(v, NumberStyles.Any, new CultureInfo("sl-SI"), out parsed))
                return parsed;

            if (decimal.TryParse(v, NumberStyles.Any, new CultureInfo("de-DE"), out parsed))
                return parsed;

            v = v.Replace(".", string.Empty).Replace(',', '.');
            if (decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                return parsed;

            return 0m;
        }

        private static string EscapeForWhere(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private static string EscapeForLike(string value)
        {
            return EscapeForWhere(value);
        }
    }
}
