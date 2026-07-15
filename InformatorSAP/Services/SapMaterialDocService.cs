using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using InformatorSAP.Classes;
using SAP.Middleware.Connector;

namespace InformatorSAP.Services
{
    /// <summary>
    /// Replicates the custom transaction ZPP_0117 ("Summary material document list",
    /// an MB51-style goods-movement report). The ABAP report does a single
    /// MSEG INNER JOIN MBEW/MARC/MARA select; RFC_READ_TABLE cannot join, so we read
    /// MSEG with its filters and resolve MBEW/MARC/MARA/MAKT/LFA1/T006B via batched
    /// IN(...) lookups, then join + filter + aggregate in .NET.
    /// </summary>
    public class SapMaterialDocService
    {
        private readonly RfcDestination _destination;
        private readonly RfcRepository _repository;
        private const int MatChunk = 100;

        // Hard safety cap on the detail payload. ZPP_0117 for a single day returns a
        // few thousand rows; a whole year is ~58k and big enough to choke a browser.
        // If a query exceeds this we refuse rather than stream a payload that breaks
        // the client. Summary rows are aggregated and never hit this.
        private const int MaxDetailRows = 25000;

        // Hard ceiling on rows pulled from SAP per RFC_READ_TABLE call (passed as
        // ROWCOUNT). Protects SAP from a runaway full-table scan and the app from
        // loading an unbounded result into memory. We request one extra row so we can
        // detect "ceiling hit" and fail loudly instead of silently truncating.
        private const int MaxSapRows = 200000;

        // Upper bound on RFC_READ_TABLE wall-clock time before we abort the call,
        // so a pathological query can never hang the worker thread indefinitely.
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(120);

        public SapMaterialDocService()
        {
            var _ = new SapService(); // ensures destination configuration is registered
            _destination = RfcDestinationManager.GetDestination("INFORMATOR_SAP");
            _repository = _destination.Repository;
        }

        public MaterialDocResult GetMaterialMovements(MaterialDocQuery q)
        {
            if (q == null) throw new ArgumentException("Query is required.");
            if (string.IsNullOrWhiteSpace(q.Mjahr)) throw new ArgumentException("mjahr is required.");

            var sw = Stopwatch.StartNew();

            string werks = (string.IsNullOrWhiteSpace(q.Werks) ? "1061" : q.Werks).Trim();
            string bukrs = string.IsNullOrWhiteSpace(q.Bukrs) ? "1060" : q.Bukrs.Trim();
            string spras = ResolveSpras(q.Lang);

            // Mirror the ZPP_0117 selection screen: "Datum knjiženja" defaults to today
            // (a single day). Without this the query scans the whole MJAHR year and
            // returns tens of thousands of rows, which is what overloaded the browser.
            string budatFrom = q.BudatFrom;
            string budatTo = q.BudatTo;
            if (string.IsNullOrWhiteSpace(budatFrom) && string.IsNullOrWhiteSpace(budatTo))
            {
                budatFrom = DateTime.Today.ToString("yyyyMMdd");
                budatTo = budatFrom;
            }

            // ---------- 1) MSEG read (all MSEG-level filters in the WHERE) ----------
            var where = new List<string> { "WERKS = '" + Esc(werks) + "'" };
            AddIn(where, "MJAHR", q.Mjahr, 0);
            AddIn(where, "BUKRS", q.Bukrs == null ? bukrs : q.Bukrs, 0);
            AddIn(where, "MATNR", q.Matnr, 18);
            AddIn(where, "AUFNR", q.Aufnr, 12);
            AddIn(where, "BWART", q.Bwart, 0);
            AddIn(where, "SOBKZ", q.Sobkz, 0);
            AddIn(where, "LGORT", q.Lgort, 0);
            AddIn(where, "LIFNR", q.Lifnr, 10);
            AddIn(where, "EBELN", q.Ebeln, 10);
            AddDateRange(where, "BUDAT_MKPF", budatFrom, budatTo);

            var msegRows = ReadTable(
                "MSEG",
                new[]
                {
                    "MATNR", "WERKS", "AUFNR", "BWART", "LGORT", "SHKZG", "BWTAR",
                    "MENGE", "MEINS", "DMBTR", "WAERS", "ZEILE", "LIFNR",
                    "MJAHR", "EBELN", "BUDAT_MKPF", "MBLNR"
                },
                BuildWhereOptions(string.Join(" AND ", where)),
                MaxSapRows);

            Trace.WriteLine($"[SapMaterialDocService] MSEG rows={msegRows.Count} werks={werks}");

            if (msegRows.Count == 0)
                return EmptyResult(q.Summary);

            // Hit the SAP read ceiling => the selection is far too broad. Refuse loudly
            // rather than process a truncated/huge set (protects SAP and the app).
            if (msegRows.Count >= MaxSapRows)
                throw new MaterialDocTooLargeException(
                    $"Query matched the SAP read ceiling of {MaxSapRows:n0} rows. Narrow the posting-date range " +
                    "(Datum knjiženja) or add filters such as material, movement type or storage location.");

            // Collect keys for the master-data lookups.
            var materials = new HashSet<string>(StringComparer.Ordinal);
            var units = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in msegRows)
            {
                var matnr = Get(r, 0).PadLeft(18, '0');
                if (matnr.Trim('0').Length > 0) materials.Add(matnr);
                var meins = Get(r, 8);
                if (!string.IsNullOrEmpty(meins)) units.Add(meins);
            }

            // ---------- 2) Master-data lookups (batched IN) ----------
            var mbew = LookupMbew(materials, werks);                // key: matnr18|bwtar -> (bklas)
            var marc = LookupMarc(materials, werks);                // key: matnr18 -> (prctr, sobsl)
            var mara = LookupMara(materials);                       // key: matnr18 (MARA inner join only)
            var makt = LookupMakt(materials, spras);                // key: matnr18 -> maktx
            var t006b = LookupUnits(units, spras);                  // key: meins -> external mseh3

            var bklasFilter = ToSet(q.Bklas);
            var prctrFilter = ToSet(q.Prctr, 10);
            var sobslFilter = ToSet(q.Sobsl);

            // ---------- 3) Join + filter (INNER JOIN semantics: drop on missing master data) ----------
            var detail = new List<MaterialDocRowDto>();
            foreach (var r in msegRows)
            {
                var matnr18 = Get(r, 0).PadLeft(18, '0');
                var bwtar = Get(r, 6);

                MbewInfo mb;
                if (!mbew.TryGetValue(matnr18 + "|" + bwtar, out mb)) continue;     // MBEW inner join
                MarcInfo mc;
                if (!marc.TryGetValue(matnr18, out mc)) continue;                   // MARC inner join
                if (!mara.Contains(matnr18)) continue;                              // MARA inner join

                if (bklasFilter != null && !bklasFilter.Contains(mb.Bklas)) continue;
                if (prctrFilter != null && !prctrFilter.Contains((mc.Prctr ?? "").PadLeft(10, '0'))) continue;
                if (sobslFilter != null && !sobslFilter.Contains(mc.Sobsl ?? "")) continue;

                var shkzg = Get(r, 5);
                decimal sign = shkzg == "H" ? -1m : 1m;
                decimal menge = ParseScaled(Get(r, 7), 3) * sign;
                decimal dmbtr = ParseScaled(Get(r, 9), 2) * sign;

                var meinsRaw = Get(r, 8);
                string meinsExt = t006b.TryGetValue(meinsRaw, out var ext) && !string.IsNullOrEmpty(ext) ? ext : meinsRaw;

                var budatRaw = Get(r, 15);
                string period = budatRaw.Length >= 6 ? budatRaw.Substring(0, 6) : budatRaw;

                makt.TryGetValue(matnr18, out var maktx);

                var row = new MaterialDocRowDto
                {
                    Period = period,
                    PostingDate = FormatDate(budatRaw),
                    Material = StripAlpha(matnr18),
                    MaterialText = maktx,
                    ValuationClass = mb.Bklas,
                    MovementType = Get(r, 3),
                    StorageLocation = Get(r, 4),
                    ValuationType = bwtar,
                    Quantity = menge,
                    Unit = meinsExt,
                    Amount = dmbtr,
                    Currency = Get(r, 10),
                    Item = StripAlpha(Get(r, 11)),
                    Vendor = StripAlpha(Get(r, 12)),
                    DocYear = Get(r, 13),
                    MaterialDoc = StripAlpha(Get(r, 16)),
                    PurchaseOrder = StripAlpha(Get(r, 14)),
                    ProfitCenter = StripAlpha(mc.Prctr),
                    SpecialProcurement = mc.Sobsl
                };
                detail.Add(row);

                if (!q.Summary && detail.Count > MaxDetailRows)
                    throw new MaterialDocTooLargeException(
                        $"Query matches more than {MaxDetailRows} rows. Narrow the posting-date range " +
                        "(Datum knjiženja) or add filters such as material, movement type or storage location.");
            }

            Trace.WriteLine($"[SapMaterialDocService] mseg={msegRows.Count} detail rows={detail.Count} in {sw.ElapsedMilliseconds} ms");

            if (!q.Summary)
            {
                return new MaterialDocResult { Summary = false, Count = detail.Count, Rows = detail };
            }

            // ---------- 4) Summary (ABAP COLLECT equivalent) ----------
            var summary = detail
                .GroupBy(d => new
                {
                    d.MovementType, d.StorageLocation, d.ValuationType,
                    d.Unit, d.Currency, d.Item, d.DocYear, d.PurchaseOrder,
                    d.MaterialDoc, d.Period, d.ValuationClass, d.ProfitCenter, d.SpecialProcurement
                })
                .Select(g => new MaterialDocSummaryRowDto
                {
                    Plant = werks,
                    MovementType = g.Key.MovementType,
                    StorageLocation = g.Key.StorageLocation,
                    ValuationType = g.Key.ValuationType,
                    Quantity = g.Sum(x => x.Quantity),
                    Unit = g.Key.Unit,
                    Amount = g.Sum(x => x.Amount),
                    Currency = g.Key.Currency,
                    Item = g.Key.Item,
                    DocYear = g.Key.DocYear,
                    PurchaseOrder = g.Key.PurchaseOrder,
                    MaterialDoc = g.Key.MaterialDoc,
                    Period = g.Key.Period,
                    ValuationClass = g.Key.ValuationClass,
                    ProfitCenter = g.Key.ProfitCenter,
                    SpecialProcurement = g.Key.SpecialProcurement
                })
                .ToList();

            Trace.WriteLine($"[SapMaterialDocService] summary rows={summary.Count} in {sw.ElapsedMilliseconds} ms");
            return new MaterialDocResult { Summary = true, Count = summary.Count, SummaryRows = summary };
        }

        // ===================== Izmet / SO ratio report =====================
        //
        // Replicates the two pivot tables in "Izmet HP.xlsx" (both sum the signed
        // MSEG-DMBTR, "Znes.v dom.val."), but per posting day instead of per week.
        //   SO HP    (shop output): BKLAS=4100, BWART in (101,102),    LGORT=0027
        //   Izmet HP (scrap/waste): BKLAS=3100, BWART in (551,552),    LGORT in (0013,0016), unit KOS/KG
        // Ratio = Izmet / SO. The material-text row field in the pivots is display
        // only and does not affect the totals, so it is ignored here.
        private const string SoBklas = "4100";
        private const string IzmetBklas = "3100";
        private const string SoLgort = "0027";
        private static readonly HashSet<string> SoBwart = new HashSet<string>(StringComparer.Ordinal) { "101", "102" };
        private static readonly HashSet<string> IzmetBwart = new HashSet<string>(StringComparer.Ordinal) { "551", "552" };
        private static readonly HashSet<string> IzmetLgort = new HashSet<string>(StringComparer.Ordinal) { "0013", "0016" };
        private static readonly HashSet<string> IzmetUnits = new HashSet<string>(StringComparer.Ordinal) { "KOS", "KG" };

        // ---- Termostat / "Montaža 55.17" Izmet/SO, from "Termostat.xlsx" == "Termostatskupaj.xlsx" ----
        // Both workbooks are the same ZPP_0117 export (plant 1061, company 1060, year 2026,
        // profit center 11032005; Datum knjiženja 21.-28.06.2026) carrying two pivots that
        // both sum the signed MSEG-DMBTR ("Vsota od Znes.v dom.val."), with NO LGORT filter:
        //   Izmet (scrap):  BWART 551, BKLAS in {3100,4100}, restricted to the termostat
        //                   assembly materials (the pivot's row filter = EGO TERMOSTAT +
        //                   the two PODNOZJE assemblies; see IsTermostatScrapMaterial).
        //   SO / Donos:     BWART in {101,102}, BKLAS 4100, ALL materials.
        // Verified against the workbook's own Data tab (== the SAP source): for the export
        // scope above, Izmet = -1192.73 EUR (-1440), SO = 183733.26 EUR (90362.6).
        private const string TermSoBklas = "4100";
        private static readonly HashSet<string> TermSoBwart = new HashSet<string>(StringComparer.Ordinal) { "101", "102" };
        private const string TermIzmetBwart = "551";
        private static readonly HashSet<string> TermIzmetBklas = new HashSet<string>(StringComparer.Ordinal) { "3100", "4100" };

        public IzmetRatioResult GetIzmetRatio(IzmetRatioQuery q)
        {
            if (q == null) throw new ArgumentException("Query is required.");
            if (string.IsNullOrWhiteSpace(q.Mjahr)) throw new ArgumentException("mjahr is required.");

            string werks = (string.IsNullOrWhiteSpace(q.Werks) ? "1061" : q.Werks).Trim();
            string bukrs = string.IsNullOrWhiteSpace(q.Bukrs) ? "1060" : q.Bukrs.Trim();
            string spras = ResolveSpras(q.Lang);

            string budatFrom = q.BudatFrom;
            string budatTo = q.BudatTo;
            if (string.IsNullOrWhiteSpace(budatFrom) && string.IsNullOrWhiteSpace(budatTo))
            {
                budatFrom = DateTime.Today.ToString("yyyyMMdd");
                budatTo = budatFrom;
            }

            // Push the union of both pivots' BWART + LGORT into the MSEG WHERE so the
            // read stays small even over a multi-day range; BKLAS (MBEW) is applied
            // after the lookup.
            var where = new List<string> { "WERKS = '" + Esc(werks) + "'" };
            AddIn(where, "MJAHR", q.Mjahr, 0);
            AddIn(where, "BUKRS", bukrs, 0);
            AddIn(where, "BWART", "101,102,551,552", 0);
            AddIn(where, "LGORT", "0013,0016,0027", 0);
            AddDateRange(where, "BUDAT_MKPF", budatFrom, budatTo);

            var msegRows = ReadTable(
                "MSEG",
                new[] { "MATNR", "BWART", "LGORT", "SHKZG", "BWTAR", "MEINS", "DMBTR", "BUDAT_MKPF" },
                BuildWhereOptions(string.Join(" AND ", where)),
                MaxSapRows);

            if (msegRows.Count >= MaxSapRows)
                throw new MaterialDocTooLargeException(
                    $"Query matched the SAP read ceiling of {MaxSapRows:n0} rows. Narrow the posting-date range.");

            if (msegRows.Count == 0)
                return new IzmetRatioResult { Days = new List<IzmetDayRowDto>() };

            var materials = new HashSet<string>(StringComparer.Ordinal);
            var units = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in msegRows)
            {
                var matnr = Get(r, 0).PadLeft(18, '0');
                if (matnr.Trim('0').Length > 0) materials.Add(matnr);
                var meins = Get(r, 5);
                if (!string.IsNullOrEmpty(meins)) units.Add(meins);
            }

            // Same INNER JOIN semantics as ZPP_0117 (the Excel source): drop rows whose
            // material is missing from MBEW / MARC / MARA.
            var mbew = LookupMbew(materials, werks);
            var marc = LookupMarc(materials, werks);
            var mara = LookupMara(materials);
            var t006b = LookupUnits(units, spras);

            var prctrFilter = ToSet(q.Prctr, 10);

            // day (yyyymmdd) -> (so, izmet)
            var byDay = new Dictionary<string, decimal[]>(StringComparer.Ordinal);
            decimal soTotal = 0m, izmetTotal = 0m;

            foreach (var r in msegRows)
            {
                var matnr18 = Get(r, 0).PadLeft(18, '0');
                var bwtar = Get(r, 4);

                MbewInfo mb;
                if (!mbew.TryGetValue(matnr18 + "|" + bwtar, out mb)) continue;
                MarcInfo mc;
                if (!marc.TryGetValue(matnr18, out mc)) continue;
                if (!mara.Contains(matnr18)) continue;
                if (prctrFilter != null && !prctrFilter.Contains((mc.Prctr ?? "").PadLeft(10, '0'))) continue;

                var bwart = Get(r, 1);
                var lgort = Get(r, 2);
                var meinsRaw = Get(r, 5);
                string unit = t006b.TryGetValue(meinsRaw, out var ext) && !string.IsNullOrEmpty(ext) ? ext : meinsRaw;

                bool isSo = mb.Bklas == SoBklas && SoBwart.Contains(bwart) && lgort == SoLgort;
                bool isIzmet = mb.Bklas == IzmetBklas && IzmetBwart.Contains(bwart)
                               && IzmetLgort.Contains(lgort) && IzmetUnits.Contains(unit);
                if (!isSo && !isIzmet) continue;

                decimal sign = Get(r, 3) == "H" ? -1m : 1m;
                decimal amount = ParseScaled(Get(r, 6), 2) * sign;

                var day = Get(r, 7);
                if (!byDay.TryGetValue(day, out var acc)) { acc = new decimal[2]; byDay[day] = acc; }
                if (isSo) { acc[0] += amount; soTotal += amount; }
                else { acc[1] += amount; izmetTotal += amount; }
            }

            var days = byDay
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new IzmetDayRowDto
                {
                    PostingDate = FormatDate(kv.Key),
                    ShopOutput = kv.Value[0],
                    Izmet = kv.Value[1],
                    Ratio = kv.Value[0] == 0m ? 0m : kv.Value[1] / kv.Value[0],
                    RatioPercent = kv.Value[0] == 0m ? 0m : kv.Value[1] / kv.Value[0] * 100m
                })
                .ToList();

            return new IzmetRatioResult
            {
                ShopOutputTotal = soTotal,
                IzmetTotal = izmetTotal,
                Ratio = soTotal == 0m ? 0m : izmetTotal / soTotal,
                RatioPercent = soTotal == 0m ? 0m : izmetTotal / soTotal * 100m,
                Days = days
            };
        }

        // The Izmet pivot's material row filter selected exactly {EGO TERMOSTAT, PODNOZJE,
        // PODNOZJE SESTAV}. Matched here on the (SL) short text; kept ASCII so the source
        // carries no code-page-sensitive literals -- "PODNO" uniquely identifies the two
        // PODNOZJE assemblies, and "EGO TERMOSTAT" the finished thermostats.
        private static bool IsTermostatScrapMaterial(string maktx)
        {
            if (string.IsNullOrEmpty(maktx)) return false;
            return maktx == "EGO TERMOSTAT" || maktx.StartsWith("PODNO", StringComparison.Ordinal);
        }

        /// <summary>
        /// Termostat ("Montaza 55.17") Izmet vs SO report, per posting day, replicating the
        /// two pivots in "Termostat.xlsx"/"Termostatskupaj.xlsx". Feeds both new graphs:
        /// "Izmet 55.17 vrednostno" uses <see cref="IzmetDayRowDto.Izmet"/> (scrap EUR) and
        /// "Izmet Termostat / SO EUR" uses the ratio. Classification is fixed (see the
        /// Term* constants); scope (year, plant, company, posting-date range, profit center)
        /// comes from the query, exactly like <see cref="GetIzmetRatio"/>.
        /// </summary>
        public IzmetRatioResult GetTermostatIzmetRatio(IzmetRatioQuery q)
        {
            if (q == null) throw new ArgumentException("Query is required.");
            if (string.IsNullOrWhiteSpace(q.Mjahr)) throw new ArgumentException("mjahr is required.");

            string werks = (string.IsNullOrWhiteSpace(q.Werks) ? "1061" : q.Werks).Trim();
            string bukrs = string.IsNullOrWhiteSpace(q.Bukrs) ? "1060" : q.Bukrs.Trim();
            string spras = ResolveSpras(q.Lang);

            string budatFrom = q.BudatFrom;
            string budatTo = q.BudatTo;
            if (string.IsNullOrWhiteSpace(budatFrom) && string.IsNullOrWhiteSpace(budatTo))
            {
                budatFrom = DateTime.Today.ToString("yyyyMMdd");
                budatTo = budatFrom;
            }

            // Union of both pivots' movement types (no LGORT restriction). BKLAS (MBEW) and
            // the profit center (MARC) are applied after the master-data lookups.
            var where = new List<string> { "WERKS = '" + Esc(werks) + "'" };
            AddIn(where, "MJAHR", q.Mjahr, 0);
            AddIn(where, "BUKRS", bukrs, 0);
            AddIn(where, "BWART", "101,102,551", 0);
            AddDateRange(where, "BUDAT_MKPF", budatFrom, budatTo);

            var msegRows = ReadTable(
                "MSEG",
                new[] { "MATNR", "BWART", "SHKZG", "BWTAR", "DMBTR", "BUDAT_MKPF" },
                BuildWhereOptions(string.Join(" AND ", where)),
                MaxSapRows);

            if (msegRows.Count >= MaxSapRows)
                throw new MaterialDocTooLargeException(
                    $"Query matched the SAP read ceiling of {MaxSapRows:n0} rows. Narrow the posting-date range.");

            if (msegRows.Count == 0)
                return new IzmetRatioResult { Days = new List<IzmetDayRowDto>() };

            var materials = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in msegRows)
            {
                var matnr = Get(r, 0).PadLeft(18, '0');
                if (matnr.Trim('0').Length > 0) materials.Add(matnr);
            }

            // Same INNER JOIN semantics as ZPP_0117 (the Excel source): drop rows whose
            // material is missing from MBEW / MARC / MARA. MAKT gives the short text the
            // Izmet material filter keys off.
            var mbew = LookupMbew(materials, werks);
            var marc = LookupMarc(materials, werks);
            var mara = LookupMara(materials);
            var makt = LookupMakt(materials, spras);

            var prctrFilter = ToSet(q.Prctr, 10);

            // day (yyyymmdd) -> (so, izmet)
            var byDay = new Dictionary<string, decimal[]>(StringComparer.Ordinal);
            decimal soTotal = 0m, izmetTotal = 0m;

            foreach (var r in msegRows)
            {
                var matnr18 = Get(r, 0).PadLeft(18, '0');
                var bwtar = Get(r, 3);

                MbewInfo mb;
                if (!mbew.TryGetValue(matnr18 + "|" + bwtar, out mb)) continue;
                MarcInfo mc;
                if (!marc.TryGetValue(matnr18, out mc)) continue;
                if (!mara.Contains(matnr18)) continue;
                if (prctrFilter != null && !prctrFilter.Contains((mc.Prctr ?? "").PadLeft(10, '0'))) continue;

                var bwart = Get(r, 1);

                bool isSo = mb.Bklas == TermSoBklas && TermSoBwart.Contains(bwart);
                bool isIzmet = false;
                if (bwart == TermIzmetBwart && TermIzmetBklas.Contains(mb.Bklas))
                {
                    makt.TryGetValue(matnr18, out var maktx);
                    isIzmet = IsTermostatScrapMaterial(maktx);
                }
                if (!isSo && !isIzmet) continue;

                decimal sign = Get(r, 2) == "H" ? -1m : 1m;
                decimal amount = ParseScaled(Get(r, 4), 2) * sign;

                var day = Get(r, 5);
                if (!byDay.TryGetValue(day, out var acc)) { acc = new decimal[2]; byDay[day] = acc; }
                if (isSo) { acc[0] += amount; soTotal += amount; }
                else { acc[1] += amount; izmetTotal += amount; }
            }

            var days = byDay
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new IzmetDayRowDto
                {
                    PostingDate = FormatDate(kv.Key),
                    ShopOutput = kv.Value[0],
                    Izmet = kv.Value[1],
                    Ratio = kv.Value[0] == 0m ? 0m : kv.Value[1] / kv.Value[0],
                    RatioPercent = kv.Value[0] == 0m ? 0m : kv.Value[1] / kv.Value[0] * 100m
                })
                .ToList();

            return new IzmetRatioResult
            {
                ShopOutputTotal = soTotal,
                IzmetTotal = izmetTotal,
                Ratio = soTotal == 0m ? 0m : izmetTotal / soTotal,
                RatioPercent = soTotal == 0m ? 0m : izmetTotal / soTotal * 100m,
                Days = days
            };
        }

        // ===================== master-data lookups =====================

        private struct MbewInfo { public string Bklas; }
        private struct MarcInfo { public string Prctr; public string Sobsl; }

        private Dictionary<string, MbewInfo> LookupMbew(HashSet<string> materials, string werks)
        {
            var map = new Dictionary<string, MbewInfo>(StringComparer.Ordinal);
            foreach (var chunk in Chunk(materials))
            {
                var rows = ReadTable("MBEW",
                    new[] { "MATNR", "BWTAR", "BKLAS" },
                    BuildWhereOptions("BWKEY = '" + Esc(werks) + "' AND MATNR IN ( " + InList(chunk) + " )"));
                foreach (var r in rows)
                {
                    var key = Get(r, 0).PadLeft(18, '0') + "|" + Get(r, 1);
                    if (!map.ContainsKey(key)) map[key] = new MbewInfo { Bklas = Get(r, 2) };
                }
            }
            return map;
        }

        private Dictionary<string, MarcInfo> LookupMarc(HashSet<string> materials, string werks)
        {
            var map = new Dictionary<string, MarcInfo>(StringComparer.Ordinal);
            foreach (var chunk in Chunk(materials))
            {
                var rows = ReadTable("MARC",
                    new[] { "MATNR", "PRCTR", "SOBSL" },
                    BuildWhereOptions("WERKS = '" + Esc(werks) + "' AND MATNR IN ( " + InList(chunk) + " )"));
                foreach (var r in rows)
                {
                    var key = Get(r, 0).PadLeft(18, '0');
                    if (!map.ContainsKey(key)) map[key] = new MarcInfo { Prctr = Get(r, 1), Sobsl = Get(r, 2) };
                }
            }
            return map;
        }

        // MARA is only needed to honour ZPP_0117's INNER JOIN MARA (drop rows whose
        // material has no MARA record); no MARA field is displayed, so we just collect
        // the set of existing materials.
        private HashSet<string> LookupMara(HashSet<string> materials)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chunk in Chunk(materials))
            {
                var rows = ReadTable("MARA",
                    new[] { "MATNR" },
                    BuildWhereOptions("MATNR IN ( " + InList(chunk) + " )"));
                foreach (var r in rows)
                    set.Add(Get(r, 0).PadLeft(18, '0'));
            }
            return set;
        }

        private Dictionary<string, string> LookupMakt(HashSet<string> materials, string spras)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            void Fetch(string lang, IEnumerable<string> mats)
            {
                foreach (var chunk in Chunk(mats))
                {
                    var rows = ReadTable("MAKT",
                        new[] { "MATNR", "MAKTX" },
                        BuildWhereOptions("MATNR IN ( " + InList(chunk) + " ) AND SPRAS = '" + Esc(lang) + "'"));
                    foreach (var r in rows)
                    {
                        var key = Get(r, 0).PadLeft(18, '0');
                        if (!map.ContainsKey(key)) map[key] = Get(r, 1);
                    }
                }
            }
            Fetch(spras, materials);
            if (!string.Equals(spras, "E", StringComparison.OrdinalIgnoreCase))
            {
                var missing = materials.Where(m => !map.ContainsKey(m)).ToList();
                if (missing.Count > 0) Fetch("E", missing);
            }
            return map;
        }

        private Dictionary<string, string> LookupUnits(HashSet<string> units, string spras)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (units.Count == 0) return map;
            foreach (var chunk in Chunk(units))
            {
                var rows = ReadTable("T006B",
                    new[] { "MSEHI", "MSEH3" },
                    BuildWhereOptions("SPRAS = '" + Esc(spras) + "' AND MSEHI IN ( " + InList(chunk) + " )"));
                foreach (var r in rows)
                {
                    var key = Get(r, 0);
                    if (!map.ContainsKey(key)) map[key] = Get(r, 1);
                }
            }
            return map;
        }

        // ===================== RFC_READ_TABLE plumbing =====================

        private List<string[]> ReadTable(string table, string[] fields, string[] options, int rowCount = 0)
        {
            var fn = _repository.CreateFunction("RFC_READ_TABLE");
            fn.SetValue("QUERY_TABLE", table);
            fn.SetValue("DELIMITER", "|");
            if (rowCount > 0) fn.SetValue("ROWCOUNT", rowCount); // cap rows SAP returns

            var f = fn.GetTable("FIELDS");
            foreach (var field in fields) { f.Append(); f.SetValue("FIELDNAME", field); }

            var o = fn.GetTable("OPTIONS");
            foreach (var opt in options)
            {
                if (string.IsNullOrWhiteSpace(opt)) continue;
                o.Append(); o.SetValue("TEXT", opt);
            }

            Trace.WriteLine($"[SapMaterialDocService] {table} OPTIONS=[{string.Join(" ¶ ", options)}]");

            // Bound the call by wall-clock time so a pathological query can't hang the
            // request thread. The work runs on a pool thread; if it overruns we abort
            // the request (the ROWCOUNT ceiling keeps the abandoned call itself bounded).
            var task = System.Threading.Tasks.Task.Run(() => fn.Invoke(_destination));
            bool finished;
            try
            {
                finished = task.Wait(ReadTimeout);
            }
            catch (AggregateException ex)
            {
                var inner = ex.InnerException ?? ex;
                throw new InvalidOperationException(
                    $"RFC_READ_TABLE failed on {table}. WHERE=[{string.Join("¬", options)}] ({inner.Message})", inner);
            }
            if (!finished)
                throw new MaterialDocTooLargeException(
                    $"SAP read on {table} exceeded {ReadTimeout.TotalSeconds:n0}s. Narrow the posting-date range " +
                    "(Datum knjiženja) or add filters.");

            var data = fn.GetTable("DATA");
            var rows = new List<string[]>(data.Count);
            foreach (IRfcStructure row in data)
                rows.Add(row.GetString("WA").Split('|'));
            return rows;
        }

        // RFC_READ_TABLE concatenates the OPTIONS rows into a dynamic ABAP WHERE
        // clause, inserting a SPACE between consecutive rows. Cutting at a fixed
        // 72-char offset can split a token across two rows, so the injected space
        // lands inside it (e.g. "BUDAT_MK" + "PF" => "BUDAT_MK PF") and SAP raises
        // OPTION_NOT_VALID. Wrap on whitespace boundaries only, never exceeding the
        // 72-char TEXT width. IN(...) lists are built space-separated (see InList /
        // AddIn) so even long lists have break points.
        private static string[] BuildWhereOptions(string where)
        {
            if (string.IsNullOrWhiteSpace(where)) return new string[0];
            var parts = new List<string>();
            var sb = new StringBuilder();
            foreach (var token in where.Split(' '))
            {
                if (token.Length == 0) continue;
                if (sb.Length == 0)
                    sb.Append(token);
                else if (sb.Length + 1 + token.Length <= 72)
                    sb.Append(' ').Append(token);
                else
                {
                    parts.Add(sb.ToString());
                    sb.Clear();
                    sb.Append(token);
                }
            }
            if (sb.Length > 0) parts.Add(sb.ToString());
            return parts.ToArray();
        }

        // ===================== WHERE builders =====================

        // Adds "FIELD IN ( 'a','b' )" for a comma-separated list; nothing if empty.
        private static void AddIn(List<string> where, string field, string csv, int pad)
        {
            var vals = SplitCsv(csv, pad);
            if (vals.Count == 0) return;
            where.Add(field + " IN ( " + string.Join(", ", vals.Select(v => "'" + Esc(v) + "'")) + " )");
        }

        private static void AddDateRange(List<string> where, string field, string from, string to)
        {
            var lo = NormalizeDate(from);
            var hi = NormalizeDate(to);
            if (lo != null && hi != null)
                where.Add(field + " >= '" + lo + "' AND " + field + " <= '" + hi + "'");
            else if (lo != null)
                where.Add(field + " >= '" + lo + "'");
            else if (hi != null)
                where.Add(field + " <= '" + hi + "'");
        }

        private static string InList(IEnumerable<string> values)
        {
            return string.Join(", ", values.Select(v => "'" + Esc(v) + "'"));
        }

        private static List<string> SplitCsv(string csv, int pad)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(csv)) return result;
            foreach (var raw in csv.Split(','))
            {
                var v = raw.Trim();
                if (v.Length == 0) continue;
                if (pad > 0 && IsAllDigits(v) && v.Length < pad) v = v.PadLeft(pad, '0');
                result.Add(v);
            }
            return result;
        }

        private static HashSet<string> ToSet(string csv, int pad = 0)
        {
            var list = SplitCsv(csv, pad);
            return list.Count == 0 ? null : new HashSet<string>(list, StringComparer.Ordinal);
        }

        private static IEnumerable<List<string>> Chunk(IEnumerable<string> source)
        {
            var list = source as IList<string> ?? source.ToList();
            for (int i = 0; i < list.Count; i += MatChunk)
                yield return list.Skip(i).Take(MatChunk).ToList();
        }

        // ===================== value helpers =====================

        private static string ResolveSpras(string lang)
        {
            var up = (lang ?? "SL").Trim().ToUpperInvariant();
            if (up == "EN" || up == "E") return "E";
            return "5"; // Slovenian
        }

        private static decimal ParseScaled(string raw, int impliedDecimals)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0m;
            var v = raw.Trim();

            bool negative = false;
            if (v.EndsWith("-")) { negative = true; v = v.Substring(0, v.Length - 1).Trim(); }
            else if (v.EndsWith("+")) { v = v.Substring(0, v.Length - 1).Trim(); }

            v = v.Replace(",", ".");
            decimal d;
            if (v.IndexOf('.') >= 0)
            {
                decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out d);
            }
            else
            {
                decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out d);
                if (impliedDecimals > 0) d = d / (decimal)Math.Pow(10, impliedDecimals);
            }
            return negative ? -d : d;
        }

        private static string StripAlpha(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var v = value.Trim();
            if (IsAllDigits(v))
            {
                var trimmed = v.TrimStart('0');
                return trimmed.Length == 0 ? "0" : trimmed;
            }
            return v;
        }

        private static string FormatDate(string yyyymmdd)
        {
            if (string.IsNullOrWhiteSpace(yyyymmdd) || yyyymmdd == "00000000") return null;
            return DateTime.TryParseExact(yyyymmdd.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                ? dt.ToString("yyyy-MM-dd")
                : yyyymmdd.Trim();
        }

        private static string NormalizeDate(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var s = input.Trim();
            string[] formats = { "yyyyMMdd", "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy" };
            if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return dt.ToString("yyyyMMdd");
            return null;
        }

        private static bool IsAllDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        private static string Esc(string value) => (value ?? string.Empty).Replace("'", "''");

        private static string Get(string[] row, int index)
        {
            if (row == null || index < 0 || index >= row.Length) return string.Empty;
            return row[index].Trim();
        }

        private static MaterialDocResult EmptyResult(bool summary)
        {
            return summary
                ? new MaterialDocResult { Summary = true, Count = 0, SummaryRows = new List<MaterialDocSummaryRowDto>() }
                : new MaterialDocResult { Summary = false, Count = 0, Rows = new List<MaterialDocRowDto>() };
        }
    }
}
