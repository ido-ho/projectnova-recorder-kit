using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P3 (§3.3) — WHICH ASSEMBLIES THE CHEAT SEARCH READS: the game's own player code and the
    /// packages it ships (a console package is one — IngameDebugConsole compiles to its own player assembly), never
    /// Unity's, the runtime's, this kit's, or an editor-only / test assembly (an editor tool is not a cheat the game a
    /// tick drives registers — the rule source 3 applies to <c>Editor/</c> folders).
    /// </summary>
    public static class CheatScanAssemblies
    {
        private static readonly string[] NotGamePrefixes =
        {
            "Unity.", "UnityEngine", "UnityEditor", "System", "Mono.", "mscorlib", "netstandard", "Newtonsoft.",
            "nunit.", "ProjectNova.RecorderKit", "Microsoft.", "Bee.", "ExCSS", "PlayerBuildProgram",
        };

        /// <summary>THE RULE, by name: may an assembly named <paramref name="name"/> be read, given whether Unity builds
        /// it into the player? Pure — the tests hold it without a game.</summary>
        public static bool IsGameAssembly(string? name, bool inPlayer)
        {
            if (!inPlayer || string.IsNullOrWhiteSpace(name)) return false;
            foreach (var p in NotGamePrefixes)
                if (name!.StartsWith(p, StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>The loaded assemblies Unity compiles into this project's PLAYER (no test assemblies), by the rule.</summary>
        public static IReadOnlyList<Assembly> Game()
        {
            var player = new HashSet<string>(
                UnityEditor.Compilation.CompilationPipeline
                    .GetAssemblies(UnityEditor.Compilation.AssembliesType.PlayerWithoutTestAssemblies)
                    .Select(a => a.name),
                StringComparer.Ordinal);
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && IsGameAssembly(a.GetName().Name, player.Contains(a.GetName().Name ?? "")))
                .OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>A type by its full name, in the given assemblies only — what the search looks a console up with, so
        /// a test's fixture assembly is the whole world of a test run.</summary>
        public static Func<string, Type?> FinderOver(IEnumerable<Assembly> assemblies)
        {
            var list = assemblies.ToList();
            return fullName =>
            {
                foreach (var a in list)
                {
                    try
                    {
                        var t = a.GetType(fullName, false);
                        if (t != null) return t;
                    }
                    catch (Exception) { }
                }
                return null;
            };
        }
    }

    /// <summary>
    /// v3 P3 — THE STATIC HALF OF ONE SEARCH (sources 2 and 4, and the registries source 1 will read): the scan, the
    /// known consoles present, and every note. Built in Edit Mode, before anything runs.
    /// </summary>
    public sealed class CheatStaticSearch
    {
        public CheatTagScan.Result Scan { get; private set; } = new();
        /// <summary>Every registry source 1 would read: the known consoles present, then the custom ones, by where.</summary>
        public List<RegistryRef> Registries { get; } = new();
        public List<string> Notes { get; } = new();
        /// <summary>Kit 0.13.2: where the player's data lives, by type (<see cref="DataRoots"/>) — what a proof with no
        /// check chosen snapshots.</summary>
        public DataRoots.Result DataRoots { get; private set; } = new();

        /// <summary>For the tests: the managed thread the last <see cref="Run"/> ran on.</summary>
        internal static int LastRunThreadForTests { get; private set; }

        /// <summary>
        /// Kit 0.14.3 (deep review KIT-15, invariant 192) — THE SAME SEARCH, OFF THE EDITOR'S MAIN THREAD. The IL scan has a
        /// 20 s budget (<see cref="CheatTagScan.BudgetMs"/>) and the data-roots walk 1.5 s more; run inline between two
        /// yields, a big game froze the editor for up to ~21.5 s, started from the web. It reads metadata only — types,
        /// method bodies' IL, <c>typeof</c> comparisons, <see cref="Levers.NotTickableReason"/>'s pure text rules — so it
        /// runs on a worker, and the agent yields a frame at a time until it is done. <paramref name="assemblies"/> must be
        /// read on the main thread first (<see cref="CheatScanAssemblies.Game"/> asks CompilationPipeline).
        /// </summary>
        public static System.Threading.Tasks.Task<CheatStaticSearch> Start(IReadOnlyList<Assembly> assemblies) =>
            System.Threading.Tasks.Task.Run(() => Run(assemblies));

        public static CheatStaticSearch Run(IReadOnlyList<Assembly> assemblies, int budgetMs = CheatTagScan.BudgetMs)
        {
            LastRunThreadForTests = Environment.CurrentManagedThreadId;
            var find = CheatScanAssemblies.FinderOver(assemblies);
            var s = new CheatStaticSearch { Scan = CheatTagScan.Scan(assemblies, find, budgetMs) };
            s.DataRoots = ProjectNova.RecorderKit.DataRoots.Find(assemblies);
            if (s.DataRoots.Roots.Count == 0)
                s.Notes.Add("no static member of the game's code holds a data model (a class of the game's with 3 or more data fields) — " +
                            "a cheat with no check chosen has no player data to snapshot; choose its check on the website");
            if (s.DataRoots.CutByBudget)
                s.Notes.Add($"the search for where the player's data lives stopped at its time budget ({ProjectNova.RecorderKit.DataRoots.BudgetMs} ms) — the rest of the code was not read for it");
            foreach (var row in KnownConsoles.Rows)
            {
                var r = RegistryRef.FromKnown(row, find, out var why);
                if (why != null) s.Notes.Add(why);
                if (r == null) continue;
                s.Registries.Add(r);
                if (!row.CheckedAgainstSource)
                    s.Notes.Add($"{row.Id}: its registry fields are from its documentation, not checked against the asset — " +
                                "if it reads nothing, that is this kit's table, not the game");
            }
            foreach (var c in s.Scan.CustomRegistries)
                if (s.Registries.All(r => r.Field != c.Field))
                    s.Registries.Add(c);
            s.Notes.AddRange(s.Scan.Notes);
            if (s.Registries.Count == 0)
                s.Notes.Add("no console registry was found in the code (no known console, and no class with a Register…(string) " +
                            "method and a static list of names) — the registry read has nothing to read; that is not proof the game has no cheats");
            return s;
        }
    }
}
