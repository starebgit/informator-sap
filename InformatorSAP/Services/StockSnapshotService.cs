using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Hosting;
using InformatorSAP.Models;

namespace InformatorSAP.Services
{
    public class StockSnapshotService
    {
        private readonly string _connString;

        public StockSnapshotService()
        {
            var named = ConfigurationManager.ConnectionStrings["InformatorDb"];
            if (named == null || string.IsNullOrWhiteSpace(named.ConnectionString))
            {
                throw new InvalidOperationException("Connection string 'InformatorDb' is missing.");
            }

            _connString = named.ConnectionString;
        }

        public int RefreshNightlySnapshots(bool includePlanned = true)
        {
            var terms = GetActiveTerms();
            var sapStockService = new SapStockService();
            var nowUtc = DateTime.UtcNow;
            var dayRange = GetLjubljanaUtcDayRange(nowUtc);
            var preparedSnapshots = new List<Tuple<StockTermConfig, Classes.StockSummaryDto>>(terms.Count);

            foreach (var term in terms)
            {
                try
                {
                    var summary = sapStockService.GetUnrestrictedStockSummary(
                        term.Werks,
                        term.Lgort,
                        term.ContainsText,
                        includePlanned);

                    preparedSnapshots.Add(Tuple.Create(term, summary));
                }
                catch (Exception ex)
                {
                    var context =
                        $"term_id={term.TermId}, query={term.ContainsText}, werks={term.Werks}, lgort={term.Lgort}, includePlanned={includePlanned}";
                    if (HasMixedUnitException(ex))
                    {
                        var mixedUnitMessage =
                            $"[StockSnapshotService] Skipping term due to mixed units. {context}. RootError={GetInnermostMessage(ex)}";
                        Trace.TraceWarning(mixedUnitMessage);
                        AppendRefreshLog(mixedUnitMessage);
                        continue;
                    }

                    var fatalMessage =
                        $"[StockSnapshotService] Failed to refresh term. {context}. Exception={ex}";
                    Trace.TraceError(fatalMessage);
                    AppendRefreshLog(fatalMessage);
                    throw;
                }
            }

            using (var conn = new SqlConnection(_connString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    DeleteRowsForUtcRange(conn, tx, dayRange.Item1, dayRange.Item2);

                    foreach (var snapshot in preparedSnapshots)
                    {
                        InsertSnapshot(conn, tx, snapshot.Item1, snapshot.Item2, nowUtc);
                    }

                    tx.Commit();
                }
            }

            return preparedSnapshots.Count;
        }

        public List<StockSnapshotRowDto> GetLatestSnapshots(string werks = null, string lgort = null, int? unitId = null)
        {
            var result = new List<StockSnapshotRowDto>();

            using (var conn = new SqlConnection(_connString))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
;WITH latest AS (
    SELECT
        snapshot_id,
        term_id,
        werks,
        lgort,
        [query],
        total,
        unit_id,
        unit,
        planned_total,
        planned_unit,
        delivered_total,
        delivered_unit,
        planned_minus_delivered_total,
        planned_minus_delivered_unit,
        retrieved_at_utc,
        ROW_NUMBER() OVER (PARTITION BY term_id ORDER BY retrieved_at_utc DESC, snapshot_id DESC) AS rn
    FROM informator.dbo.stock_summary_snapshot
)
SELECT
    snapshot_id,
    term_id,
    werks,
    lgort,
    [query],
    total,
    unit_id,
    unit,
    planned_total,
    planned_unit,
    delivered_total,
    delivered_unit,
    planned_minus_delivered_total,
    planned_minus_delivered_unit,
    retrieved_at_utc
FROM latest
WHERE rn = 1
  AND (@werks IS NULL OR werks = @werks)
  AND (@lgort IS NULL OR lgort = @lgort)
  AND (@unit_id IS NULL OR unit_id = @unit_id)
ORDER BY unit_id, [query];";

                cmd.Parameters.Add("@werks", SqlDbType.NVarChar, 4).Value = (object)NormalizeNullable(werks) ?? DBNull.Value;
                cmd.Parameters.Add("@lgort", SqlDbType.NVarChar, 4).Value = (object)NormalizeNullable(lgort) ?? DBNull.Value;
                cmd.Parameters.Add("@unit_id", SqlDbType.Int).Value = (object)unitId ?? DBNull.Value;

                conn.Open();
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        result.Add(new StockSnapshotRowDto
                        {
                            SnapshotId = rdr.GetInt64(0),
                            TermId = rdr.GetInt32(1),
                            Werks = rdr.GetString(2),
                            Lgort = rdr.GetString(3),
                            Query = rdr.GetString(4),
                            Total = rdr.GetDecimal(5),
                            UnitId = rdr.GetInt32(6),
                            Unit = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                            PlannedTotal = rdr.GetDecimal(8),
                            PlannedUnit = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                            DeliveredTotal = rdr.GetDecimal(10),
                            DeliveredUnit = rdr.IsDBNull(11) ? null : rdr.GetString(11),
                            PlannedMinusDeliveredTotal = rdr.GetDecimal(12),
                            PlannedMinusDeliveredUnit = rdr.IsDBNull(13) ? null : rdr.GetString(13),
                            RetrievedAtUtc = rdr.GetDateTime(14)
                        });
                    }
                }
            }

            return result;
        }

        private List<StockTermConfig> GetActiveTerms()
        {
            var result = new List<StockTermConfig>();

            using (var conn = new SqlConnection(_connString))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT term_id, contains_text, werks, lgort, unit_id, is_active
FROM informator.dbo.stock_term
WHERE is_active = 1
ORDER BY term_id;";

                conn.Open();
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        result.Add(new StockTermConfig
                        {
                            TermId = rdr.GetInt32(0),
                            ContainsText = rdr.GetString(1).Trim(),
                            Werks = rdr.IsDBNull(2) ? null : rdr.GetString(2).Trim(),
                            Lgort = rdr.IsDBNull(3) ? null : rdr.GetString(3).Trim(),
                            UnitId = rdr.GetInt32(4),
                            IsActive = rdr.GetBoolean(5)
                        });
                    }
                }
            }

            return result;
        }

        private void DeleteRowsForUtcRange(SqlConnection conn, SqlTransaction tx, DateTime fromUtcInclusive, DateTime toUtcExclusive)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
DELETE FROM informator.dbo.stock_summary_snapshot
WHERE retrieved_at_utc >= @from_utc
  AND retrieved_at_utc < @to_utc;";

                cmd.Parameters.Add("@from_utc", SqlDbType.DateTime2).Value = fromUtcInclusive;
                cmd.Parameters.Add("@to_utc", SqlDbType.DateTime2).Value = toUtcExclusive;
                cmd.ExecuteNonQuery();
            }
        }

        private void InsertSnapshot(SqlConnection conn, SqlTransaction tx, StockTermConfig term, Classes.StockSummaryDto summary, DateTime retrievedAtUtc)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO informator.dbo.stock_summary_snapshot
(
    term_id,
    werks,
    lgort,
    [query],
    total,
    unit_id,
    unit,
    planned_total,
    planned_unit,
    delivered_total,
    delivered_unit,
    planned_minus_delivered_total,
    planned_minus_delivered_unit,
    retrieved_at_utc
)
VALUES
(
    @term_id,
    @werks,
    @lgort,
    @query,
    @total,
    @unit_id,
    @unit,
    @planned_total,
    @planned_unit,
    @delivered_total,
    @delivered_unit,
    @planned_minus_delivered_total,
    @planned_minus_delivered_unit,
    @retrieved_at_utc
);";

                cmd.Parameters.Add("@term_id", SqlDbType.Int).Value = term.TermId;
                cmd.Parameters.Add("@werks", SqlDbType.NVarChar, 4).Value = term.Werks;
                cmd.Parameters.Add("@lgort", SqlDbType.NVarChar, 4).Value = term.Lgort;
                cmd.Parameters.Add("@query", SqlDbType.NVarChar, 200).Value = term.ContainsText;
                cmd.Parameters.Add("@total", SqlDbType.Decimal).Value = summary.Total;
                cmd.Parameters["@total"].Precision = 18;
                cmd.Parameters["@total"].Scale = 3;
                cmd.Parameters.Add("@unit_id", SqlDbType.Int).Value = term.UnitId;
                cmd.Parameters.Add("@unit", SqlDbType.NVarChar, 10).Value = (object)summary.Unit ?? DBNull.Value;

                cmd.Parameters.Add("@planned_total", SqlDbType.Decimal).Value = summary.PlannedTotal;
                cmd.Parameters["@planned_total"].Precision = 18;
                cmd.Parameters["@planned_total"].Scale = 3;

                cmd.Parameters.Add("@planned_unit", SqlDbType.NVarChar, 10).Value = (object)summary.PlannedUnit ?? DBNull.Value;

                cmd.Parameters.Add("@delivered_total", SqlDbType.Decimal).Value = summary.DeliveredTotal;
                cmd.Parameters["@delivered_total"].Precision = 18;
                cmd.Parameters["@delivered_total"].Scale = 3;

                cmd.Parameters.Add("@delivered_unit", SqlDbType.NVarChar, 10).Value = (object)summary.DeliveredUnit ?? DBNull.Value;

                cmd.Parameters.Add("@planned_minus_delivered_total", SqlDbType.Decimal).Value = summary.PlannedMinusDeliveredTotal;
                cmd.Parameters["@planned_minus_delivered_total"].Precision = 18;
                cmd.Parameters["@planned_minus_delivered_total"].Scale = 3;

                cmd.Parameters.Add("@planned_minus_delivered_unit", SqlDbType.NVarChar, 10).Value = (object)summary.PlannedMinusDeliveredUnit ?? DBNull.Value;
                cmd.Parameters.Add("@retrieved_at_utc", SqlDbType.DateTime2).Value = retrievedAtUtc;

                cmd.ExecuteNonQuery();
            }
        }

        private static Tuple<DateTime, DateTime> GetLjubljanaUtcDayRange(DateTime utcNow)
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
            var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
            var localDayStart = local.Date;
            var localDayEnd = localDayStart.AddDays(1);
            var utcStart = TimeZoneInfo.ConvertTimeToUtc(localDayStart, tz);
            var utcEnd = TimeZoneInfo.ConvertTimeToUtc(localDayEnd, tz);
            return Tuple.Create(utcStart, utcEnd);
        }

        private static string NormalizeNullable(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim();
        }

        private static bool HasMixedUnitException(Exception ex)
        {
            if (ex == null)
            {
                return false;
            }

            if (ex is InvalidOperationException &&
                ex.Message != null &&
                ex.Message.IndexOf("mixed unit", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var aggregate = ex as AggregateException;
            if (aggregate != null)
            {
                return aggregate.Flatten().InnerExceptions.Any(HasMixedUnitException);
            }

            return HasMixedUnitException(ex.InnerException);
        }

        private static string GetInnermostMessage(Exception ex)
        {
            var cursor = ex;
            while (cursor != null && cursor.InnerException != null)
            {
                cursor = cursor.InnerException;
            }

            return cursor != null ? cursor.Message : string.Empty;
        }

        private static void AppendRefreshLog(string line)
        {
            try
            {
                var path = ResolveRefreshLogPath();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(path, $"[{DateTime.UtcNow:O}] {line}{Environment.NewLine}");
            }
            catch
            {
                // logging must never break refresh flow
            }
        }

        private static string ResolveRefreshLogPath()
        {
            var hostedPath = HostingEnvironment.MapPath("~/App_Data/stock-snapshot-refresh.log");
            if (!string.IsNullOrWhiteSpace(hostedPath))
            {
                return hostedPath;
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "stock-snapshot-refresh.log");
        }
    }
}
