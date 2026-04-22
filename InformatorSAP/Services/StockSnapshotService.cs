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
                        term.ExactText,
                        includePlanned);

                    preparedSnapshots.Add(Tuple.Create(term, summary));
                }
                catch (Exception ex)
                {
                    var context =
                        $"term_id={term.TermId}, query={term.ContainsText}, exactText={term.ExactText}, werks={term.Werks}, lgort={term.Lgort}, includePlanned={includePlanned}";
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
        exact_text,
        search_mode,
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
    latest.snapshot_id,
    latest.term_id,
    latest.werks,
    latest.lgort,
    latest.[query],
    latest.exact_text,
    latest.search_mode,
    latest.total,
    latest.unit_id,
    latest.unit,
    latest.planned_total,
    latest.planned_unit,
    latest.delivered_total,
    latest.delivered_unit,
    latest.planned_minus_delivered_total,
    latest.planned_minus_delivered_unit,
    latest.retrieved_at_utc,
    goal.id AS goal_id,
    goal.goal_value,
    goal.valid_from,
    goal.valid_to,
    goal.created_at,
    goal.updated_at
FROM latest
OUTER APPLY (
    SELECT TOP 1
        g.id,
        g.goal_value,
        g.valid_from,
        g.valid_to,
        g.created_at,
        g.updated_at
    FROM informator.dbo.stock_goal g
    WHERE g.term_id = latest.term_id
      AND CAST(latest.retrieved_at_utc AS date) >= g.valid_from
      AND CAST(latest.retrieved_at_utc AS date) <= g.valid_to
    ORDER BY g.updated_at DESC, g.created_at DESC, g.id DESC
) goal
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
                        result.Add(MapSnapshotRow(rdr));
                    }
                }
            }

            return result;
        }


        public List<StockSnapshotRowDto> GetSnapshots(
            string werks,
            int unitId,
            bool latestPerTerm,
            DateTime? fromUtc = null,
            DateTime? toUtc = null,
            string lgort = null)
        {
            var result = new List<StockSnapshotRowDto>();

            using (var conn = new SqlConnection(_connString))
            using (var cmd = conn.CreateCommand())
            {
                if (latestPerTerm)
                {
                    cmd.CommandText = @"
;WITH latest AS (
    SELECT
        snapshot_id,
        term_id,
        werks,
        lgort,
        [query],
        exact_text,
        search_mode,
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
    latest.snapshot_id,
    latest.term_id,
    latest.werks,
    latest.lgort,
    latest.[query],
    latest.exact_text,
    latest.search_mode,
    latest.total,
    latest.unit_id,
    latest.unit,
    latest.planned_total,
    latest.planned_unit,
    latest.delivered_total,
    latest.delivered_unit,
    latest.planned_minus_delivered_total,
    latest.planned_minus_delivered_unit,
    latest.retrieved_at_utc,
    goal.id AS goal_id,
    goal.goal_value,
    goal.valid_from,
    goal.valid_to,
    goal.created_at,
    goal.updated_at
FROM latest
OUTER APPLY (
    SELECT TOP 1
        g.id,
        g.goal_value,
        g.valid_from,
        g.valid_to,
        g.created_at,
        g.updated_at
    FROM informator.dbo.stock_goal g
    WHERE g.term_id = latest.term_id
      AND CAST(latest.retrieved_at_utc AS date) >= g.valid_from
      AND CAST(latest.retrieved_at_utc AS date) <= g.valid_to
    ORDER BY g.updated_at DESC, g.created_at DESC, g.id DESC
) goal
WHERE rn = 1
  AND werks = @werks
  AND unit_id = @unit_id
  AND (@lgort IS NULL OR lgort = @lgort)
ORDER BY term_id;";
                }
                else
                {
                    cmd.CommandText = @"
;WITH day_latest AS (
    SELECT
        snapshot_id,
        term_id,
        werks,
        lgort,
        [query],
        exact_text,
        search_mode,
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
        ROW_NUMBER() OVER (
            PARTITION BY term_id, CAST(retrieved_at_utc AS date)
            ORDER BY retrieved_at_utc DESC, snapshot_id DESC
        ) AS rn
    FROM informator.dbo.stock_summary_snapshot
    WHERE werks = @werks
      AND unit_id = @unit_id
      AND (@lgort IS NULL OR lgort = @lgort)
      AND (@from_utc IS NULL OR retrieved_at_utc >= @from_utc)
      AND (@to_utc IS NULL OR retrieved_at_utc <= @to_utc)
)
SELECT
    day_latest.snapshot_id,
    day_latest.term_id,
    day_latest.werks,
    day_latest.lgort,
    day_latest.[query],
    day_latest.exact_text,
    day_latest.search_mode,
    day_latest.total,
    day_latest.unit_id,
    day_latest.unit,
    day_latest.planned_total,
    day_latest.planned_unit,
    day_latest.delivered_total,
    day_latest.delivered_unit,
    day_latest.planned_minus_delivered_total,
    day_latest.planned_minus_delivered_unit,
    day_latest.retrieved_at_utc,
    goal.id AS goal_id,
    goal.goal_value,
    goal.valid_from,
    goal.valid_to,
    goal.created_at,
    goal.updated_at
FROM day_latest
OUTER APPLY (
    SELECT TOP 1
        g.id,
        g.goal_value,
        g.valid_from,
        g.valid_to,
        g.created_at,
        g.updated_at
    FROM informator.dbo.stock_goal g
    WHERE g.term_id = day_latest.term_id
      AND CAST(day_latest.retrieved_at_utc AS date) >= g.valid_from
      AND CAST(day_latest.retrieved_at_utc AS date) <= g.valid_to
    ORDER BY g.updated_at DESC, g.created_at DESC, g.id DESC
) goal
WHERE rn = 1
ORDER BY retrieved_at_utc DESC, snapshot_id DESC;";
                }

                cmd.Parameters.Add("@werks", SqlDbType.NVarChar, 4).Value = NormalizeNullable(werks);
                cmd.Parameters.Add("@unit_id", SqlDbType.Int).Value = unitId;
                cmd.Parameters.Add("@lgort", SqlDbType.NVarChar, 4).Value = (object)NormalizeNullable(lgort) ?? DBNull.Value;
                cmd.Parameters.Add("@from_utc", SqlDbType.DateTime2).Value = (object)fromUtc ?? DBNull.Value;
                cmd.Parameters.Add("@to_utc", SqlDbType.DateTime2).Value = (object)toUtc ?? DBNull.Value;

                conn.Open();
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        result.Add(MapSnapshotRow(rdr));
                    }
                }
            }

            return result;
        }

        public List<StockSnapshotRowDto> GetSnapshotsForDate(DateTime localDate, int? unitId = null, string werks = null, string lgort = null)
        {
            var result = new List<StockSnapshotRowDto>();
            var dayRange = GetLjubljanaUtcRangeForLocalDate(localDate);

            using (var conn = new SqlConnection(_connString))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
;WITH day_rows AS (
    SELECT
        snapshot_id,
        term_id,
        werks,
        lgort,
        [query],
        exact_text,
        search_mode,
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
    WHERE retrieved_at_utc >= @from_utc
      AND retrieved_at_utc < @to_utc
)
SELECT
    day_rows.snapshot_id,
    day_rows.term_id,
    day_rows.werks,
    day_rows.lgort,
    day_rows.[query],
    day_rows.exact_text,
    day_rows.search_mode,
    day_rows.total,
    day_rows.unit_id,
    day_rows.unit,
    day_rows.planned_total,
    day_rows.planned_unit,
    day_rows.delivered_total,
    day_rows.delivered_unit,
    day_rows.planned_minus_delivered_total,
    day_rows.planned_minus_delivered_unit,
    day_rows.retrieved_at_utc,
    goal.id AS goal_id,
    goal.goal_value,
    goal.valid_from,
    goal.valid_to,
    goal.created_at,
    goal.updated_at
FROM day_rows
OUTER APPLY (
    SELECT TOP 1
        g.id,
        g.goal_value,
        g.valid_from,
        g.valid_to,
        g.created_at,
        g.updated_at
    FROM informator.dbo.stock_goal g
    WHERE g.term_id = day_rows.term_id
      AND CAST(day_rows.retrieved_at_utc AS date) >= g.valid_from
      AND CAST(day_rows.retrieved_at_utc AS date) <= g.valid_to
    ORDER BY g.updated_at DESC, g.created_at DESC, g.id DESC
) goal
WHERE rn = 1
  AND (@werks IS NULL OR werks = @werks)
  AND (@lgort IS NULL OR lgort = @lgort)
  AND (@unit_id IS NULL OR unit_id = @unit_id)
ORDER BY unit_id, [query];";

                cmd.Parameters.Add("@from_utc", SqlDbType.DateTime2).Value = dayRange.Item1;
                cmd.Parameters.Add("@to_utc", SqlDbType.DateTime2).Value = dayRange.Item2;
                cmd.Parameters.Add("@werks", SqlDbType.NVarChar, 4).Value = (object)NormalizeNullable(werks) ?? DBNull.Value;
                cmd.Parameters.Add("@lgort", SqlDbType.NVarChar, 4).Value = (object)NormalizeNullable(lgort) ?? DBNull.Value;
                cmd.Parameters.Add("@unit_id", SqlDbType.Int).Value = (object)unitId ?? DBNull.Value;

                conn.Open();
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        result.Add(MapSnapshotRow(rdr));
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
SELECT term_id, contains_text, exact_text, werks, lgort, unit_id, is_active
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
                            ExactText = rdr.IsDBNull(2) ? null : rdr.GetString(2).Trim(),
                            Werks = rdr.IsDBNull(3) ? null : rdr.GetString(3).Trim(),
                            Lgort = rdr.IsDBNull(4) ? null : rdr.GetString(4).Trim(),
                            UnitId = rdr.GetInt32(5),
                            IsActive = rdr.GetBoolean(6)
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
    exact_text,
    search_mode,
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
    @exact_text,
    @search_mode,
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
                cmd.Parameters.Add("@exact_text", SqlDbType.NVarChar, 200).Value = (object)term.ExactText ?? DBNull.Value;
                cmd.Parameters.Add("@search_mode", SqlDbType.NVarChar, 20).Value =
                    string.IsNullOrWhiteSpace(term.ExactText) ? "contains" : "exact";
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

        private static Tuple<DateTime, DateTime> GetLjubljanaUtcRangeForLocalDate(DateTime localDate)
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
            var localDayStart = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
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

        private static StockSnapshotRowDto MapSnapshotRow(SqlDataReader rdr)
        {
            return new StockSnapshotRowDto
            {
                SnapshotId = rdr.GetInt64(0),
                TermId = rdr.GetInt32(1),
                Werks = rdr.GetString(2),
                Lgort = rdr.GetString(3),
                Query = rdr.GetString(4),
                ExactText = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                SearchMode = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                Total = rdr.GetDecimal(7),
                UnitId = rdr.GetInt32(8),
                Unit = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                PlannedTotal = rdr.GetDecimal(10),
                PlannedUnit = rdr.IsDBNull(11) ? null : rdr.GetString(11),
                DeliveredTotal = rdr.GetDecimal(12),
                DeliveredUnit = rdr.IsDBNull(13) ? null : rdr.GetString(13),
                PlannedMinusDeliveredTotal = rdr.GetDecimal(14),
                PlannedMinusDeliveredUnit = rdr.IsDBNull(15) ? null : rdr.GetString(15),
                RetrievedAtUtc = rdr.GetDateTime(16),
                GoalId = rdr.IsDBNull(17) ? (long?)null : rdr.GetInt64(17),
                GoalValue = rdr.IsDBNull(18) ? (decimal?)null : rdr.GetDecimal(18),
                GoalValidFrom = rdr.IsDBNull(19) ? (DateTime?)null : rdr.GetDateTime(19),
                GoalValidTo = rdr.IsDBNull(20) ? (DateTime?)null : rdr.GetDateTime(20),
                GoalCreatedAt = rdr.IsDBNull(21) ? (DateTime?)null : rdr.GetDateTime(21),
                GoalUpdatedAt = rdr.IsDBNull(22) ? (DateTime?)null : rdr.GetDateTime(22)
            };
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
