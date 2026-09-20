using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Game-agnostic reflection access to a studio's own types.
    ///
    /// WHY THIS IS IN THE KIT rather than re-authored per game: a game that keeps its code in the
    /// predefined Assembly-CSharp (no .asmdef over its scripts — the common case) cannot be
    /// referenced at compile time by an .asmdef assembly, and the adapter must be an .asmdef to see
    /// the kit at all. So reflection is not a style choice, it is the only bridge — and every game
    /// needs the same hardened core. The alternative is ~600 lines re-derived per onboarding, with
    /// each of the traps below rediscovered the hard way.
    ///
    /// Every rule encoded here was learned live, and each comment says which failure it prevents.
    /// </summary>
    public static class GameReflection
    {
        private const BindingFlags ANY_STATIC = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags ANY_INSTANCE = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>
        /// Cache for FindType, negative hits included — a discovery session repeatedly asks for
        /// types that do not exist. Load-bearing for responsiveness: an uncached lookup calls
        /// GetTypes() on EVERY loaded assembly, which on a live-service game is the whole game plus
        /// Firebase, Photon, Addressables and the ad SDKs. The kit calls the state probe from
        /// EditorApplication.update, so a slow lookup blocks the editor's update loop and presents
        /// as a frozen editor with a stalled relay heartbeat. Statics die with the domain, which is
        /// exactly the right cache lifetime.
        /// </summary>
        private static readonly Dictionary<string, Type?> TypeCache = new();

        /// <summary>Find a type by simple name or Namespace.Name across every loaded assembly.</summary>
        public static Type? FindType(string name)
        {
            if (TypeCache.TryGetValue(name, out var cached))
                return cached;
            var found = FindTypeUncached(name);
            TypeCache[name] = found;
            return found;
        }

        /// <summary>
        /// Resolution ORDER matters, and getting it wrong is silent. Taking the first simple-name
        /// match in assembly load order resolved "PlayerData" to a third-party
        /// ParserTestNamespace.PlayerData — a TEST type with none of the game's members — while the
        /// game's own PlayerData sat in the global namespace. So:
        ///   1. exact FullName anywhere (a global-namespace type's FullName IS its bare name);
        ///   2. else simple-name matches, preferring the game's own Assembly-CSharp;
        ///   3. still ambiguous → take the first but NAME every candidate, so the operator can
        ///      disambiguate with a fully-qualified name instead of debugging a wrong-type dump.
        /// </summary>
        private static Type? FindTypeUncached(string name)
        {
            var byName = new List<Type>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                // A partially-loadable assembly still yields the types that DID load; without this
                // one bad third-party assembly aborts the whole scan.
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t.FullName == name)
                        return t;
                    if (t.Name == name)
                        byName.Add(t);
                }
            }

            if (byName.Count == 0)
                return null;
            if (byName.Count == 1)
                return byName[0];

            var fromGame = byName.FirstOrDefault(t => t.Assembly.GetName().Name == "Assembly-CSharp");
            var chosen = fromGame ?? byName[0];
            Debug.LogWarning($"[RecorderKit] '{name}' is ambiguous across {byName.Count} assemblies; chose " +
                             $"{chosen.FullName} ({chosen.Assembly.GetName().Name}). Candidates: " +
                             $"{string.Join(", ", byName.Select(t => $"{t.FullName} [{t.Assembly.GetName().Name}]"))}. " +
                             "Pass a fully-qualified name to pick another.");
            return chosen;
        }

        /// <summary>
        /// The live instance of a type. Three strategies IN ORDER: a static Instance property, a
        /// static Instance field, then FindFirstObjectByType for a Component. A type with none of
        /// those is static-only and correctly yields null.
        /// </summary>
        public static object? InstanceOf(Type type)
        {
            var prop = type.GetProperty("Instance", ANY_STATIC);
            if (prop != null)
                return prop.GetValue(null);
            var field = type.GetField("Instance", ANY_STATIC);
            if (field != null)
                return field.GetValue(null);
            if (typeof(Component).IsAssignableFrom(type))
                return UnityEngine.Object.FindFirstObjectByType(type);
            return null;
        }

        /// <summary>
        /// Read one member off a resolved target (static when target is null).
        ///
        /// Returns false rather than throwing when the member is non-static and no instance was
        /// resolved. Reflection's own error there is "Non-static method requires a target", thrown
        /// from whichever kit seam called you — which in Edit Mode (no managers in the scene) is
        /// EVERY probe-state. Before kit 0.2.0 that escaped the relay pump and wedged it forever.
        /// </summary>
        public static bool TryReadMember(object? target, Type type, string member, out object? value)
        {
            value = null;
            var prop = type.GetProperty(member, ANY_STATIC) ?? type.GetProperty(member, ANY_INSTANCE);
            if (prop != null && prop.CanRead)
            {
                var isStatic = prop.GetGetMethod(true)?.IsStatic == true;
                if (!isStatic && target == null)
                    return false;
                value = prop.GetValue(isStatic ? null : target);
                return true;
            }
            var field = type.GetField(member, ANY_STATIC) ?? type.GetField(member, ANY_INSTANCE);
            if (field != null)
            {
                if (!field.IsStatic && target == null)
                    return false;
                value = field.GetValue(field.IsStatic ? null : target);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Resolve a dotted path to (owning object, owning type, final member name).
        ///
        /// Chaining is REQUIRED, not a nicety: the values that matter are rarely one hop from a
        /// singleton. One game's master onboarding gate was
        /// `FirebaseSystem.UserData.User.MatchesPlayed` — three hops, ending on a plain field of a
        /// plain model class reachable no other way.
        ///
        /// Greedy longest-prefix: the longest leading run of segments that names a Type wins, then
        /// the remainder walks as members. Handles a bare global-namespace type and a namespaced
        /// one without the caller saying which it is.
        /// </summary>
        public static bool TryResolveOwner(string path, out object? owner, out Type? ownerType,
            out string lastMember, out string error)
        {
            owner = null;
            ownerType = null;
            lastMember = "";
            error = "";

            var segments = path.Split('.');
            if (segments.Length < 2)
            {
                error = $"expected <Type>.<Member>[.<Member>...], got '{path}'";
                return false;
            }

            for (var take = segments.Length - 1; take >= 1; take--)
            {
                var type = FindType(string.Join(".", segments.Take(take)));
                if (type == null)
                    continue;

                var current = InstanceOf(type);
                var currentType = type;
                for (var i = take; i < segments.Length - 1; i++)
                {
                    if (!TryReadMember(current, currentType, segments[i], out var next))
                    {
                        error = $"{currentType.FullName} has no readable member '{segments[i]}' while walking '{path}'";
                        return false;
                    }
                    if (next == null)
                    {
                        error = $"{currentType.FullName}.{segments[i]} is null while walking '{path}' — " +
                                "is the game in the right state?";
                        return false;
                    }
                    current = next;
                    currentType = next.GetType();
                }

                owner = current;
                ownerType = currentType;
                lastMember = segments[^1];
                return true;
            }

            error = $"no leading part of '{path}' names a loaded type";
            return false;
        }

        /// <summary>Read the value a dotted path points at.</summary>
        public static bool TryGetPath(string path, out object? value, out string error)
        {
            value = null;
            if (!TryResolveOwner(path, out var owner, out var ownerType, out var member, out error))
                return false;
            if (!TryReadMember(owner, ownerType!, member, out value))
            {
                error = $"{ownerType!.FullName} has no readable member '{member}' " +
                        "(or it is an instance member with no live instance)";
                return false;
            }
            return true;
        }

        /// <summary>Write the value a dotted path points at, coercing from the raw string.</summary>
        public static bool TrySetPath(string path, string raw, out string error)
        {
            if (!TryResolveOwner(path, out var owner, out var ownerType, out var member, out error))
                return false;
            return TrySetOn(owner, ownerType!, member, raw, out error);
        }

        /// <summary>
        /// Write a property/field, coercing the raw string to the member's type.
        ///
        /// Ship this alongside get/call: a game's best director lever is often a SETTABLE PROPERTY
        /// (one game's whole board loop is directable through a single settable NextDiceRollResult),
        /// and a bridge that cannot write a field is half a bridge.
        /// </summary>
        public static bool TrySetOn(object? target, Type type, string member, string raw, out string error)
        {
            error = "";

            var prop = type.GetProperty(member, ANY_STATIC) ?? type.GetProperty(member, ANY_INSTANCE);
            if (prop != null && prop.CanWrite)
            {
                var isStatic = prop.GetSetMethod(true)?.IsStatic == true;
                if (!isStatic && target == null) { error = $"{type.Name} has no live instance"; return false; }
                if (!TryCoerce(raw, prop.PropertyType, out var v))
                {
                    error = $"cannot read '{raw}' as {prop.PropertyType.Name}";
                    return false;
                }
                prop.SetValue(isStatic ? null : target, v);
                return true;
            }
            var field = type.GetField(member, ANY_STATIC) ?? type.GetField(member, ANY_INSTANCE);
            if (field != null && !field.IsLiteral && !field.IsInitOnly)
            {
                if (!field.IsStatic && target == null) { error = $"{type.Name} has no live instance"; return false; }
                if (!TryCoerce(raw, field.FieldType, out var v))
                {
                    error = $"cannot read '{raw}' as {field.FieldType.Name}";
                    return false;
                }
                field.SetValue(field.IsStatic ? null : target, v);
                return true;
            }
            error = $"{type.Name} has no writable member '{member}'";
            return false;
        }

        /// <summary>
        /// Invoke a method by name, coercing string args to the parameter types. Overloads are
        /// matched on argument COUNT then on whether every argument coerces, so
        /// `EndBattle true 12345 900` finds EndBattle(bool, int, long) without naming types.
        /// </summary>
        public static bool TryCall(Type type, string method, string[] args, out object? result, out string error)
        {
            result = null;
            error = "";
            var target = InstanceOf(type);

            var candidates = type.GetMethods(ANY_STATIC).Concat(type.GetMethods(ANY_INSTANCE))
                .Where(m => m.Name == method && m.GetParameters().Length == args.Length)
                .ToList();
            if (candidates.Count == 0)
            {
                error = $"no overload of {type.Name}.{method} takes {args.Length} arg(s)";
                return false;
            }

            foreach (var m in candidates)
            {
                var ps = m.GetParameters();
                var coerced = new object?[ps.Length];
                var ok = true;
                for (var i = 0; i < ps.Length; i++)
                {
                    // `out` is write-only to the callee. The dummy "_" we pass for
                    // TryExecuteCommand(string, out string) is a string, so it only
                    // coerced on String&. out int / out bool / enum& used to die
                    // with "no overload accepted those argument values" — the same
                    // error this ByRef unwrap exists to remove. Skip coerce; put a
                    // default in the slot; Invoke writes the real value back.
                    if (ps[i].IsOut)
                    {
                        var el = ps[i].ParameterType.GetElementType();
                        if (el == null) { ok = false; break; }
                        coerced[i] = el.IsValueType ? Activator.CreateInstance(el) : null;
                        continue;
                    }
                    if (!TryCoerce(args[i], ps[i].ParameterType, out coerced[i])) { ok = false; break; }
                }
                if (!ok)
                    continue;

                if (!m.IsStatic && target == null)
                {
                    error = $"{type.Name}.{method} is an instance method but no live instance was found " +
                            "(is the game in Play Mode and on the right screen?)";
                    return false;
                }
                try
                {
                    result = m.Invoke(m.IsStatic ? null : target, coerced);
                    // out/ref slots are written back into `coerced`. A door like
                    // TryExecuteCommand(string, out string) returns bool; the useful
                    // payload is the out response. Surface those so `call` is not
                    // "True" with the answer stranded in an unseen array slot.
                    var outBits = new List<string>();
                    for (var i = 0; i < ps.Length; i++)
                    {
                        if (ps[i].ParameterType.IsByRef)
                            outBits.Add($"{ps[i].Name}={Render(coerced[i])}");
                    }
                    if (outBits.Count > 0)
                        result = $"{Render(result)} | {string.Join(" | ", outBits)}";
                    return true;
                }
                catch (TargetInvocationException e)
                {
                    // Surface the GAME's exception, not the reflection wrapper — a cheat throwing
                    // inside game code is a real finding and the wrapper hides it.
                    error = $"{type.Name}.{method} threw: {e.InnerException?.Message ?? e.Message}";
                    return false;
                }
            }
            error = $"{type.Name}.{method}: no overload accepted those argument values";
            return false;
        }

        /// <summary>
        /// Invoke a GENERIC method with a runtime type argument, e.g. a board's GetAllTiles&lt;T&gt;().
        /// Generic methods are unreachable from a string command (nowhere to put T), so named cheats
        /// needing one come through here.
        /// </summary>
        public static bool TryCallGeneric(Type type, string method, Type[] genericArgs, object?[] args,
            out object? result, out string error)
        {
            result = null;
            error = "";
            var open = type.GetMethods(ANY_STATIC).Concat(type.GetMethods(ANY_INSTANCE))
                .FirstOrDefault(m => m.Name == method && m.IsGenericMethodDefinition
                                     && m.GetGenericArguments().Length == genericArgs.Length
                                     && m.GetParameters().Length == args.Length);
            if (open == null)
            {
                error = $"no generic {type.Name}.{method} with {genericArgs.Length} type arg(s) and " +
                        $"{args.Length} parameter(s)";
                return false;
            }
            var target = open.IsStatic ? null : InstanceOf(type);
            if (!open.IsStatic && target == null) { error = $"{type.Name} has no live instance"; return false; }
            try
            {
                result = open.MakeGenericMethod(genericArgs).Invoke(target, args);
                return true;
            }
            catch (TargetInvocationException e)
            {
                error = $"{type.Name}.{method} threw: {e.InnerException?.Message ?? e.Message}";
                return false;
            }
        }

        /// <summary>Invoke with already-constructed arguments (no string coercion).</summary>
        public static bool TryCallRaw(Type type, string method, object? target, object?[] args,
            out object? result, out string error)
        {
            result = null;
            error = "";
            var m = type.GetMethods(ANY_STATIC).Concat(type.GetMethods(ANY_INSTANCE))
                .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
            if (m == null)
            {
                error = $"no {type.Name}.{method} taking {args.Length} arg(s)";
                return false;
            }
            try
            {
                result = m.Invoke(m.IsStatic ? null : target ?? InstanceOf(type), args);
                return true;
            }
            catch (TargetInvocationException e)
            {
                error = $"{type.Name}.{method} threw: {e.InnerException?.Message ?? e.Message}";
                return false;
            }
        }

        public static bool TryCoerce(string raw, Type want, out object? value)
        {
            value = null;
            try
            {
                // `out`/`ref` parameters arrive as String& / Int32&. Coerce the
                // element type and leave a boxed value in the invoke array; Invoke
                // writes the out value back into that slot.
                if (want.IsByRef)
                {
                    var element = want.GetElementType();
                    if (element == null) return false;
                    want = element;
                }

                if (want == typeof(string)) { value = raw; return true; }

                // Nullable<T> takes the same text as T; null/empty means "no value".
                var inner = Nullable.GetUnderlyingType(want) ?? want;
                if (inner != want && (raw.Length == 0 ||
                                      raw.Equals("null", StringComparison.OrdinalIgnoreCase)))
                    return true; // value stays null

                if (inner.IsEnum) { value = Enum.Parse(inner, raw, ignoreCase: true); return true; }

                // Convert.ChangeType covers EVERY numeric width in one line — uint, ulong, short,
                // ushort, byte, sbyte, decimal, char — where the old explicit ladder covered only
                // int/long/float/double and silently failed on the rest. Live consequence: a public
                // editor API taking (uint, uint, string) was unreachable through `call`, reporting
                // only "no overload accepted those argument values".
                //
                // InvariantCulture is load-bearing rather than tidiness: the previous float.Parse used
                // the CURRENT culture, so on a comma-decimal machine "1.5" would parse as 15 — a wrong
                // value that succeeds, which is worse than a failure.
                if (typeof(IConvertible).IsAssignableFrom(inner))
                {
                    value = Convert.ChangeType(raw, inner, CultureInfo.InvariantCulture);
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Property + method signatures of a type — the method-API dump onboarding requires.
        ///
        /// GAME-DECLARED members only: everything inherited from MonoBehaviour/Component/Object is
        /// filtered out, or a manager's dump arrives as ~40 lines of GetComponentInChildren
        /// overloads wrapped around the three methods that matter, defeating the point.
        /// </summary>
        public static IEnumerable<string> DescribeApi(Type type)
        {
            static bool IsUnityBase(Type? t) =>
                t == typeof(MonoBehaviour) || t == typeof(Behaviour) || t == typeof(Component) ||
                t == typeof(UnityEngine.Object) || t == typeof(object);

            yield return $"TYPE {type.FullName}  [{type.Assembly.GetName().Name}]";
            yield return $"live instance: {(InstanceOf(type) == null ? "none" : "yes")}";
            yield return "";
            // FIELDS ARE NOT OPTIONAL. Games expose plenty of plain public fields, and one game's
            // entire master onboarding gate was a public FIELD (UserModel.MatchesPlayed) — a dump
            // that showed only properties and methods would have hidden the best lever in the game.
            yield return "-- fields (game-declared) --";
            foreach (var f in type.GetFields(ANY_STATIC).Concat(type.GetFields(ANY_INSTANCE))
                         .Where(f => !IsUnityBase(f.DeclaringType)).OrderBy(f => f.Name))
                yield return $"{(f.IsStatic ? "static " : "")}{Short(f.FieldType)} {f.Name}" +
                             (f.IsLiteral || f.IsInitOnly ? "  (read-only)" : "");
            yield return "";
            yield return "-- properties (game-declared) --";
            foreach (var p in type.GetProperties(ANY_STATIC).Concat(type.GetProperties(ANY_INSTANCE))
                         .Where(p => !IsUnityBase(p.DeclaringType)).OrderBy(p => p.Name))
                yield return $"{(p.GetGetMethod(true)?.IsStatic == true ? "static " : "")}{Short(p.PropertyType)} {p.Name}";
            yield return "";
            yield return "-- methods (game-declared) --";
            foreach (var m in type.GetMethods(ANY_STATIC).Concat(type.GetMethods(ANY_INSTANCE))
                         .Where(m => !m.IsSpecialName && !IsUnityBase(m.DeclaringType)).OrderBy(m => m.Name))
            {
                var ps = string.Join(", ", m.GetParameters().Select(p => $"{Short(p.ParameterType)} {p.Name}"));
                yield return $"{(m.IsStatic ? "static " : "")}{Short(m.ReturnType)} {m.Name}({ps})";
            }
        }

        private static string Short(Type t) => t.IsGenericType
            ? $"{t.Name.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(Short))}>"
            : t.Name;

        /// <summary>
        /// A short identifying description of a domain object.
        ///
        /// Domain types rarely override ToString(), so a content collection renders as
        /// "HeroData, HeroData, HeroData" — a successful read carrying no information. Surfacing the
        /// first id-ish member instead turned exactly that call into all seven hero ids in one round
        /// trip. Enumerating content ids is a universal need when authoring cheats.
        /// </summary>
        public static string DescribeObject(object? v)
        {
            if (v == null)
                return "null";
            var t = v.GetType();
            if (t.IsPrimitive || v is string || t.IsEnum)
                return v.ToString() ?? "null";
            foreach (var name in new[] { "Id", "id", "ID", "Key", "key", "Name", "name" })
                if (TryReadMember(v, t, name, out var idValue) && idValue != null)
                    return $"{t.Name}({name}={idValue})";
            var s = v.ToString();
            return string.IsNullOrEmpty(s) ? t.Name : s!;
        }

        /// <summary>Render a value or collection for operator-facing output.</summary>
        public static string Render(object? v, int max = 30)
        {
            if (v == null)
                return "null";
            if (v is System.Collections.IEnumerable e and not string)
            {
                var items = e.Cast<object?>().ToList();
                return $"[{items.Count}] {string.Join(", ", items.Take(max).Select(x => DescribeObject(x)))}"
                       + (items.Count > max ? " …" : "");
            }
            return DescribeObject(v);
        }

        /// <summary>
        /// Every live singleton-ish manager — the auto-discovered cheat surface. A MonoBehaviour that
        /// exposes a static Instance member or survives scene loads. One scan found 140 on a real game.
        /// </summary>
        public static IEnumerable<string> LiveSingletons()
        {
            var seen = new SortedSet<string>();
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb == null)
                    continue;
                var t = mb.GetType();
                var hasInstance = t.GetProperty("Instance", ANY_STATIC) != null
                                  || t.GetField("Instance", ANY_STATIC) != null;
                var persistent = (mb.gameObject.hideFlags & HideFlags.DontSave) != 0
                                 || mb.gameObject.scene.name == "DontDestroyOnLoad";
                if (hasInstance || persistent)
                    seen.Add($"{t.Name}{(hasInstance ? " [static Instance]" : "")}{(persistent ? " [persistent]" : "")}");
            }
            return seen;
        }
    }
}
