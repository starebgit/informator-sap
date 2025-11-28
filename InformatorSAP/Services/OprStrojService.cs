using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using InformatorSAP.Models;

namespace InformatorSAP.Services
{
    public class OprStrojService
    {
        private readonly string _connString;

        public OprStrojService()
        {
            _connString = ConfigurationManager.ConnectionStrings["DelNalogi"]
                          .ConnectionString;
        }

        public List<OperationMachineDto> GetMachinesForOperation(string operationId)
        {
            var result = new List<OperationMachineDto>();

            if (string.IsNullOrWhiteSpace(operationId))
                return result;

            using (var conn = new SqlConnection(_connString))
            using (var cmd = new SqlCommand(
                "SELECT idstroj, idoprema FROM OprStroj WHERE idOperacija = @opr",
                conn))
            {
                // IMPORTANT: trim to avoid hidden spaces and always use string.ToString()
                cmd.Parameters.Add("@opr", SqlDbType.VarChar, 50).Value = operationId.Trim();

                conn.Open();
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        result.Add(new OperationMachineDto
                        {
                            // if columns are numeric, `as string` returns null – use ToString()
                            MachineKey = rdr["idstroj"].ToString().Trim(),
                            MachineAltKey = rdr["idoprema"].ToString().Trim()
                        });
                    }
                }
            }

            return result;
        }
    }
}
