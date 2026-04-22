using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using InformatorSAP.Models;

namespace InformatorSAP.Services
{
    public class StockGoalService
    {
        private readonly string _connString;

        public StockGoalService()
        {
            var named = ConfigurationManager.ConnectionStrings["InformatorDb"];
            if (named == null || string.IsNullOrWhiteSpace(named.ConnectionString))
            {
                throw new InvalidOperationException("Connection string 'InformatorDb' is missing.");
            }

            _connString = named.ConnectionString;
        }

        public StockGoalDto CreateGoal(int termId, decimal goalValue, DateTime validFrom, DateTime validTo)
        {
            using (var conn = new SqlConnection(_connString))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO informator.dbo.stock_goal
(
    term_id,
    goal_value,
    valid_from,
    valid_to
)
OUTPUT
    INSERTED.id,
    INSERTED.term_id,
    INSERTED.goal_value,
    INSERTED.valid_from,
    INSERTED.valid_to,
    INSERTED.created_at,
    INSERTED.updated_at
VALUES
(
    @term_id,
    @goal_value,
    @valid_from,
    @valid_to
);";

                cmd.Parameters.Add("@term_id", SqlDbType.Int).Value = termId;

                cmd.Parameters.Add("@goal_value", SqlDbType.Decimal).Value = goalValue;
                cmd.Parameters["@goal_value"].Precision = 18;
                cmd.Parameters["@goal_value"].Scale = 3;

                cmd.Parameters.Add("@valid_from", SqlDbType.Date).Value = validFrom.Date;
                cmd.Parameters.Add("@valid_to", SqlDbType.Date).Value = validTo.Date;

                conn.Open();
                using (var rdr = cmd.ExecuteReader())
                {
                    if (!rdr.Read())
                    {
                        throw new InvalidOperationException("Failed to create stock goal.");
                    }

                    return MapGoal(rdr);
                }
            }
        }

        public List<StockGoalDto> GetGoals(int termId)
        {
            var result = new List<StockGoalDto>();

            using (var conn = new SqlConnection(_connString))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT
    id,
    term_id,
    goal_value,
    valid_from,
    valid_to,
    created_at,
    updated_at
FROM informator.dbo.stock_goal
WHERE term_id = @term_id
ORDER BY updated_at DESC, created_at DESC, id DESC;";

                cmd.Parameters.Add("@term_id", SqlDbType.Int).Value = termId;

                conn.Open();
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        result.Add(MapGoal(rdr));
                    }
                }
            }

            return result;
        }

        private static StockGoalDto MapGoal(SqlDataReader rdr)
        {
            return new StockGoalDto
            {
                Id = rdr.GetInt64(0),
                TermId = rdr.GetInt32(1),
                GoalValue = rdr.GetDecimal(2),
                ValidFrom = rdr.GetDateTime(3),
                ValidTo = rdr.GetDateTime(4),
                CreatedAt = rdr.GetDateTime(5),
                UpdatedAt = rdr.GetDateTime(6)
            };
        }
    }
}
