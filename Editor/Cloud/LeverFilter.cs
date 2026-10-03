using System;
using System.Collections.Generic;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Which lever rows the Nova Capture window lists under its "Find" box: every row whose command holds the text
    /// (case-insensitive, any part), plus every TICKED row, so a tick is never hidden from the person who may untick it.
    /// An empty or blank filter lists every row.
    /// </summary>
    internal static class LeverFilter
    {
        public static HashSet<string> Visible(IEnumerable<(string Command, bool Approved)> rows, string? filter)
        {
            var needle = (filter ?? "").Trim();
            var shown = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (command, approved) in rows)
            {
                if (needle.Length == 0 || approved ||
                    command.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    shown.Add(command);
            }
            return shown;
        }
    }
}
