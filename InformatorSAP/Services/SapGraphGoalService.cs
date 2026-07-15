using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using InformatorSAP.Models;

namespace InformatorSAP.Services
{
    /// <summary>
    /// Persists a single target value per SAP graph in informator.dbo.sap_graph_goal.
    /// Mirrors <see cref="StockGoalService"/> (same InformatorDb connection), but the store
    /// is a plain key/value upsert so the frontend goal handling stays as simple as possible.
    /// The table is created on demand, so there is no separate migration step to run.
    /// </summary>
    public class SapGraphGoalService
    {
        private readonly string _connString;

        public SapGraphGoalService()
        {
            var named = ConfigurationManager.ConnectionStrings["InformatorDb"];
            if (named == null || string.IsNullOrWhiteSpace(named.ConnectionString))
            {
                throw new InvalidOperationException("Connection string 'InformatorDb' is missing.");
            }

            _connString = named.ConnectionString;
        }

        private static void EnsureTable(SqlConnection conn)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
IF OBJECT_ID('informator.dbo.sap_graph_goal', 'U') IS NULL
BEGIN
    CREATE TABLE informator.dbo.sap_graph_goal
    (
        graph_key   VARCHAR(64)   NOT NULL CONSTRAINT PK_sap_graph_goal PRIMARY KEY,
        goal_value  DECIMAL(18,3) NOT NULL,
        updated_by  VARCHAR(64)   NULL,
        updated_at  DATETIME2     NOT NULL CONSTRAINT DF_sap_graph_goal_updated_at DEFAULT SYSUTCDATETIME()
    );
END";
                cmd.ExecuteNonQuery();
            }
        }

        public List<SapGraphGoalDto> GetGoals()
        {
            var result = new List<SapGraphGoalDto>();

            using (var conn = new SqlConnection(_connString))
            {
                conn.Open();
                EnsureTable(conn);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT graph_key, goal_value, updated_by, updated_at
FROM informator.dbo.sap_graph_goal
ORDER BY graph_key;";
                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read()) result.Add(Map(rdr));
                    }
                }
            }

            return result;
        }

        public SapGraphGoalDto GetGoal(string graphKey)
        {
            using (var conn = new SqlConnection(_connString))
            {
                conn.Open();
                EnsureTable(conn);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT graph_key, goal_value, updated_by, updated_at
FROM informator.dbo.sap_graph_goal
WHERE graph_key = @graph_key;";
                    cmd.Parameters.Add("@graph_key", SqlDbType.VarChar, 64).Value = graphKey;

                    using (var rdr = cmd.ExecuteReader())
                    {
                        return rdr.Read() ? Map(rdr) : null;
                    }
                }
            }
        }

        public SapGraphGoalDto UpsertGoal(string graphKey, decimal goalValue, string updatedBy)
        {
            using (var conn = new SqlConnection(_connString))
            {
                conn.Open();
                EnsureTable(conn);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE informator.dbo.sap_graph_goal
    SET goal_value = @goal_value, updated_by = @updated_by, updated_at = SYSUTCDATETIME()
    WHERE graph_key = @graph_key;
IF @@ROWCOUNT = 0
    INSERT INTO informator.dbo.sap_graph_goal (graph_key, goal_value, updated_by)
    VALUES (@graph_key, @goal_value, @updated_by);

SELECT graph_key, goal_value, updated_by, updated_at
FROM informator.dbo.sap_graph_goal
WHERE graph_key = @graph_key;";

                    cmd.Parameters.Add("@graph_key", SqlDbType.VarChar, 64).Value = graphKey;
                    cmd.Parameters.Add("@goal_value", SqlDbType.Decimal).Value = goalValue;
                    cmd.Parameters["@goal_value"].Precision = 18;
                    cmd.Parameters["@goal_value"].Scale = 3;
                    cmd.Parameters.Add("@updated_by", SqlDbType.VarChar, 64).Value =
                        (object)updatedBy ?? DBNull.Value;

                    using (var rdr = cmd.ExecuteReader())
                    {
                        if (!rdr.Read()) throw new InvalidOperationException("Failed to save graph goal.");
                        return Map(rdr);
                    }
                }
            }
        }

        private static SapGraphGoalDto Map(SqlDataReader rdr)
        {
            return new SapGraphGoalDto
            {
                GraphKey = rdr.GetString(0),
                GoalValue = rdr.GetDecimal(1),
                UpdatedBy = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                UpdatedAt = rdr.GetDateTime(3)
            };
        }
    }
}
