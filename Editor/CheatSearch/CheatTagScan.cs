using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P3 (§3.3 sources 2 and 4) — THE STATIC SEARCH over the game's compiled code. RUNS NO GAME
    /// CODE: it reads types, attribute DATA (<see cref="CustomAttributeData"/> — an attribute's constructor never runs)
    /// and IL bytes (<see cref="IlCallSites"/>). No static constructor runs either: none of these touch a static member.
    ///
    /// Source 2, cheat-tool tags: a known console's command attribute (<see cref="KnownConsoles"/>), an options class
    /// (SRDebugger's <c>SROptions</c>), and <c>Register*("…")</c> / <c>AddCommand("…")</c> call sites — whose callee's
    /// class, when it has the custom-console shape (<see cref="CustomConsoleRule"/>), is a registry source 1 reads.
    /// When that class is a THIN FORWARDER (no executor, no list — it only hands the name on to another class's
    /// <c>Register…</c>), the forward is followed to the class that runs a name (<c>ForwardOf</c>, 0.13.1).
    ///
    /// Source 4, debug methods with no console (<see cref="DebugMethods"/>): methods declared on classes whose NAME has
    /// the word Debug / Cheat / Dev / Test / GM.
    ///
    /// The assemblies are the caller's (<see cref="CheatScanAssemblies"/>): the job passes the game's, a test its own.
    /// </summary>
    public static class CheatTagScan
    {
        /// <summary>How long one search may spend reading IL before it stops and says so.</summary>
        public const int BudgetMs = 20000;

        /// <summary>The most debug-method findings one search keeps (the window lists 300 rows at most).</summary>
        public const int MaxDebugMethods = 300;

        public sealed class Result
        {
            public List<CheatFinding> Findings { get; } = new();
            /// <summary>The custom consoles the call sites led to — what source 1 reads.</summary>
            public List<RegistryRef> CustomRegistries { get; } = new();
            public List<string> Notes { get; } = new();
            public int TypesRead { get; set; }
            public bool CutByBudget { get; set; }
            public int DebugMethodsCut { get; set; }
        }

        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                                              | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        public static Result Scan(IEnumerable<Assembly> assemblies, Func<string, Type?> findType, int budgetMs = BudgetMs)
        {
            var result = new Result();
            var clock = Stopwatch.StartNew();
            var custom = new Dictionary<Type, RegistryRef?>();
            var forwards = new Dictionary<(MethodBase, int), Forward?>();
            var debugKept = 0;
            var ordered = assemblies.ToList();
            var scanned = new HashSet<Assembly>(ordered);
            var devSetters = new DevSetters.Tally();
            foreach (var asm in ordered)
            {
                foreach (var type in TypesOf(asm, result.Notes))
                {
                    if (clock.ElapsedMilliseconds > budgetMs)
                    {
                        result.CutByBudget = true;
                        break;
                    }
                    result.TypesRead++;
                    MethodInfo[] methods;
                    try { methods = type.GetMethods(Declared); }
                    catch (Exception) { continue; }
                    // a lambda's closure class: its bodies hold registrations (`list.ForEach(n => Register(n, …))`), but it
                    // is no class a person named — so only its call sites are read
                    if (IsCompilerGenerated(type))
                    {
                        foreach (var m in methods) CallSiteFindings(m, findType, custom, forwards, result);
                        continue;
                    }
                    var options = KnownConsoles.Rows.FirstOrDefault(r => r.OptionsType != null && r.OptionsType == type.FullName);
                    if (options != null) OptionsFindings(type, options, result);
                    var isDebugClass = IsDebugClassName(type.Name);
                    foreach (var m in methods)
                    {
                        AttributeFindings(type, m, findType, result);
                        CallSiteFindings(m, findType, custom, forwards, result);
                        if (isDebugClass && DebugMethods.Candidate(m))
                        {
                            if (debugKept >= MaxDebugMethods) { result.DebugMethodsCut++; continue; }
                            debugKept++;
                            result.Findings.Add(DebugMethods.Finding(type, m));
                        }
                    }
                    // Fix 4: the setters the game's own DEV UI calls (a debug-named class, or a MonoBehaviour that turns
                    // itself off under a release flag) — RL's `PredeterminedRollUI` → `GameManager.NextDiceRollResult`
                    DevSetters.Add(type, methods, scanned, devSetters, result);
                }
                if (result.CutByBudget) break;
            }
            if (devSetters.Cut > 0)
                result.Notes.Add($"{devSetters.Cut} more dev-UI setter(s) past the first {DevSetters.MaxFindings} were not listed");
            result.CustomRegistries.AddRange(custom.Values.Where(r => r != null).Select(r => r!)
                .OrderBy(r => r.Where, StringComparer.Ordinal));
            if (result.CutByBudget)
                result.Notes.Add($"the code scan stopped after {budgetMs / 1000} s having read {result.TypesRead} classes — " +
                                 "what it found is listed; the rest of the code was not read");
            if (result.DebugMethodsCut > 0)
                result.Notes.Add($"{result.DebugMethodsCut} more debug method(s) past the first {MaxDebugMethods} were not listed");
            var distinct = CheatFinding.Distinct(result.Findings);
            result.Findings.Clear();
            result.Findings.AddRange(distinct);
            return result;
        }

        internal static IEnumerable<Type> TypesOf(Assembly asm, List<string> notes)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                notes.Add($"{asm.GetName().Name}: {e.Types.Count(t => t == null)} class(es) could not be loaded and were not read");
                return e.Types.Where(t => t != null).Select(t => t!);
            }
            catch (Exception e)
            {
                notes.Add($"{asm.GetName().Name} could not be read: {e.Message}");
                return Array.Empty<Type>();
            }
        }

        private static bool IsCompilerGenerated(MemberInfo m) =>
            m.Name.IndexOf('<') >= 0
            || CustomAttributeData.GetCustomAttributes(m).Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");

        /// <summary>The word rule of source 4: a class whose name has one of these words (<c>DebugPanel</c>,
        /// <c>CheatManager</c>, <c>GMTools</c>, <c>DevMenu</c>) — never a letter run inside a word (<c>Device</c>,
        /// <c>Latest</c>, <c>Contest</c>).</summary>
        public static readonly IReadOnlyList<string> DebugClassWords = new[] { "debug", "cheat", "cheats", "dev", "test", "gm" };

        public static bool IsDebugClassName(string name) =>
            CheatFinding.WordsOf(name).Any(w => DebugClassWords.Contains(w));

        private static void AttributeFindings(Type type, MethodInfo m, Func<string, Type?> findType, Result result)
        {
            IList<CustomAttributeData> attrs;
            try { attrs = CustomAttributeData.GetCustomAttributes(m); }
            catch (Exception) { return; }
            foreach (var a in attrs)
            {
                var row = KnownConsoles.ByAttribute(a.AttributeType.FullName);
                if (row == null) continue;
                string? name = null;
                if (a.ConstructorArguments.Count > row.AttributeNameArg
                    && a.ConstructorArguments[row.AttributeNameArg].Value is string s && s.Length > 0)
                    name = s;
                else if (row.NameDefaultsToMethod) name = m.Name;
                var finding = new CheatFinding
                {
                    Source = CheatFinding.TagScanSource, Name = name, Console = row.Id,
                    Where = $"{type.FullName}.{m.Name}", Signature = m.GetParameters().Length > 0 ? SignatureOf(m) : null,
                };
                if (name == null) finding.Note = "the attribute names no command";
                else if (m.GetParameters().Length > 0)
                    finding.Note = "this command takes values — a tick is one fixed command with fixed values, so it is listed, not proposed";
                else if (row.Executor != null && ExecutorResolves(row.Executor, findType))
                {
                    finding.Command = CheatFinding.ExecutorCommand(row.Executor, name, out var why);
                    finding.Note = why;
                }
                else if (m.IsStatic)
                {
                    finding.Command = CheatFinding.CallCommand(type, m.Name, out var why);
                    finding.Note = why;
                }
                else finding.Note = "an instance method and no executor this kit knows for its console";
                if (name != null && name.Length > CheatFinding.MaxNameLength) continue;
                result.Findings.Add(finding);
            }
        }

        private static bool ExecutorResolves(string executor, Func<string, Type?> findType)
        {
            var dot = executor.LastIndexOf('.');
            var t = findType(executor.Substring(0, dot));
            return t != null && CustomConsoleRule.IsNameExecutor(t, executor.Substring(dot + 1));
        }

        private static void OptionsFindings(Type type, KnownConsoles.ConsoleRow row, Result result)
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName || IsCompilerGenerated(m)) continue;
                result.Findings.Add(new CheatFinding
                {
                    Source = CheatFinding.TagScanSource, Name = m.Name, Console = row.Id, Where = $"{type.FullName}.{m.Name}",
                    Signature = m.GetParameters().Length > 0 ? SignatureOf(m) : null,
                    Note = "an options-class method: it runs on the console's live options object, which `call` does not reach — listed, not proposed",
                });
            }
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                result.Findings.Add(new CheatFinding
                {
                    Source = CheatFinding.TagScanSource, Name = p.Name, Console = row.Id, Where = $"{type.FullName}.{p.Name}",
                    Note = "an options-class setting (a value, not an action) — listed, not proposed",
                });
        }

        private static void CallSiteFindings(MethodInfo m, Func<string, Type?> findType, Dictionary<Type, RegistryRef?> custom,
            Dictionary<(MethodBase, int), Forward?> forwards, Result result)
        {
            if (m.IsAbstract) return;
            foreach (var site in IlCallSites.Scan(m, IsRegistration))
            {
                var registryType = site.Callee.DeclaringType!;
                var registry = RegistryOf(registryType, custom);
                var known = KnownConsoles.ByType(registryType);
                // A THIN FORWARDER's own body (`RegisterAction(string name, …) => Registry.Register(name, …)`): it hands on
                // its CALLER's name — plumbing, not a cheat whose name is built at run time. Its target is still recorded
                // above (`custom`), so source 1 reads the registry it forwards to.
                if (site.FirstLiteral == null && m.DeclaringType != registryType && IsRegistration(m))
                {
                    var line = $"{m.DeclaringType?.FullName}.{m.Name} forwards its caller's name to {registryType.FullName}.{site.Callee.Name} " +
                               "— a pass-through, not a cheat; the names registered on it are followed there";
                    if (!result.Notes.Contains(line)) result.Notes.Add(line);
                    continue;
                }
                var executor = known?.Executor != null && ExecutorResolves(known.Executor, findType) ? known.Executor : registry?.Executor;
                var console = known?.Id ?? (registry != null ? "custom" : null);
                Forward? forward = null;
                if (executor == null && known == null)
                {
                    forward = ForwardOf(site.Callee, findType, custom, forwards, MaxForwardDepth);
                    if (forward?.Executor != null)
                    {
                        executor = forward.Executor;
                        console = forward.Console;
                    }
                }
                var finding = new CheatFinding
                {
                    Source = CheatFinding.TagScanSource,
                    Name = site.FirstLiteral is { Length: > 0 } lit && lit.Length <= CheatFinding.MaxNameLength ? lit : null,
                    Console = console,
                    Where = $"{m.DeclaringType?.FullName}.{m.Name} → {registryType.FullName}.{site.Callee.Name}",
                };
                if (finding.Name == null)
                    finding.Note = "the name is built at run time or held in a variable — the running game's registry (source 1) reads it";
                else if (executor != null)
                {
                    finding.Command = CheatFinding.ExecutorCommand(executor, finding.Name, out var why);
                    finding.Note = why ?? (forward?.Executor != null
                        ? $"{registryType.FullName}.{site.Callee.Name} forwards the name to {forward.Path} — proposed through {forward.Executor}"
                        : null);
                }
                else finding.Note = forward?.Why ?? "no executor found on the registering class — listed, not proposed";
                result.Findings.Add(finding);
            }
        }

        /// <summary>How many forwarders in a row are followed to find the registry (A → B → C).</summary>
        private const int MaxForwardDepth = 3;

        /// <summary>Where a registration method hands its caller's name: the console that can run it (its executor),
        /// the chain it was followed along, or — when it runs nowhere — why.</summary>
        internal sealed class Forward
        {
            public string? Console;
            public string? Executor;
            public string Path = "";
            public string? Why;
        }

        private static RegistryRef? RegistryOf(Type type, Dictionary<Type, RegistryRef?> custom)
        {
            if (!custom.TryGetValue(type, out var registry))
            {
                registry = KnownConsoles.ByType(type) != null ? null : CustomConsoleRule.Describe(type);
                custom[type] = registry;
            }
            return registry;
        }

        /// <summary>
        /// FOLLOW A THIN FORWARDER (0.13.1): <paramref name="callee"/> is a registration method whose class has no executor
        /// (<c>DevConsole.RegisterAction(name, a) => Registry.Register(name, a)</c>). Its compiled body is read — METADATA
        /// ONLY, as every call site is — for the registration calls it makes on ANOTHER class with no name literal of its
        /// own (it passes its caller's name on). Exactly one such class, and that class's executor — a known console's, or
        /// the custom-console rule's — is where the name runs; a class with none is followed again, up to
        /// <see cref="MaxForwardDepth"/>. Two target classes are never guessed between. Null: not a forwarder.
        /// NOT PROVEN by this read: that the forwarder passes the name UNCHANGED (<c>"dev:" + name</c> also resets the
        /// literal) — a proposal is a proposal: it runs nothing until it is ticked, and its proof would fail.
        /// </summary>
        private static Forward? ForwardOf(MethodBase callee, Func<string, Type?> findType, Dictionary<Type, RegistryRef?> custom,
            Dictionary<(MethodBase, int), Forward?> cache, int depth)
        {
            // keyed by (method, forwards left): an answer worked out with one forward left is not the answer with three
            // left — reusing it let a chain run past the limit, or lost one within it, by the order the sites were read
            var key = (callee, depth);
            if (cache.TryGetValue(key, out var hit)) return hit;
            cache[key] = null; // a cycle (A → B → A) reads as "not a forwarder", never a loop
            Forward? forward = null;
            if (depth > 0 && !callee.IsAbstract && callee.DeclaringType != null)
            {
                var sites = IlCallSites.Scan(callee, IsRegistration)
                    .Where(s => s.FirstLiteral == null && s.Callee.DeclaringType != null && s.Callee.DeclaringType != callee.DeclaringType)
                    .ToList();
                var targets = sites.Select(s => s.Callee.DeclaringType!).Distinct().ToList();
                var from = $"{callee.DeclaringType.FullName}.{callee.Name}";
                if (targets.Count > 1)
                    forward = new Forward
                    {
                        Why = $"{from} forwards the name to {targets.Count} classes ({string.Join(", ", targets.Select(t => t.FullName))}) — " +
                              "which one runs it is not guessed; listed, not proposed",
                    };
                else if (targets.Count == 1)
                {
                    var target = targets[0];
                    var to = sites.First(s => s.Callee.DeclaringType == target).Callee;
                    var here = $"{target.FullName}.{to.Name}";
                    var known = KnownConsoles.ByType(target);
                    var registry = RegistryOf(target, custom);
                    if (known?.Executor != null && ExecutorResolves(known.Executor, findType))
                        forward = new Forward { Console = known.Id, Executor = known.Executor, Path = here };
                    else if (registry?.Executor != null)
                        forward = new Forward { Console = "custom", Executor = registry.Executor, Path = here };
                    else
                    {
                        var next = ForwardOf(to, findType, custom, cache, depth - 1);
                        forward = next?.Executor != null
                            ? new Forward { Console = next.Console, Executor = next.Executor, Path = $"{here} → {next.Path}" }
                            : new Forward
                            {
                                Why = next?.Why ?? $"no executor found on the registering class, nor on {here}, which it forwards the name to " +
                                      "— listed, not proposed",
                            };
                    }
                }
            }
            cache[key] = forward;
            return forward;
        }

        /// <summary>A callee that registers a name: a registration verb whose first parameter is a string.</summary>
        private static bool IsRegistration(MethodBase callee)
        {
            if (callee.DeclaringType == null || !CustomConsoleRule.IsRegistrationName(callee.Name)) return false;
            try
            {
                var ps = callee.GetParameters();
                return ps.Length >= 1 && ps[0].ParameterType == typeof(string);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static string SignatureOf(MethodInfo m) =>
            "(" + string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")) + ")";
    }

    /// <summary>
    /// v3 P3 (§3.3 source 4) — DEBUG METHODS WITH NO CONSOLE, e.g. <c>CheatManager.AddCoins(int)</c>: a method a
    /// person could tick as a <c>call</c>. Listed as candidates only; proposed as a command only when it takes no value
    /// (a tick is one fixed command — "AddCoins 100" and "AddCoins 1000" are two ticks, written by the studio).
    /// </summary>
    public static class DebugMethods
    {
        /// <summary>Unity's message methods and the object plumbing every class has — never a cheat.</summary>
        private static readonly HashSet<string> NotCheats = new(StringComparer.Ordinal)
        {
            "Awake", "Start", "Update", "LateUpdate", "FixedUpdate", "OnEnable", "OnDisable", "OnDestroy", "OnGUI",
            "OnValidate", "Reset", "OnApplicationPause", "OnApplicationQuit", "OnApplicationFocus", "OnDrawGizmos",
            "OnDrawGizmosSelected", "OnBecameVisible", "OnBecameInvisible", "OnTriggerEnter", "OnTriggerExit",
            "OnCollisionEnter", "OnCollisionExit", "OnTriggerEnter2D", "OnTriggerExit2D", "OnCollisionEnter2D",
            "OnCollisionExit2D", "OnMouseDown", "OnMouseUp", "OnRectTransformDimensionsChange", "OnTransformParentChanged",
            "Equals", "GetHashCode", "ToString", "Finalize", "Dispose",
        };

        internal static readonly HashSet<Type> SimpleArgs = new()
        {
            typeof(string), typeof(bool), typeof(int), typeof(long), typeof(float), typeof(double),
        };

        public const int MaxArgs = 3;

        public static bool Candidate(MethodInfo m)
        {
            if (m.IsSpecialName || m.IsAbstract || m.IsGenericMethodDefinition || m.Name.IndexOf('<') >= 0) return false;
            if (NotCheats.Contains(m.Name)) return false;
            if (m.DeclaringType is { IsGenericTypeDefinition: true }) return false;
            ParameterInfo[] ps;
            try { ps = m.GetParameters(); }
            catch (Exception) { return false; }
            return ps.Length <= MaxArgs && ps.All(p => !p.ParameterType.IsByRef
                                                       && (SimpleArgs.Contains(p.ParameterType) || p.ParameterType.IsEnum));
        }

        public static CheatFinding Finding(Type type, MethodInfo m)
        {
            var f = new CheatFinding
            {
                Source = CheatFinding.DebugMethodSource, Name = m.Name, Where = $"{type.FullName}.{m.Name}",
            };
            if (m.GetParameters().Length > 0)
            {
                f.Signature = CheatTagScan.SignatureOf(m);
                f.Note = "this method takes values — each value the studio wants is its own fixed command, so it is listed, not proposed";
                return f;
            }
            if (!m.IsStatic && !Reachable(type))
            {
                f.Note = "an instance method on a class with no static Instance and that is not a scene component — `call` cannot reach an object of it";
                return f;
            }
            f.Command = CheatFinding.CallCommand(type, m.Name, out var why);
            f.Note = why;
            return f;
        }

        /// <summary>Would the bridge's <c>call</c> find a live object of this class (<see cref="GameReflection.InstanceOf"/>'s
        /// three ways)? Metadata only — nothing is read.</summary>
        internal static bool Reachable(Type type)
        {
            const BindingFlags s = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            return type.GetProperty("Instance", s) != null || type.GetField("Instance", s) != null
                   || typeof(UnityEngine.Component).IsAssignableFrom(type);
        }
    }

    /// <summary>
    /// Fix 4 (six-fixes plan r1, 2026-09-28) — A VALUE THE GAME'S OWN DEV UI SETS IS A CHEAT CANDIDATE. Rogue Legend's
    /// dice are directable through one settable property, <c>GameManager.NextDiceRollResult</c>, set only from
    /// <c>PredeterminedRollUI</c> — a MonoBehaviour with no debug word in its name that turns itself off in a release
    /// build (<c>if (PlatformsManager.IsRelease) { gameObject.SetActive(false); return; }</c>). No other source saw it:
    /// source 4 reads only debug-NAMED classes and drops special-name methods, so no property setter was ever read.
    ///
    /// A class is DEV UI (<see cref="DevUiWhy"/>) when its name has a debug word (<see cref="CheatTagScan.IsDebugClassName"/>),
    /// or — THE RELEASE-FLAG RULE — it is a MonoBehaviour one of whose methods reads a static bool (a property or a field)
    /// whose name holds <c>release</c>, <c>isdev</c> or <c>debug</c> (any case) AND calls <c>GameObject.SetActive(false)</c>
    /// or sets <c>Behaviour.enabled = false</c> (the <c>false</c> is read off the IL: the constant loaded right before the
    /// call, on its OWN object or GameObject — the audit's M5). In a dev UI class — its own methods and its compiler-made lambda bodies — every call site of a property
    /// SETTER is read (<see cref="IlCallSites"/>, METADATA ONLY — nothing runs), and it becomes a finding when the setter's
    /// class is one of the game's (never an engine type's <c>enabled</c> or <c>text</c>), is not the dev UI's own state,
    /// can be reached by the bridge (a static setter, or <see cref="DebugMethods.Reachable"/>), and takes one simple value
    /// (<see cref="DebugMethods.SimpleArgs"/> or an enum — what <see cref="GameReflection.TryCoerce"/> reads). The
    /// finding proposes the set-value template <c>set Type.Member {v}</c> (<see cref="CheatFinding.SetValueCommand"/>).
    ///
    /// NOT SEEN (said, not hidden): a flag the compiler inlined (a <c>const bool</c>, or <c>#if</c>); a dev UI that hides
    /// itself another way (a parent's SetActive, a CanvasGroup); a write through a field rather than a property setter.
    /// Invariant 182.
    /// </summary>
    public static class DevSetters
    {
        /// <summary>The most dev-setter findings one search keeps.</summary>
        public const int MaxFindings = 100;

        /// <summary>The words a release flag's name holds (lowercased, as a substring).</summary>
        public static readonly IReadOnlyList<string> FlagWords = new[] { "release", "isdev", "debug" };

        public sealed class Tally
        {
            public int Kept;
            public int Cut;
        }

        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                                              | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        internal static void Add(Type type, MethodInfo[] methods, ISet<Assembly> scanned, Tally tally, CheatTagScan.Result result)
        {
            var why = DevUiWhy(type, methods);
            if (why == null) return;
            foreach (var m in BodiesOf(type, methods))
                foreach (var site in IlCallSites.Scan(m, IsSetter))
                {
                    var f = FindingOf(type, why, (MethodInfo)site.Callee, scanned);
                    if (f == null) continue;
                    if (tally.Kept >= MaxFindings) { tally.Cut++; continue; }
                    tally.Kept++;
                    result.Findings.Add(f);
                }
        }

        /// <summary>Why <paramref name="type"/> is dev UI, in words for the finding's note — or null when it is not.</summary>
        public static string? DevUiWhy(Type type) => DevUiWhy(type, MethodsOf(type));

        private static string? DevUiWhy(Type type, MethodInfo[] methods)
        {
            if (CheatTagScan.IsDebugClassName(type.Name)) return "its name has a debug word";
            if (!typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(type)) return null;
            foreach (var m in methods)
            {
                if (m.IsAbstract) continue;
                string? flag = null;
                var turnsOff = false;
                var sites = IlCallSites.Scan(m, c => IsFlagGetter(c) || IsSelfOff(c),
                    field => { if (flag == null && IsFlagField(field)) flag = $"{field.DeclaringType?.Name}.{field.Name}"; });
                foreach (var s in sites)
                {
                    if (IsFlagGetter(s.Callee)) flag ??= $"{s.Callee.DeclaringType?.Name}.{s.Callee.Name.Substring(4)}";
                    // the audit's M5: only ITSELF off — `gameObject.SetActive(false)` / `enabled = false` on its own
                    // object; a HUD that hides a CHILD under `Debug.isDebugBuild` is gameplay, not a dev UI
                    else if (s.PrecedingI4 == 0
                             && (s.Callee.Name == "SetActive" ? s.ReceiverIsOwnGameObject : s.ReceiverIsThis))
                        turnsOff = true;
                }
                if (flag != null && turnsOff) return $"{m.Name} reads {flag} and turns itself off";
            }
            return null;
        }

        private static MethodInfo[] MethodsOf(Type type)
        {
            try { return type.GetMethods(Declared); }
            catch (Exception) { return Array.Empty<MethodInfo>(); }
        }

        /// <summary>The class's own methods and those of the compiler-made classes nested in it (a lambda's body:
        /// <c>slider.onValueChanged.AddListener(v => GameManager.Instance.Next = (int)v)</c>).</summary>
        private static IEnumerable<MethodInfo> BodiesOf(Type type, MethodInfo[] methods)
        {
            foreach (var m in methods) if (!m.IsAbstract) yield return m;
            Type[] nested;
            try { nested = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic); }
            catch (Exception) { yield break; }
            foreach (var n in nested)
                if (n.Name.IndexOf('<') >= 0)
                    foreach (var m in BodiesOf(n, MethodsOf(n)))
                        yield return m;
        }

        private static bool IsFlagName(string name)
        {
            var lower = name.ToLowerInvariant();
            return FlagWords.Any(w => lower.Contains(w));
        }

        private static bool IsFlagGetter(MethodBase callee) =>
            callee is MethodInfo mi && mi.IsStatic && mi.IsSpecialName && mi.Name.StartsWith("get_", StringComparison.Ordinal)
            && mi.ReturnType == typeof(bool) && IsFlagName(mi.Name.Substring(4));

        private static bool IsFlagField(FieldInfo f) => f.IsStatic && f.FieldType == typeof(bool) && IsFlagName(f.Name);

        private static bool IsSelfOff(MethodBase callee) =>
            (callee.Name == "SetActive" && callee.DeclaringType == typeof(UnityEngine.GameObject))
            || (callee.Name == "set_enabled" && callee.DeclaringType != null
                && typeof(UnityEngine.Behaviour).IsAssignableFrom(callee.DeclaringType));

        private static bool IsSetter(MethodBase callee)
        {
            if (callee is not MethodInfo mi || !mi.IsSpecialName || mi.DeclaringType == null
                || !mi.Name.StartsWith("set_", StringComparison.Ordinal)) return false;
            try { return mi.GetParameters().Length == 1; }
            catch (Exception) { return false; }
        }

        private static CheatFinding? FindingOf(Type devUi, string why, MethodInfo setter, ISet<Assembly> scanned)
        {
            var owner = setter.DeclaringType!;
            // an engine type's setter (`enabled`, `text`) is the dev UI drawing itself, never the game's switch
            if (!scanned.Contains(owner.Assembly)) return null;
            // the dev UI's own state (its base classes included) is not a value the game reads
            if (owner.IsAssignableFrom(devUi)) return null;
            if (!setter.IsStatic && !DebugMethods.Reachable(owner)) return null;
            Type valueType;
            try { valueType = setter.GetParameters()[0].ParameterType; }
            catch (Exception) { return null; }
            if (valueType.IsByRef || !(DebugMethods.SimpleArgs.Contains(valueType) || valueType.IsEnum)) return null;
            var member = setter.Name.Substring(4);
            var f = new CheatFinding
            {
                Source = CheatFinding.DevSetterSource,
                Name = $"{owner.Name}.{member}",
                Where = $"{owner.FullName}.{member}",
                Signature = $"({valueType.Name} value)",
            };
            f.Command = CheatFinding.SetValueCommand(owner, member, out var commandWhy);
            f.Note = commandWhy ?? $"set by the game's dev UI {devUi.FullName} ({why}) — it takes any {valueType.Name} value, so a tick lets any one value through (a wider grant than a fixed cheat)";
            return f;
        }
    }
}
