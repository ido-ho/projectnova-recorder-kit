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

        public static CheatStaticSearch Run(IReadOnlyList<Assembly> assemblies, int budgetMs = CheatTagScan.BudgetMs)
        {
            var find = CheatScanAssemblies.FinderOver(assemblies);
            var s = new CheatStaticSearch { Scan = CheatTagScan.Scan(assemblies, find, budgetMs) };
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
