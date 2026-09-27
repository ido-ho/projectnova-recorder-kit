using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P3 (§3.3) — ONE CHEAT THE DEEP CHEAT SEARCH FOUND, as the kit reports it. FACTS, never a
    /// verdict: the website labels the kind and the risk and merges the sources into <c>learn/cheat-candidates.json</c>
    /// (<c>apps/renderer/src/game-ads/learn/cheat-search.ts</c>), and nothing here is ever a tick. A finding's
    /// <see cref="Command"/> is only a PROPOSAL of the fixed command a person may tick in the Nova Capture window; it
    /// runs nothing until that tick exists (<see cref="Levers"/>).
    /// </summary>
    public sealed class CheatFinding
    {
        /// <summary>The three kit sources of §3.3 (the code-line read, source 3, is the server's).</summary>
        public const string RegistrySource = "registry";
        public const string TagScanSource = "tag-scan";
        public const string DebugMethodSource = "debug-method";

        /// <summary>Which search found it: <see cref="RegistrySource"/>, <see cref="TagScanSource"/> or
        /// <see cref="DebugMethodSource"/>.</summary>
        public string Source { get; set; } = "";

        /// <summary>The name the game gives the cheat — a registered console name, or a method's own name. Null when it
        /// cannot be read without running code (a name built at run time); never invented.</summary>
        public string? Name { get; set; }

        /// <summary>The FIXED command a tick would run (<c>call Type.Method "name"</c>), or null when the kit knows no
        /// fixed command for it (a method that needs argument values, a console with no executor this kit knows).</summary>
        public string? Command { get; set; }

        /// <summary>The console it belongs to: a known-console id (<see cref="KnownConsoles"/>), <c>custom</c>, or null.</summary>
        public string? Console { get; set; }

        /// <summary>Where it was found: <c>Namespace.Type.Member</c> — code, never a file on disk.</summary>
        public string Where { get; set; } = "";

        /// <summary>A method's parameters, <c>(int amount, bool all)</c>, when the command needs values; else null.</summary>
        public string? Signature { get; set; }

        /// <summary>Why there is no command, or anything else a person needs to read beside it; else null.</summary>
        public string? Note { get; set; }

        /// <summary>The longest name a finding carries (the kit's lever cap: a name longer than a command can be is no
        /// name a tick can use).</summary>
        public const int MaxNameLength = 256;

        public JObject ToJson()
        {
            var o = new JObject { ["source"] = Source, ["where"] = Where };
            o["name"] = Name == null ? JValue.CreateNull() : Name;
            o["command"] = Command == null ? JValue.CreateNull() : Command;
            o["console"] = Console == null ? JValue.CreateNull() : Console;
            if (Signature != null) o["signature"] = Signature;
            if (Note != null) o["note"] = Note;
            return o;
        }

        /// <summary>
        /// THE FIXED COMMAND that fires a registered cheat by name through its console's executor — a static method that
        /// takes the name as one string: <c>call Type.Method "name"</c>, the name always quoted (the bridge's
        /// <see cref="ReflectionCheatBridge.SplitCommand"/> keeps a quoted span as one argument, so a spaced name is one
        /// argument). Null, with the reason, when no fixed command can say it: a name holding a quote or a character the
        /// window would draw differently, or a command the window could not tick (<see cref="Levers.NotTickableReason"/>)
        /// or that is longer than a lever may be.
        /// </summary>
        public static string? ExecutorCommand(string executor, string name, out string? why)
        {
            why = null;
            if (name.IndexOf('"') >= 0)
            {
                why = "the name holds a double quote, which no fixed command can pass as one argument";
                return null;
            }
            var command = $"call {executor} \"{name}\"";
            return Tickable(command, out why) ? command : null;
        }

        /// <summary>A parameterless method called by its full name: <c>call Type.Method</c> — or null, with why.</summary>
        public static string? CallCommand(Type type, string method, out string? why)
        {
            why = null;
            if (type.IsNested || type.IsGenericType || type.FullName == null)
            {
                why = "a nested or generic class — the bridge's `call` finds a type by its plain full name";
                return null;
            }
            var command = $"call {type.FullName}.{method}";
            return Tickable(command, out why) ? command : null;
        }

        private static bool Tickable(string command, out string? why)
        {
            why = null;
            if (command.Length > Levers.MaxLeverLength)
            {
                why = $"the command would be {command.Length} characters, and a lever may be at most {Levers.MaxLeverLength}";
                return false;
            }
            if (Levers.NotTickableReason(command) is { } reason)
            {
                why = reason;
                return false;
            }
            return true;
        }

        /// <summary>One finding per (source, name, where), first kept — a console method registered twice is one row.</summary>
        public static List<CheatFinding> Distinct(IEnumerable<CheatFinding> findings)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var kept = new List<CheatFinding>();
            foreach (var f in findings)
                if (seen.Add($"{f.Source}\u0001{f.Name}\u0001{f.Where}\u0001{f.Command}"))
                    kept.Add(f);
            return kept;
        }

        /// <summary>Words of an identifier, lowercased: <c>GMTools</c> → gm, tools; <c>DebugPanel2</c> → debug, panel.</summary>
        internal static IReadOnlyList<string> WordsOf(string identifier)
        {
            var spaced = System.Text.RegularExpressions.Regex.Replace(identifier, "([A-Z]+)([A-Z][a-z])", "$1 $2");
            spaced = System.Text.RegularExpressions.Regex.Replace(spaced, "([a-z0-9])([A-Z])", "$1 $2");
            return System.Text.RegularExpressions.Regex.Split(spaced, "[^A-Za-z]+")
                .Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()).ToList();
        }
    }
}
