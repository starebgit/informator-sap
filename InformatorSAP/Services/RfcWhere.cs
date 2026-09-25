using System.Collections.Generic;

namespace InformatorSAP.Services
{
    /// Splits a WHERE clause into RFC_READ_TABLE OPTIONS lines (max 72 chars each).
    /// SAP rejects a keyword or field name cut across two lines (e.g. "SPR" | "AS" -> OPTION_NOT_VALID),
    /// so lines are only broken at a space outside quotes. The space starts the next line, because SAP
    /// drops trailing blanks of a line but keeps leading ones.
    internal static class RfcWhere
    {
        private const int MaxLine = 72;

        public static List<string> Split(string where)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(where)) return lines;

            var text = where.StartsWith(" ") ? where : " " + where;
            int start = 0;
            while (text.Length - start > MaxLine)
            {
                // last space outside quotes within the next 72 chars (quote state is tracked from the line start)
                int cut = -1;
                bool inQuote = false;
                for (int i = start; i < start + MaxLine; i++)
                {
                    var c = text[i];
                    if (c == '\'') inQuote = !inQuote;
                    else if (c == ' ' && !inQuote && i > start) cut = i;
                }
                if (cut < 0) cut = start + MaxLine; // single token longer than a line: hard cut (old behaviour)

                lines.Add(text.Substring(start, cut - start));
                start = cut;
            }
            lines.Add(text.Substring(start));
            return lines;
        }
    }
}
