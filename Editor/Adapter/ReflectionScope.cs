using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Kit 0.14.3 (deep review KIT-2, invariant 192) — WHAT A <c>call</c> OR A <c>set</c> MAY REACH: THE GAME'S OWN CODE.
    ///
    /// Before this, <see cref="GameReflection.FindType"/> indexed every loaded assembly and the bridge's <c>call</c> / <c>set</c>
    /// ran whatever type the name resolved to — <c>call System.Diagnostics.Process.Start /bin/sh</c> and
    /// <c>call System.IO.File.WriteAllText …</c> were valid cheats, reachable through the file relay with no tick and from
    /// the cloud behind one "Allow these N" press. The cheat SEARCH already read only the game's assemblies
    /// (<see cref="CheatScanAssemblies"/>); the RUN did not. Now both use the same rule.
    ///
    /// Two halves, because a tick list is refused WHOLE by <see cref="TickList"/> and the website must be able to predict
    /// that refusal byte for byte:
    /// <list type="number">
    /// <item><b>By name</b> (<see cref="NameRefusal"/>) — pure, shared with the website through
    /// <c>Editor/Tests/Fixtures/runner-scope.cases.json</c>: a <c>call</c>/<c>set</c> whose target is spelled in the
    /// runtime's or the engine's namespaces (<see cref="BlockedNamespaces"/>) is not tickable, except the few engine
    /// members a shot legitimately writes (<see cref="EngineMembers"/>). Asked by <see cref="Levers.NotTickableReason"/>.</item>
    /// <item><b>By the type the name resolves to</b> (<see cref="TypeRefusal"/>, <see cref="ResolvedRefusal"/>) — kit only,
    /// because only the editor can resolve a bare <c>Process</c> to <c>System.Diagnostics.Process</c>: the type's assembly
    /// must be one of the game's player assemblies (<see cref="CheatScanAssemblies.IsGameAssembly"/>), or the exact member
    /// must be one of <see cref="EngineMembers"/>. Asked where a person ticks (<see cref="Levers.SetApproved"/>, the
    /// "Allow these N" list, a lever row's note) and where the command RUNS (<see cref="GameReflection.TryCall"/>,
    /// <see cref="GameReflection.TryCallGeneric"/>, <see cref="GameReflection.TrySetPath"/>) — so such a command is neither
    /// tickable nor runnable, by any door, ticked or not.</item>
    /// </list>
    /// What it does NOT cover, said so: <c>get</c> (a read; a static property with a body is never run —
    /// <see cref="GameReflection.TryReadStaticProperty"/>), a <c>set</c> whose ROOT is a game type and whose path walks into
    /// an engine object the game holds (the game handed that object out), a <c>camera-pose</c> (its methods are the
    /// ticked camera-spec's), and game code shipped only as a precompiled DLL — not a player assembly Unity compiles, so the
    /// search never proposed it and a <c>call</c> now refuses it too.
    /// </summary>
    public static class ReflectionScope
    {
        /// <summary>The namespaces a <c>call</c>/<c>set</c> target may not be spelled in — the runtime's and the engine's. Held
        /// equal to <c>runner-scope.cases.json</c> (<c>blockedNamespaces</c>) and to the website's
        /// <c>BLOCKED_RUNNER_NAMESPACES</c> (renderer <c>lane-shots.ts</c>).</summary>
        public static readonly IReadOnlyList<string> BlockedNamespaces = new[]
        {
            "System", "UnityEditor", "UnityEngine", "Unity", "Microsoft", "Mono", "Newtonsoft",
        };

        /// <summary>
        /// The engine members a shot may write or call, as <c>verb Namespace.Type.member</c> — each one seen in a real pack:
        /// the mute-all fallback (<c>set UnityEngine.AudioListener.volume 0</c>, onboard-game SKILL.md), a paused board
        /// (<c>set Time.timeScale 0</c>, sentaur), and a tutorial skipped by its PlayerPrefs keys
        /// (<c>call PlayerPrefs.SetInt &lt;Key&gt; 1</c> + <c>call PlayerPrefs.Save</c>, intake-signals.md). No delete is here
        /// (<c>PlayerPrefs.DeleteAll</c>, <c>DeleteKey</c>): a wipe is never an engine lever. Held equal to
        /// <c>runner-scope.cases.json</c> (<c>engineMembers</c>).
        /// </summary>
        public static readonly IReadOnlyList<string> EngineMembers = new[]
        {
            "set UnityEngine.Time.timeScale",
            "set UnityEngine.AudioListener.volume",
            "set UnityEngine.AudioListener.pause",
            "call UnityEngine.PlayerPrefs.SetInt",
            "call UnityEngine.PlayerPrefs.SetFloat",
            "call UnityEngine.PlayerPrefs.SetString",
            "call UnityEngine.PlayerPrefs.Save",
            "call UnityEngine.PlayerPrefs.HasKey",
            "call UnityEngine.PlayerPrefs.GetInt",
            "call UnityEngine.PlayerPrefs.GetFloat",
            "call UnityEngine.PlayerPrefs.GetString",
        };

        /// <summary>The sentence's start, shared by every refusal of this rule (the window, the gate log, the relay answer).</summary>
        public const string RefusalStart = "the kit only calls and sets the game's own code";

        private static string Verb(string? command, out string target)
        {
            target = "";
            var words = (command ?? "").Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) return "";
            // the bridge lowercases the verb before it dispatches (`CALL X.Y` is a call), so this rule does too
            var verb = words[0].ToLowerInvariant();
            if (verb != "call" && verb != "set") return "";
            target = words[1];
            return verb;
        }

        private static bool SpelledInBlocked(string target) =>
            BlockedNamespaces.Any(ns => target == ns || target.StartsWith(ns + ".", StringComparison.Ordinal)
                                                     || target.StartsWith(ns + "+", StringComparison.Ordinal));

        /// <summary>
        /// THE NAME RULE — pure, shared with the website. Null = fine by name; otherwise why, in one sentence. Only a
        /// <c>call</c> or <c>set</c> (the two reflection verbs that run or write) is judged; its target is the second word.
        /// </summary>
        public static string? NameRefusal(string? command)
        {
            var verb = Verb(command, out var target);
            if (verb.Length == 0 || !SpelledInBlocked(target)) return null;
            if (EngineMembers.Contains(verb + " " + target, StringComparer.Ordinal)) return null;
            return $"{RefusalStart}: '{target}' is in the runtime's or the engine's own code ({string.Join(", ", BlockedNamespaces)}), " +
                   "which can reach outside your game (files, processes, the editor). It cannot be approved here; ask for a lever of the game's own classes.";
        }

        // ---- the resolved half (editor only) --------------------------------------------------------------------------

        /// <summary>For the tests: assemblies treated as the game's (a test's fixture types live in the test assembly, whose
        /// name the rule refuses). Set by the tests' one-time setup; empty in production.</summary>
        internal static readonly HashSet<Assembly> AlsoRunnableForTests = new();

        private static HashSet<string>? _playerAssemblies;
        private static readonly object PlayerLock = new();

        /// <summary>The names of the assemblies Unity compiles into this project's player — read once per domain, on the
        /// first ask. <c>CompilationPipeline</c> is main-thread only; every caller of this class is on the editor's main thread
        /// (the gate, the window, the bridge), never the cheat search's worker (which reads only <see cref="NameRefusal"/>).</summary>
        /// <remarks>MED-1 (kit 0.14.3): a read that THROWS is not cached — it fails closed for that one ask and is tried again
        /// on the next, with one console warning per domain. Before 0.14.3 one throw (an ask mid-compile) cached an empty list
        /// until the next domain reload, and every game cheat was refused as "not one of your game's assemblies".</remarks>
        private static HashSet<string> PlayerAssemblies()
        {
            lock (PlayerLock)
            {
                if (_playerAssemblies != null) return _playerAssemblies;
                try
                {
                    var names = PlayerAssemblyNamesForTests?.Invoke() ??
                                UnityEditor.Compilation.CompilationPipeline
                                    .GetAssemblies(UnityEditor.Compilation.AssembliesType.PlayerWithoutTestAssemblies)
                                    .Select(a => a.name);
                    _playerAssemblies = new HashSet<string>(names, StringComparer.Ordinal);
                    return _playerAssemblies;
                }
                catch (Exception e)
                {
                    // fail closed for THIS ask only: nothing cached, the next ask reads again
                    if (!_warnedUnreadable)
                    {
                        _warnedUnreadable = true;
                        UnityEngine.Debug.LogWarning("[RecorderKit] the list of your game's assemblies could not be read (" + e.Message +
                                                     ") — game cheats are refused until it can be; the kit asks again on the next one.");
                    }
                    return new HashSet<string>(StringComparer.Ordinal);
                }
            }
        }

        private static bool _warnedUnreadable;

        /// <summary>For the tests: stands in for <c>CompilationPipeline.GetAssemblies</c> (a throw included). Never set by the kit.</summary>
        internal static Func<IEnumerable<string>>? PlayerAssemblyNamesForTests;

        /// <summary>For the tests: forget the cached list and the warning, as a domain reload would.</summary>
        internal static void ForgetPlayerAssembliesForTests()
        {
            lock (PlayerLock)
            {
                _playerAssemblies = null;
                _warnedUnreadable = false;
            }
        }

        /// <summary>For the tests: is <paramref name="name"/> one of the player assemblies, as the type rule reads them?</summary>
        internal static bool IsPlayerAssemblyForTests(string name) => PlayerAssemblies().Contains(name);

        /// <summary>Is this type the game's own — in a player assembly the rule reads as the game's, or one the tests named?</summary>
        public static bool IsGameType(Type? type)
        {
            if (type == null) return false;
            var asm = type.Assembly;
            if (AlsoRunnableForTests.Contains(asm)) return true;
            var name = asm.GetName().Name;
            return CheatScanAssemblies.IsGameAssembly(name, PlayerAssemblies().Contains(name ?? ""));
        }

        /// <summary>
        /// THE TYPE RULE — the one the executor asks. <paramref name="type"/> is what the name resolved to, <paramref name="member"/>
        /// the member named right after it, <paramref name="exact"/> whether the path is exactly <c>Type.member</c> (an engine
        /// member is allowed only then). Null = may run.
        /// </summary>
        public static string? TypeRefusal(string verb, Type type, string member, bool exact = true)
        {
            if (IsGameType(type)) return null;
            if (exact && EngineMembers.Contains($"{verb} {type.FullName}.{member}", StringComparer.Ordinal)) return null;
            return $"{RefusalStart}: '{type.FullName}' is in '{type.Assembly.GetName().Name}', which is not one of your game's " +
                   "assemblies — a call or a set there can reach outside your game (files, processes, the editor), so it never runs. " +
                   $"If your game has its own {type.Name}, write its full name (Namespace.{type.Name}).";
        }

        /// <summary>
        /// The type rule for a whole COMMAND, resolved the way the bridge will resolve it: a <c>call A.B.M</c> looks up the type
        /// <c>A.B</c>; a <c>set</c> path's root is its longest leading run of names that is a type
        /// (<see cref="GameReflection.TryResolveOwner"/>'s own rule). Null for any other verb, and for a name that resolves to
        /// nothing (nothing would run — the bridge says so itself). Editor only.
        /// </summary>
        public static string? ResolvedRefusal(string? command)
        {
            var verb = Verb(command, out var target);
            if (verb.Length == 0) return null;
            if (NameRefusal(command) is { } byName) return byName;
            if (target.IndexOf('{') >= 0) return null; // a template's placeholder is not a name to resolve
            if (!RootOf(verb, target, out var type, out var member, out var exact)) return null;
            return TypeRefusal(verb, type!, member, exact);
        }

        /// <summary>The type a command's target starts with, the member after it, and whether nothing follows that member.</summary>
        internal static bool RootOf(string verb, string target, out Type? type, out string member, out bool exact)
        {
            type = null;
            member = "";
            exact = false;
            if (target.IndexOf('[') >= 0) target = target.Substring(0, target.IndexOf('['));
            var segments = target.Split('.');
            if (segments.Length < 2) return false;
            if (verb == "call")
            {
                type = GameReflection.FindType(string.Join(".", segments.Take(segments.Length - 1)));
                member = segments[^1];
                exact = true;
                return type != null;
            }
            for (var take = segments.Length - 1; take >= 1; take--)
            {
                type = GameReflection.FindType(string.Join(".", segments.Take(take)));
                if (type == null) continue;
                member = segments[take];
                exact = take == segments.Length - 1;
                return true;
            }
            return false;
        }
    }
}
