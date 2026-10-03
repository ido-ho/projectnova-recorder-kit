using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Kit 0.13.2 — WHERE THE PLAYER'S DATA LIVES: the static members of the game's own code that hold a large data
    /// model, found from TYPES only (no value is read, no getter runs), so a cheat with no check chosen can be proven by a
    /// snapshot of that root (<see cref="SnapshotRead"/>) without the studio typing a path.
    ///
    /// THE RULE — generic, bounded, no game's name in it:
    /// <list type="bullet">
    /// <item>over the assemblies the cheat search reads (<see cref="CheatScanAssemblies.Game"/>): every non-generic,
    /// non-nested, not compiler-made type whose full name the data-path grammar can write (<see cref="DataPath"/>);</item>
    /// <item>its STATIC fields and static AUTO-properties (a property whose value is a compiler-made backing field — a
    /// computed static property such as <c>X =&gt; Instance.GetController(…)</c> runs code when read, so it is never a
    /// root);</item>
    /// <item>whose declared type is a class or struct of the game's own assemblies — never a <c>UnityEngine.Object</c>
    /// (a scene object or an asset), a delegate, or the runtime's;</item>
    /// <item>with at least <see cref="MinDataFields"/> data-shaped instance fields (a number, bool, enum, string, one of
    /// the runtime's collections, or another class / struct of the game's).</item>
    /// </list>
    /// RANKED by a score: data-shaped fields, plus <see cref="NestedWeight"/> for each that is itself a game model or a
    /// collection, plus <see cref="SerializableBonus"/> when the type is <c>[Serializable]</c>, plus / minus
    /// <see cref="NameWeight"/> when the member's or type's name has a word of player data (player, user, save, profile,
    /// progress, account, inventory, wallet) or of static game content (config, setting, database, catalog, definition,
    /// table, remote, cache, pool, registry). Ties by path. The best <see cref="MaxRoots"/> are reported; the website
    /// snapshots the first. At most <see cref="BudgetMs"/> of scanning.
    /// </summary>
    public static class DataRoots
    {
        public const int MaxRoots = 5;
        public const int MinDataFields = 3;
        public const int NestedWeight = 2;
        public const int SerializableBonus = 5;
        public const int NameWeight = 10;
        public const int BudgetMs = 1500;

        private static readonly string[] PlayerWords = { "player", "user", "save", "profile", "progress", "account", "inventory", "wallet" };
        private static readonly string[] ContentWords = { "config", "setting", "database", "catalog", "definition", "table", "remote", "cache", "pool", "registry" };

        public sealed class Root
        {
            public string Path { get; set; } = "";
            public string Type { get; set; } = "";
            public int Score { get; set; }
            public int Fields { get; set; }

            public JObject ToJson() => new() { ["path"] = Path, ["type"] = Type, ["score"] = Score, ["fields"] = Fields };
        }

        public sealed class Result
        {
            public List<Root> Roots { get; } = new();
            public bool CutByBudget { get; set; }
        }

        /// <summary>The roots, best first. Never throws: a type that cannot be read is skipped.</summary>
        public static Result Find(IReadOnlyList<Assembly> assemblies, int budgetMs = BudgetMs)
        {
            IEnumerable<Type> TypesOf(Assembly asm)
            {
                try { return asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).Cast<Type>().ToArray(); }
                catch (Exception) { return Array.Empty<Type>(); }
            }
            return FindAmong(assemblies.SelectMany(TypesOf), new HashSet<Assembly>(assemblies), budgetMs);
        }

        /// <summary>The rule over these owner types, <paramref name="game"/> being the game's assemblies — the tests hand
        /// it one fixture namespace.</summary>
        internal static Result FindAmong(IEnumerable<Type> owners, HashSet<Assembly> game, int budgetMs = BudgetMs)
        {
            var result = new Result();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var found = new Dictionary<string, Root>(StringComparer.Ordinal);
            foreach (var t in owners)
            {
                if (clock.ElapsedMilliseconds > budgetMs) { result.CutByBudget = true; break; }
                try { Consider(t, game, found); }
                catch (Exception) { }
            }
            result.Roots.AddRange(found.Values
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Path, StringComparer.Ordinal)
                .Take(MaxRoots));
            return result;
        }

        private static void Consider(Type owner, HashSet<Assembly> game, Dictionary<string, Root> found)
        {
            if (owner.IsGenericTypeDefinition || owner.IsNested || owner.FullName == null || owner.Name.IndexOf('<') >= 0) return;
            const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var f in owner.GetFields(statics))
            {
                if (f.IsLiteral) continue;
                var member = SnapshotRead.MemberNameOf(f.Name);
                if (member == null) continue;
                var path = owner.FullName + "." + member;
                if (!DataPath.IsPath(path) || found.ContainsKey(path)) continue;
                var score = ScoreOf(f.FieldType, game, member, out var fields);
                if (score == null) continue;
                found[path] = new Root { Path = path, Type = f.FieldType.FullName ?? f.FieldType.Name, Score = score.Value, Fields = fields };
            }
        }

        /// <summary>Null when the type is not a data model of the game's; else its score (<see cref="DataRoots"/>).</summary>
        internal static int? ScoreOf(Type type, HashSet<Assembly> game, string member, out int fields)
        {
            fields = 0;
            if (!IsGameModel(type, game)) return null;
            var nested = 0;
            foreach (var (_, f) in SnapshotRead.DataFields(type))
            {
                var ft = f.FieldType;
                if (IsGameModel(ft, game) || IsRuntimeCollection(ft)) { fields++; nested++; }
                else if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string) || ft == typeof(decimal)
                         || (Nullable.GetUnderlyingType(ft) is { } u && (u.IsPrimitive || u.IsEnum))) fields++;
            }
            if (fields < MinDataFields) return null;
            var score = fields + NestedWeight * nested + (type.IsDefined(typeof(SerializableAttribute), false) ? SerializableBonus : 0);
            var words = (member + " " + type.Name).ToLowerInvariant();
            if (PlayerWords.Any(w => words.Contains(w))) score += NameWeight;
            if (ContentWords.Any(w => words.Contains(w))) score -= NameWeight;
            return score;
        }

        private static bool IsGameModel(Type t, HashSet<Assembly> game) =>
            (t.IsClass || (t.IsValueType && !t.IsPrimitive && !t.IsEnum)) && game.Contains(t.Assembly)
            && !typeof(UnityEngine.Object).IsAssignableFrom(t) && !typeof(Delegate).IsAssignableFrom(t)
            && !t.IsGenericTypeDefinition && !SnapshotRead.IsRuntimeType(t);

        private static bool IsRuntimeCollection(Type t) =>
            SnapshotRead.IsRuntimeType(t) && typeof(System.Collections.ICollection).IsAssignableFrom(t);
    }
}
