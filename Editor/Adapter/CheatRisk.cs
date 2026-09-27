using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P2 (§3.1, §3.3 step 4, §4) — WHICH TICKED CHEATS ARE RISKY, so they stay refused until the
    /// per-project tick "this editor talks to a non-production server" (<see cref="Levers.NonProductionTicked"/>).
    ///
    /// Two sources, and a delivered one can only RAISE the answer:
    /// <list type="number">
    /// <item><b>The command's own words</b> — <see cref="PressGuard"/>'s roots (money, deleting, resetting, the account),
    /// read the way the press guard reads a button, plus the cheat kinds the plan names risky: <c>give</c> /
    /// <c>grant</c> / <c>gift</c> (a give writes the player's data on the game's server) and <c>server</c>. Words are
    /// read across the WHOLE command — verb, type path and arguments — so
    /// <c>call DevConsoleActions.Invoke "Give me 10k topaz"</c> is risky by its argument.</item>
    /// <item><b>The tick list the website delivered</b> (<see cref="TickList"/>): a candidate of exactly this command
    /// whose kind is give / reset / purchase, or whose risk names server / account / purchase / destructive. A file
    /// the cloud wrote can make a cheat risky; it can never make one safe.</item>
    /// </list>
    ///
    /// THE WORDS ARE THE SECOND FENCE, said plainly: <c>set Player.coins 99999</c> is a give that says nothing risky,
    /// and a localised name is not read by English roots. What makes a command run at all is still the person's exact
    /// tick; this only holds some ticked commands back until the project says its server is not production.
    /// Fail-closed direction: a false positive (<c>call AccountHud.Show</c>) waits for the tick; it never runs early.
    /// </summary>
    public static class CheatRisk
    {
        /// <summary>The cheat roots added to <see cref="PressGuard"/>'s: the plan's risky kinds that are not presses.</summary>
        public static readonly IReadOnlyList<string> CheatRoots = new[] { "give", "grant", "gift", "server" };

        /// <summary>The tick-list kinds that are risky (v3 §3.1: server / reset / give / destructive / purchase).</summary>
        public static readonly IReadOnlyList<string> RiskyKinds = new[] { "give", "reset", "purchase" };

        /// <summary>The tick-list risk labels (every one is risky — a label is only written when there is a risk).</summary>
        public static readonly IReadOnlyList<string> RiskLabels = new[] { "destructive", "purchase", "server", "account" };

        /// <summary>The risky root in the command's own words, or null. Never throws.</summary>
        public static string? RootOf(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            var press = PressGuard.RiskyRoot(command!);
            if (press != null) return press;
            foreach (var w in PressGuard.Words(command!))
                foreach (var root in CheatRoots)
                    if (w.StartsWith(root, StringComparison.Ordinal))
                        return root;
            return null;
        }

        /// <summary>Why this command is risky — its own root, else what the delivered tick list said of it — or null.</summary>
        public static string? Why(string? command, IReadOnlyList<TickList.Candidate>? delivered)
        {
            var root = RootOf(command);
            if (root != null) return $"the word root '{root}'";
            if (delivered == null || command == null) return null;
            foreach (var c in delivered)
            {
                if (!string.Equals(c.Command, command, StringComparison.Ordinal)) continue;
                if (c.Kind != null && RiskyKinds.Contains(c.Kind)) return $"the website's kind '{c.Kind}'";
                var label = c.Risk.FirstOrDefault(r => RiskLabels.Contains(r));
                if (label != null) return $"the website's risk '{label}'";
            }
            return null;
        }

        /// <summary>The line a risky, ticked command leaves when the project has not said its server is not production.</summary>
        public static string NotYetLog(string command, string why) =>
            $"risky cheat refused until \"{NonProductionLabel}\" is ticked: '{command}' ({why})";

        /// <summary>The words of the tick in the Nova Capture window — named once, so the log and the box say the same.</summary>
        public const string NonProductionLabel = "this editor talks to a non-production server";
    }

    /// <summary>
    /// Learn-and-drive v3 P2 (§3.1) — WHAT THE EDITOR HINTS about its server, SHOWN beside the non-production tick and
    /// NEVER acted on. Unity has no editor-wide "not production" signal, and a hint read as a verdict fails open on a
    /// game that compiles cheats into every build (§3.1, A6 blocker 1). Pure: the window passes the project's scripting
    /// defines in. The "backend URL containing dev/staging" hint of the plan is NOT built — it needs a per-game
    /// config read (stated in TRUST.md).
    /// </summary>
    public static class NonProductionHints
    {
        private static readonly string[] DevWords = { "dev", "debug", "staging", "stage", "test", "qa", "cheat", "sandbox" };
        private static readonly string[] ProdWords = { "release", "prod", "production", "live" };

        /// <summary>One sentence per define that sounds like either side, in the defines' order. Never throws.</summary>
        public static IReadOnlyList<string> FromDefines(IEnumerable<string>? defines, bool developmentBuild)
        {
            var hints = new List<string>();
            if (developmentBuild) hints.Add("Build Settings: Development Build is on");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in defines ?? Array.Empty<string>())
            {
                var d = (raw ?? "").Trim();
                if (d.Length == 0 || !seen.Add(d)) continue;
                var words = PressGuard.Words(d.Replace('_', ' ')).ToList();
                if (words.Any(w => ProdWords.Contains(w)))
                    hints.Add($"scripting define '{d}' is set — this may be a PRODUCTION configuration");
                else if (words.Any(w => DevWords.Any(dev => w.StartsWith(dev, StringComparison.Ordinal))))
                    hints.Add($"scripting define '{d}' is set — sounds like a development configuration");
            }
            if (!seen.Any(d => PressGuard.Words(d.Replace('_', ' ')).Any(w => ProdWords.Contains(w))))
                hints.Add("no RELEASE / PROD scripting define is set");
            return hints;
        }
    }
}
