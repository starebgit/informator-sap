using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Linq;
using InformatorSAP.Classes;
using SAP.Middleware.Connector;

namespace InformatorSAP.Services
{
    public class SapStockService
    {
        private readonly RfcDestination _destination;
        private readonly RfcRepository _repository;
        private static readonly ConcurrentDictionary<string, CacheEntry> PlannedCache =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan PlannedCacheTtl = TimeSpan.FromMinutes(3);

        public SapStockService()
        {
            // Reuse existing destination configuration/auth setup.
            var _ = new SapService();
            _destination = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            _repository = _destination.Repository;
        }

        private sealed class CacheEntry
        {
            public DateTime CreatedUtc { get; set; }
            public decimal Total { get; set; }
            public string Unit { get; set; }
        }

        public StockSummaryDto GetUnrestrictedStockSummary(string werks, string lgort, string query, bool includePlanned = true)
        {
            var sw = Stopwatch.StartNew();
            Trace.WriteLine($"[SapStockService] START werks={werks}, lgort={lgort}, query={query}, includePlanned={includePlanned}");

            var normalizedQuery = (query ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(werks)) throw new ArgumentException("WERKS is required.");
            if (string.IsNullOrWhiteSpace(lgort)) throw new ArgumentException("LGORT is required.");
            if (string.IsNullOrWhiteSpace(normalizedQuery)) throw new ArgumentException("query is required.");

            var matchingMaterials = GetMatchingMaterialsByShortText(normalizedQuery);
            Trace.WriteLine($"[SapStockService] matchingMaterials={matchingMaterials.Count}");
            if (matchingMaterials.Count == 0)
            {
                sw.Stop();
                Trace.WriteLine($"[SapStockService] DONE in {sw.ElapsedMilliseconds} ms (no matching materials)");

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
            Trace.WriteLine($"[SapStockService] stockTotal={total}, stockUnit={unit}, contributingMaterials={contributingMaterials.Count}");

            var planned = includePlanned
                ? CalculatePlannedTotalBatched(matchingMaterials, werks)
                : (0m, (string)null);

            sw.Stop();
            Trace.WriteLine($"[SapStockService] DONE in {sw.ElapsedMilliseconds} ms plannedTotal={planned.Item1} plannedUnit={planned.Item2}");

            return new StockSummaryDto
            {
                WERKS = werks,
                LGORT = lgort,
                Query = query,
                Total = total,
                Unit = unit,
                PlannedTotal = planned.Item1,
                PlannedUnit = planned.Item2
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


        private (decimal Total, string Unit) CalculatePlannedTotalBatched(HashSet<string> materialNumbers, string werks)
        {
            if (materialNumbers == null || materialNumbers.Count == 0)
                return (0m, null);

            var cacheKey = BuildPlannedCacheKey(werks, materialNumbers);
            CacheEntry cached;
            if (PlannedCache.TryGetValue(cacheKey, out cached))
            {
                if (DateTime.UtcNow - cached.CreatedUtc <= PlannedCacheTtl)
                {
                    Trace.WriteLine($"[SapStockService] planned cache HIT key={cacheKey}");
                    return (cached.Total, cached.Unit);
                }
            }

            Trace.WriteLine($"[SapStockService] planned cache MISS; calculating batched planned total for {materialNumbers.Count} materials");

            var mats18 = materialNumbers.Select(m => (m ?? string.Empty).Trim().PadLeft(18, '0')).Distinct(StringComparer.Ordinal).ToList();
            var perOrderUnit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var orderToMaterials = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var perMaterialPlanned = new Dictionary<string, decimal>(StringComparer.Ordinal);

            // 1) AFPO batched: get candidate orders (and fallback unit hints).
            var candidateOrders = new HashSet<string>(StringComparer.Ordinal);
            const int matChunk = 30;
            for (int i = 0; i < mats18.Count; i += matChunk)
            {
                var slice = mats18.Skip(i).Take(matChunk).ToList();
                var inList = string.Join(",", slice.Select(m => "'" + EscapeForWhere(m) + "'"));

                var rows = ReadTable(
                    "AFPO",
                    new[] { "AUFNR", "MATNR", "MEINS" },
                    BuildWhereOptions($"DWERK = '{EscapeForWhere(werks)}' AND MATNR IN ( {inList} )"));

                foreach (var row in rows)
                {
                    var aufnr = SafeGet(row, 0);
                    var matnr = SafeGet(row, 1);
                    var meins = SafeGet(row, 2);

                    if (string.IsNullOrWhiteSpace(aufnr) || string.IsNullOrWhiteSpace(matnr))
                        continue;

                    if (!mats18.Contains(matnr, StringComparer.Ordinal))
                        continue;

                    candidateOrders.Add(aufnr.PadLeft(12, '0'));
                    var key = aufnr.PadLeft(12, '0');

                    HashSet<string> matsForOrder;
                    if (!orderToMaterials.TryGetValue(key, out matsForOrder))
                    {
                        matsForOrder = new HashSet<string>(StringComparer.Ordinal);
                        orderToMaterials[key] = matsForOrder;
                    }
                    matsForOrder.Add(matnr);

                    if (!string.IsNullOrWhiteSpace(meins) && !perOrderUnit.ContainsKey(key))
                        perOrderUnit[key] = meins;
                }

                Trace.WriteLine($"[SapStockService] planned AFPO chunk {i / matChunk + 1}: rows={rows.Count}, candidateOrders={candidateOrders.Count}");
            }

            if (candidateOrders.Count == 0)
            {
                PlannedCache[cacheKey] = new CacheEntry { CreatedUtc = DateTime.UtcNow, Total = 0m, Unit = null };
                return (0m, null);
            }

            // 2) CAUFV batched: AUFNR -> OBJNR.
            var orderList = candidateOrders.ToList();
            var objByOrder = new Dictionary<string, string>(StringComparer.Ordinal);
            const int orderChunk = 120;
            for (int i = 0; i < orderList.Count; i += orderChunk)
            {
                var slice = orderList.Skip(i).Take(orderChunk).ToList();
                var inList = string.Join(",", slice.Select(a => "'" + EscapeForWhere(a) + "'"));
                var rows = ReadTable(
                    "CAUFV",
                    new[] { "AUFNR", "OBJNR" },
                    BuildWhereOptions($"AUFNR IN ( {inList} )"));

                foreach (var row in rows)
                {
                    var aufnr = SafeGet(row, 0)?.PadLeft(12, '0');
                    var objnr = SafeGet(row, 1);
                    if (!string.IsNullOrWhiteSpace(aufnr) && !string.IsNullOrWhiteSpace(objnr))
                        objByOrder[aufnr] = objnr;
                }
            }

            if (objByOrder.Count == 0)
            {
                PlannedCache[cacheKey] = new CacheEntry { CreatedUtc = DateTime.UtcNow, Total = 0m, Unit = null };
                return (0m, null);
            }

            // 3) JEST: check active released status (I0002) per OBJNR.
            // NOTE: avoids long IN(...) OPTION strings that can trigger OPTION_NOT_VALID on some systems.
            var releasedOrders = new HashSet<string>(StringComparer.Ordinal);
            var objToOrder = objByOrder.ToDictionary(k => k.Value, v => v.Key, StringComparer.Ordinal);
            var objList = objByOrder.Values.Distinct().ToList();

            for (int i = 0; i < objList.Count; i++)
            {
                var objnr = objList[i];
                var rows = ReadTable(
                    "JEST",
                    new[] { "STAT", "INACT" },
                    new[] { "OBJNR = '" + EscapeForWhere(objnr) + "'", "AND STAT = 'I0002'", "AND INACT = ' '" });

                if (rows.Count > 0)
                {
                    string order;
                    if (objToOrder.TryGetValue(objnr, out order))
                        releasedOrders.Add(order);
                }

                if ((i + 1) % 100 == 0)
                {
                    Trace.WriteLine($"[SapStockService] planned JEST progress {i + 1}/{objList.Count}, releasedOrders={releasedOrders.Count}");
                }
            }

            if (releasedOrders.Count == 0)
            {
                PlannedCache[cacheKey] = new CacheEntry { CreatedUtc = DateTime.UtcNow, Total = 0m, Unit = null };
                return (0m, null);
            }

            // 4) AFKO batched: planned qty + unit.
            decimal plannedTotal = 0m;
            string plannedUnit = null;
            var releasedList = releasedOrders.ToList();

            for (int i = 0; i < releasedList.Count; i += orderChunk)
            {
                var slice = releasedList.Skip(i).Take(orderChunk).ToList();
                var inList = string.Join(",", slice.Select(a => "'" + EscapeForWhere(a) + "'"));
                var rows = ReadTable(
                    "AFKO",
                    new[] { "AUFNR", "GAMNG", "GMEIN" },
                    BuildWhereOptions($"AUFNR IN ( {inList} )"));

                foreach (var row in rows)
                {
                    var aufnr = SafeGet(row, 0)?.PadLeft(12, '0');
                    var gamngRaw = SafeGet(row, 1);
                    var gmein = SafeGet(row, 2);
                    if (string.IsNullOrWhiteSpace(aufnr)) continue;

                    var planned = ParseQuanScaled(gamngRaw);
                    plannedTotal += planned;

                    HashSet<string> matsForOrder;
                    if (orderToMaterials.TryGetValue(aufnr, out matsForOrder))
                    {
                        foreach (var mat in matsForOrder)
                        {
                            if (!perMaterialPlanned.ContainsKey(mat)) perMaterialPlanned[mat] = 0m;
                            perMaterialPlanned[mat] += planned;
                        }
                    }

                    var candidateUnit = !string.IsNullOrWhiteSpace(gmein)
                        ? gmein
                        : (perOrderUnit.ContainsKey(aufnr) ? perOrderUnit[aufnr] : null);

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

            foreach (var kv in perMaterialPlanned.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                Trace.WriteLine($"[SapStockService] planned material total MATNR={kv.Key} planned={kv.Value}");
            }

            PlannedCache[cacheKey] = new CacheEntry
            {
                CreatedUtc = DateTime.UtcNow,
                Total = plannedTotal,
                Unit = plannedUnit
            };

            return (plannedTotal, plannedUnit);
        }

        private static decimal ParseQuanScaled(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0m;

            var v = value.Trim();
            decimal d;

            if (v.IndexOf('.') < 0 && v.IndexOf(',') < 0)
            {
                if (decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                    return d / 1000m;
            }

            if (decimal.TryParse(v.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                return d;

            return 0m;
        }

        private static string BuildPlannedCacheKey(string werks, HashSet<string> materialNumbers)
        {
            var mats = materialNumbers.Select(x => (x ?? string.Empty).Trim()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            return (werks ?? string.Empty).Trim() + "|" + string.Join(",", mats);
        }

        private static string[] BuildWhereOptions(string where)
        {
            if (string.IsNullOrWhiteSpace(where)) return new string[0];

            var parts = new List<string>();
            var line = where.StartsWith(" ") ? where : " " + where;
            for (int i = 0; i < line.Length; i += 72)
            {
                parts.Add(line.Substring(i, Math.Min(72, line.Length - i)));
            }
            return parts.ToArray();
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
