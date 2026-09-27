using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P3 (§3.3 source 1) — READ THE RUNNING GAME'S CHEAT REGISTRY: the names each console holds in
    /// its static list or dictionary, in the scene the game reached by itself.
    ///
    /// THIS IS A TICKED READ. It reads static fields (no getter), but walking a collection and, for a list of objects,
    /// a name PROPERTY, is code of the game's classes; so the search job runs it only when <see cref="Lever"/> is ticked
    /// in the Nova Capture window, with the gate forced on (<see cref="Levers.LeverRefusal"/> with the cloud flag, as
    /// every kit job asks it — <see cref="KitJobRun"/>).
    ///
    /// READ UNTIL STABLE (spike A: 40 names right after the lobby appeared, 63 a moment later): <see cref="StableRead"/>
    /// takes reads until two in a row hold the same names. NOTHING HERE THROWS: a console that cannot be read is a
    /// reason beside its row.
    /// </summary>
    public static class CheatRegistry
    {
        /// <summary>The kit verb a person ticks for the search to read the registry. A fixed command with no argument.</summary>
        public const string Lever = "cheat-registry";

        /// <summary>The most names one console's read keeps.</summary>
        public const int MaxNames = 1000;

        public sealed class ConsoleRead
        {
            public RegistryRef Registry { get; set; } = null!;
            public List<string> Names { get; set; } = new();
            public string? Error { get; set; }
            /// <summary>Names the read left out: longer than a command can be, or past <see cref="MaxNames"/>.</summary>
            public int Cut { get; set; }
        }

        /// <summary>One read of every registry, in the given order.</summary>
        public static List<ConsoleRead> ReadOnce(IEnumerable<RegistryRef> registries) =>
            registries.Select(ReadOne).ToList();

        public static ConsoleRead ReadOne(RegistryRef r)
        {
            var read = new ConsoleRead { Registry = r };
            try
            {
                var value = r.Field.GetValue(null);
                if (value == null)
                {
                    read.Error = $"{r.Where} is null — the console has not been set up in this scene";
                    return read;
                }
                IEnumerable items = value is IDictionary d ? d.Keys : value as IEnumerable ?? Array.Empty<object>();
                foreach (var item in items)
                {
                    var name = NameOf(item, r.ItemName);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (name!.Length > CheatFinding.MaxNameLength || read.Names.Count >= MaxNames)
                    {
                        read.Cut++;
                        continue;
                    }
                    if (!read.Names.Contains(name)) read.Names.Add(name);
                }
            }
            catch (Exception e)
            {
                // a collection the game changed while it was walked, a getter that threw — a fact, not a crash
                var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                read.Error = $"{r.Where} could not be read: {inner.GetType().Name}: {inner.Message}";
                read.Names.Clear();
            }
            return read;
        }

        private static string? NameOf(object? item, string? member)
        {
            if (item == null) return null;
            if (member == null) return item as string;
            var t = item.GetType();
            var inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var f = t.GetField(member, inst);
            if (f != null) return f.GetValue(item) as string;
            var p = t.GetProperty(member, inst);
            return p?.GetValue(item) as string;
        }

        /// <summary>
        /// THE STABLE READ — fed one full read at a time (the job reads every <see cref="IntervalSec"/> seconds), done
        /// when two reads in a row hold the same names per console, or after <see cref="MaxReads"/>. An EMPTY read pair is
        /// not stable before <see cref="EmptyGraceReads"/> reads: a console that fills itself a few seconds after boot
        /// would otherwise read "nothing" twice and stop. Pure: no clock, no scene — the job owns the waiting.
        /// </summary>
        public sealed class StableRead
        {
            public const double IntervalSec = 2;
            public const int MaxReads = 15;
            public const int EmptyGraceReads = 5;

            private readonly List<int> _counts = new();
            private List<ConsoleRead>? _last;

            /// <summary>The total name count of every read so far — what the website shows ("40 → 63 → 63").</summary>
            public IReadOnlyList<int> Counts => _counts;
            public List<ConsoleRead>? Last => _last;
            public bool Stable { get; private set; }
            public bool Done => Stable || _counts.Count >= MaxReads;

            /// <summary>Take one read. True when the search may stop now.</summary>
            public bool Offer(List<ConsoleRead> read)
            {
                if (Done) return true;
                var total = read.Sum(r => r.Names.Count);
                var same = _last != null && Same(_last, read);
                _counts.Add(total);
                _last = read;
                if (same && (total > 0 || _counts.Count >= EmptyGraceReads)) Stable = true;
                return Done;
            }

            private static bool Same(List<ConsoleRead> a, List<ConsoleRead> b)
            {
                if (a.Count != b.Count) return false;
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i].Names.Count != b[i].Names.Count) return false;
                    if (!new HashSet<string>(a[i].Names, StringComparer.Ordinal).SetEquals(b[i].Names)) return false;
                }
                return true;
            }
        }

        /// <summary>The findings a finished read yields: one per name, with the console's executor command when it has one.</summary>
        public static List<CheatFinding> Findings(IEnumerable<ConsoleRead> reads)
        {
            var found = new List<CheatFinding>();
            foreach (var r in reads)
                foreach (var name in r.Names)
                {
                    string? why = "this console has no executor this kit knows — the name cannot be proposed as one command yet";
                    var command = r.Registry.Executor == null ? null : CheatFinding.ExecutorCommand(r.Registry.Executor, name, out why);
                    found.Add(new CheatFinding
                    {
                        Source = CheatFinding.RegistrySource, Name = name, Command = command,
                        Console = r.Registry.Console, Where = r.Registry.Where, Note = command == null ? why : null,
                    });
                }
            return found;
        }
    }
}
