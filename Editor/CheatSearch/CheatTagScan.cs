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
            var debugKept = 0;
            foreach (var asm in assemblies)
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
                        foreach (var m in methods) CallSiteFindings(m, findType, custom, result);
                        continue;
                    }
                    var options = KnownConsoles.Rows.FirstOrDefault(r => r.OptionsType != null && r.OptionsType == type.FullName);
                    if (options != null) OptionsFindings(type, options, result);
                    var isDebugClass = IsDebugClassName(type.Name);
                    foreach (var m in methods)
                    {
                        AttributeFindings(type, m, findType, result);
                        CallSiteFindings(m, findType, custom, result);
                        if (isDebugClass && DebugMethods.Candidate(m))
                        {
                            if (debugKept >= MaxDebugMethods) { result.DebugMethodsCut++; continue; }
                            debugKept++;
                            result.Findings.Add(DebugMethods.Finding(type, m));
                        }
                    }
                }
                if (result.CutByBudget) break;
            }
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

        private static void CallSiteFindings(MethodInfo m, Func<string, Type?> findType, Dictionary<Type, RegistryRef?> custom, Result result)
        {
            if (m.IsAbstract) return;
            foreach (var site in IlCallSites.Scan(m, IsRegistration))
            {
                var registryType = site.Callee.DeclaringType!;
                if (!custom.TryGetValue(registryType, out var registry))
                {
                    registry = KnownConsoles.ByType(registryType) != null ? null : CustomConsoleRule.Describe(registryType);
                    custom[registryType] = registry;
                }
                var known = KnownConsoles.ByType(registryType);
                var executor = known?.Executor != null && ExecutorResolves(known.Executor, findType) ? known.Executor : registry?.Executor;
                var finding = new CheatFinding
                {
                    Source = CheatFinding.TagScanSource,
                    Name = site.FirstLiteral is { Length: > 0 } lit && lit.Length <= CheatFinding.MaxNameLength ? lit : null,
                    Console = known?.Id ?? (registry != null ? "custom" : null),
                    Where = $"{m.DeclaringType?.FullName}.{m.Name} → {registryType.FullName}.{site.Callee.Name}",
                };
                if (finding.Name == null)
                    finding.Note = "the name is built at run time or held in a variable — the running game's registry (source 1) reads it";
                else if (executor != null)
                {
                    finding.Command = CheatFinding.ExecutorCommand(executor, finding.Name, out var why);
                    finding.Note = why;
                }
                else finding.Note = "no executor found on the registering class — listed, not proposed";
                result.Findings.Add(finding);
            }
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

        private static readonly HashSet<Type> SimpleArgs = new()
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
}
