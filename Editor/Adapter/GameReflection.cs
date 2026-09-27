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
        /// THE NAME INDEX (the fourteenth audit, S1). Every type in every loaded assembly, by full name and by simple name,
        /// built once per domain; an assembly that loads afterwards is APPENDED to it (the fifteenth audit, S1). It replaced
        /// a per-name cache over a per-name SCAN: an uncached lookup called GetTypes() on every assembly and compared every
        /// type — 46.7 ms in the bare test host (186 assemblies, 34,517 types), and a live-service game loads more — and
        /// <see cref="TryResolveOwner"/> asks one lookup per leading part of a dotted path, each a new string, so a
        /// 255-character `get` of one-letter segments was 124 scans in one editor tick: 6.0 s. Now every lookup, a miss
        /// included, is a dictionary read. The kit calls lookups from EditorApplication.update (the state probe, the
        /// director's pump), where a slow one presents as a frozen editor with a stalled relay heartbeat. Statics die with
        /// the domain, which is the right lifetime for the index.
        ///
        /// WHEN IT IS BUILT (corrected by the fifteenth audit): not lazily by the first lookup, which is how it was, but on
        /// the editor's first <c>delayCall</c> after the domain loads (<see cref="GameReflectionWarmup"/>), outside any
        /// director pump. The old "its cost is one scan" was false: a first build in a fresh domain measured 137–422 ms in
        /// the test host (in the audits' and fold's runs, contention included), its own GetTypes() loaded 5 assemblies, each of those marked the index stale, and the next lookup
        /// — the second label a `ui-dump` read — rebuilt it (69–112 ms): 211–586 ms in the first dump's pump. Now an
        /// assembly load only QUEUES the assembly, and the next lookup (or the build itself, for the loads its own scan
        /// causes) appends that assembly's types: never a second full build. If a lookup comes before the delayCall has
        /// run, the lookup builds the index itself — correctness first — and <see cref="WarmedByForTests"/> says so.
        /// A type added to an already-indexed DYNAMIC (Reflection.Emit) assembly after it was appended is not seen, ever
        /// (on ea016964, until the next assembly load; the sixteenth audit, M1); a game's own types are compiled, never that.
        /// </summary>
        private static readonly object IndexLock = new();
        private static Dictionary<string, Type>? _byFullName;
        private static Dictionary<string, List<Type>>? _bySimpleName;
        /// <summary>Assemblies loaded since they were last looked at, in load order: appended by the next lookup.</summary>
        private static readonly List<Assembly> Pending = new();
        /// <summary>Every assembly already in the index — so an assembly both queued and in the build's own snapshot (loaded
        /// between this class's first use and the build) is indexed ONCE, and a unique name does not turn ambiguous.</summary>
        private static readonly HashSet<Assembly> Indexed = new();
        /// <summary>The choice made for an ambiguous simple name, so its warning is said once per index.</summary>
        private static readonly Dictionary<string, Type> AmbiguousChoice = new(StringComparer.Ordinal);

        static GameReflection()
        {
            // QUEUE ONLY. Never GetTypes() here: the handler also fires on the thread running a scan (Monitor is reentrant).
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) => { lock (IndexLock) Pending.Add(e.LoadedAssembly); };
        }

        /// <summary>For the tests: how long the last full index build took, in milliseconds.</summary>
        internal static double LastIndexBuildMsForTests { get; private set; }
        /// <summary>For the tests: full builds of the index in this domain (one, unless a test asked for another).</summary>
        internal static int FullBuildsForTests { get; private set; }
        /// <summary>For the tests: assemblies appended to an existing index.</summary>
        internal static int AppendsForTests { get; private set; }
        /// <summary>Who built the index first in this domain: "delayCall" (the editor, outside any pump) or "lookup".</summary>
        internal static string? WarmedByForTests { get; private set; }
        /// <summary>For the tests: run once inside the next full build's scan, after its first assembly — the way a test
        /// loads an assembly DURING a build.</summary>
        internal static Action? DuringScanForTests;

        /// <summary>Find a type by simple name or Namespace.Name across every loaded assembly.</summary>
        public static Type? FindType(string name)
        {
            lock (IndexLock)
            {
                EnsureIndex("lookup");
                return Resolve(name, _byFullName!, _bySimpleName!);
            }
        }

        /// <summary>Build the index now, outside any director pump — the editor's delayCall after a domain load.</summary>
        internal static void WarmForEditor()
        {
            lock (IndexLock) EnsureIndex("delayCall");
        }

        /// <summary>For the tests: throw the index away and build it again now, as a fresh domain would.</summary>
        internal static void RebuildForTests()
        {
            lock (IndexLock)
            {
                _byFullName = null;
                _bySimpleName = null;
                EnsureIndex("lookup");
            }
        }

        private static void EnsureIndex(string by)
        {
            if (_byFullName == null || _bySimpleName == null)
            {
                WarmedByForTests ??= by;
                BuildIndex();
            }
            AppendPending();
        }

        /// <summary>One pass over every type of every loaded assembly, in <see cref="AppDomain.GetAssemblies"/> order and
        /// each assembly's own type order — the order the per-name scan it replaced read them in, so the first match it
        /// would have found is the first one kept here. The assemblies its own scan loads are queued meanwhile, and
        /// appended right after it, in the same call.</summary>
        private static void BuildIndex()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            FullBuildsForTests++;
            _byFullName = new Dictionary<string, Type>(StringComparer.Ordinal);
            _bySimpleName = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
            Indexed.Clear();
            AmbiguousChoice.Clear();
            var hook = DuringScanForTests;
            DuringScanForTests = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Add(asm);
                if (hook != null) { hook(); hook = null; }
            }
            AppendPending();
            LastIndexBuildMsForTests = watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>Append every queued assembly not yet indexed, in load order — and the ones those appends load.</summary>
        private static void AppendPending()
        {
            while (Pending.Count > 0)
            {
                var batch = Pending.ToArray();
                Pending.Clear();
                foreach (var asm in batch)
                    if (Add(asm)) AppendsForTests++;
            }
        }

        /// <summary>One assembly's types into the index: a full name keeps the FIRST type that had it (load order), a
        /// simple name's list grows at its end, and a cached ambiguous choice for any name this assembly defines is
        /// dropped, since its candidates just changed — the Assembly-CSharp preference is judged again. False when the
        /// assembly was already indexed.</summary>
        private static bool Add(Assembly asm)
        {
            if (!Indexed.Add(asm)) return false;
            Type[] types;
            // A partially-loadable assembly still yields the types that DID load; without this
            // one bad third-party assembly aborts the whole scan.
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
            catch { return true; }

            var byFullName = _byFullName!;
            var bySimpleName = _bySimpleName!;
            foreach (var t in types)
            {
                if (t.FullName is { } full && !byFullName.ContainsKey(full)) byFullName[full] = t;
                if (!bySimpleName.TryGetValue(t.Name, out var list)) bySimpleName[t.Name] = list = new List<Type>(1);
                list.Add(t);
                AmbiguousChoice.Remove(t.Name);
            }
            return true;
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
        private static Type? Resolve(string name, Dictionary<string, Type> byFullName, Dictionary<string, List<Type>> bySimpleName)
        {
            if (byFullName.TryGetValue(name, out var exact))
                return exact;
            if (!bySimpleName.TryGetValue(name, out var byName) || byName.Count == 0)
                return null;
            if (byName.Count == 1)
                return byName[0];
            if (AmbiguousChoice.TryGetValue(name, out var already))
                return already;

            var fromGame = byName.FirstOrDefault(t => t.Assembly.GetName().Name == "Assembly-CSharp");
            var chosen = fromGame ?? byName[0];
            Debug.LogWarning($"[RecorderKit] '{name}' is ambiguous across {byName.Count} assemblies; chose " +
                             $"{chosen.FullName} ({chosen.Assembly.GetName().Name}). Candidates: " +
                             $"{string.Join(", ", byName.Select(t => $"{t.FullName} [{t.Assembly.GetName().Name}]"))}. " +
                             "Pass a fully-qualified name to pick another.");
            AmbiguousChoice[name] = chosen;
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
            var field = type.GetField(member, ANY_STATIC) ?? type.GetField(member, ANY_INSTANCE) ?? FieldInBaseChain(type, member);
            if (field != null)
            {
                if (!field.IsStatic && target == null)
                    return false;
                value = field.GetValue(field.IsStatic ? null : target);
                return true;
            }
            return false;
        }

        /// <summary>A field declared on a BASE class, private ones included — `GetField` on the derived type does not see a
        /// base's private field (kit 0.13.2: a snapshot names them, so a `get` must read them back). Most-derived first.</summary>
        internal static FieldInfo? FieldInBaseChain(Type type, string member)
        {
            for (var t = type.BaseType; t != null && t != typeof(object); t = t.BaseType)
            {
                var f = t.GetField(member, ANY_INSTANCE | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>
        /// THE OWNER A PATH'S FIRST MEMBER IS READ FROM (the fresh audit of 0.13.2, M1). A STATIC member needs no instance,
        /// so none is looked for — reading `Type.Data` never touches `Type.Instance`. A non-static one reads from an
        /// instance that ALREADY EXISTS (<see cref="ExistingInstanceOf"/>), never from a getter that might make one.
        /// </summary>
        private static object? OwnerFor(Type type, string firstMember)
        {
            var prop = type.GetProperty(firstMember, ANY_STATIC);
            if (prop?.GetGetMethod(true)?.IsStatic == true) return null;
            if (type.GetField(firstMember, ANY_STATIC) != null) return null;
            return ExistingInstanceOf(type);
        }

        /// <summary>Conventional static fields a singleton keeps its instance in (read as FIELDS: no getter runs).</summary>
        private static readonly string[] InstanceFieldNames =
            { "Instance", "instance", "_instance", "_Instance", "s_instance", "s_Instance", "sInstance", "m_Instance", "<Instance>k__BackingField" };

        /// <summary>
        /// A LIVE INSTANCE OF A TYPE THAT ALREADY EXISTS — found without running any of the game's code: a static FIELD of
        /// this type (or of a base, a generic singleton base included) under a conventional name, the compiler-made backing
        /// field of a static auto-property <c>Instance</c>, then an existing Component in the scene
        /// (<c>FindFirstObjectByType</c>, which finds and never creates). A static <c>Instance</c> property with a body is
        /// NOT called: a lazy singleton's getter creates the object (the fourteenth audit's `DeliveredGetLazySingleton`).
        /// Null when none exists. (Reading a static field can run the type's static constructor once, as any first use does.)
        /// </summary>
        public static object? ExistingInstanceOf(Type type)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var name in InstanceFieldNames)
                {
                    var f = t.GetField(name, ANY_STATIC | BindingFlags.DeclaredOnly);
                    if (f == null || !type.IsAssignableFrom(f.FieldType) && !f.FieldType.IsAssignableFrom(type)) continue;
                    var v = f.GetValue(null);
                    if (v != null && type.IsInstanceOfType(v) && !(v is UnityEngine.Object uo && uo == null)) return v;
                }
            if (typeof(Component).IsAssignableFrom(type))
            {
                var found = UnityEngine.Object.FindFirstObjectByType(type);
                return found == null ? null : found;
            }
            return null;
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

                var current = OwnerFor(type, segments[take]);
                var currentType = type;
                for (var i = take; i < segments.Length - 1; i++)
                {
                    if (!TryReadStep(current, currentType, segments[i], out var next, out var refusal))
                    {
                        error = refusal != null ? $"{refusal} (while walking '{path}')" : current == null && i == take
                            ? $"{currentType.FullName}.{segments[i]} is an instance member, and no live instance of {currentType.Name} exists (the kit finds one in a static Instance field or the scene; it never calls an Instance getter, which could create one) while walking '{path}'"
                            : $"{currentType.FullName} has no readable member '{segments[i]}' while walking '{path}'";
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

        /// <summary>
        /// Read the value a path points at — a dotted path (<c>Type.member.member</c>), and since kit 0.13.2 the
        /// ADDRESSING a snapshot proposes (<see cref="DataPath"/>): <c>[key]</c> after a member reads one entry of a
        /// dictionary (the key coerced to the dictionary's key type) or one item of a list or array (a whole-number
        /// index), and <c>.Count</c> reads any collection's count, an array's included. A path with no <c>[</c> resolves
        /// exactly as it always did (<see cref="TryResolveOwner"/>), plus that <c>.Count</c>.
        /// </summary>
        public static bool TryGetPath(string path, out object? value, out string error)
        {
            value = null;
            if (path.IndexOf('[') >= 0) return TryGetIndexedPath(path, out value, out error);
            if (!TryResolveOwner(path, out var owner, out var ownerType, out var member, out error))
                return false;
            if (!TryReadStep(owner, ownerType!, member, out value, out var refusal))
            {
                error = refusal ?? $"{ownerType!.FullName} has no readable member '{member}' " +
                        "(or it is an instance member with no live instance)";
                return false;
            }
            return true;
        }

        /// <summary>One member read, plus <c>Count</c> of any collection that has no member of that name (an array: its
        /// <c>Count</c> is an explicit interface member) — so every <c>X.Count</c> a snapshot proposes reads back. A STATIC
        /// property never runs its body (<see cref="TryReadStaticProperty"/>); a runtime collection that wraps a game one
        /// is never counted (<see cref="SnapshotRead.WrapsGameCollection"/>).</summary>
        private static bool TryReadStep(object? target, Type type, string member, out object? value, out string? refusal)
        {
            refusal = null;
            if (TryReadStaticProperty(type, member, out value, out refusal)) return true;
            if (refusal != null) return false;
            if (target != null && SnapshotRead.WrapsGameCollection(target))
            {
                refusal = $"{type.Name} wraps a collection of the game's own code — the kit never counts or opens it";
                return false;
            }
            if (TryReadMember(target, type, member, out value)) return true;
            if (member == "Count" && target is System.Collections.ICollection col) { value = col.Count; return true; }
            return false;
        }

        /// <summary>
        /// A STATIC PROPERTY ON A PATH, READ WITHOUT ITS BODY (the second fresh check of 0.13.2, HIGH). An auto-property is
        /// read through its compiler-made backing field. A property with a BODY — a lazy singleton's
        /// <c>Instance =&gt; _i ??= new …</c>, a computed <c>X =&gt; Instance.Get…()</c> — is never run: it resolves to an
        /// instance of its type that ALREADY EXISTS (<see cref="ExistingInstanceOf"/>), or it is refused by name. False with
        /// a null <paramref name="refusal"/> = not a static property (read it as before).
        /// </summary>
        internal static bool TryReadStaticProperty(Type type, string member, out object? value, out string? refusal)
        {
            value = null;
            refusal = null;
            var prop = type.GetProperty(member, ANY_STATIC);
            if (prop == null || prop.GetGetMethod(true)?.IsStatic != true) return false;
            for (var t = prop.DeclaringType; t != null && t != typeof(object); t = t.BaseType)
            {
                var backing = t.GetField($"<{member}>k__BackingField", ANY_STATIC | BindingFlags.DeclaredOnly);
                if (backing != null) { value = backing.GetValue(null); return true; }
            }
            var existing = ExistingInstanceOf(prop.PropertyType);
            if (existing != null) { value = existing; return true; }
            refusal = $"{type.FullName}.{member} is a static property with a body, and the kit never runs one (a lazy " +
                      $"singleton's getter builds the object); no existing {prop.PropertyType.Name} was found in a static field " +
                      "or the scene — is the game running and past its start?";
            return false;
        }

        /// <summary>A path holding <c>[key]</c>: the type is found among the leading names before the first bracket (the
        /// longest that names a type, as <see cref="TryResolveOwner"/> does), then every step is walked — a member, or an
        /// entry by key / an item by index.</summary>
        private static bool TryGetIndexedPath(string path, out object? value, out string error)
        {
            value = null;
            if (!DataPath.TryParse(path, out var steps, out error)) return false;
            var leading = 0;
            while (leading < steps.Count && !steps[leading].IsIndex) leading++;
            for (var take = Math.Min(leading, steps.Count - 1); take >= 1; take--)
            {
                var type = FindType(string.Join(".", steps.Take(take).Select(s => s.Text)));
                if (type == null) continue;
                object? current = steps[take].IsIndex ? ExistingInstanceOf(type) : OwnerFor(type, steps[take].Text);
                var currentType = type;
                var walked = type.FullName ?? type.Name;
                for (var i = take; i < steps.Count; i++)
                {
                    var step = steps[i];
                    object? next;
                    if (step.IsIndex)
                    {
                        if (!TryIndex(current, step.Text, out next, out var why))
                        {
                            error = $"{walked}[{step.Text}]: {why}";
                            return false;
                        }
                    }
                    else if (!TryReadStep(current, currentType, step.Text, out next, out var refusal))
                    {
                        error = refusal != null
                            ? $"{refusal} (while walking '{path}')"
                            : $"{currentType.FullName} has no readable member '{step.Text}' while walking '{path}'";
                        return false;
                    }
                    walked += step.IsIndex ? $"[{step.Text}]" : "." + step.Text;
                    if (i == steps.Count - 1) { value = next; return true; }
                    if (next == null)
                    {
                        error = $"{walked} is null while walking '{path}' — is the game in the right state?";
                        return false;
                    }
                    current = next;
                    currentType = next.GetType();
                }
            }
            error = $"no leading part of '{path}' names a loaded type";
            return false;
        }

        /// <summary>One entry of a dictionary by its key (coerced to the key type: a string, a number, an enum's name), or
        /// one item of a list or array by a whole-number index. Never throws.</summary>
        internal static bool TryIndex(object? container, string key, out object? value, out string why)
        {
            value = null;
            why = "";
            try
            {
                switch (container)
                {
                    case null:
                        why = "is null";
                        return false;
                    case System.Collections.ICollection wrapper when SnapshotRead.WrapsGameCollection(wrapper):
                        why = $"is a {container.GetType().Name} over a collection of the game's own code — the kit never opens it";
                        return false;
                    case System.Collections.IDictionary d:
                    {
                        // FOUND BY ENUMERATION, never by the dictionary's own lookup: `Contains` / the indexer run its key
                        // comparer, which may be the game's code. A hashed key (DataKeys) is matched by hashing each key
                        // here, on this machine — one keyed hasher for the whole scan, bounded in entries and in time.
                        var hashed = DataKeys.IsHashed(key);
                        using var hasher = hashed ? DataKeys.NewHasher() : null;
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        var seen = 0;
                        foreach (System.Collections.DictionaryEntry e in d)
                        {
                            if (++seen > DataKeys.MaxHashedScan || clock.ElapsedMilliseconds > DataKeys.LookupBudgetMs)
                            {
                                why = $"there is no key matching {key} among the first {seen - 1} entries read (the lookup stops at " +
                                      $"{DataKeys.MaxHashedScan} entries or {DataKeys.LookupBudgetMs} ms; it holds {d.Count})";
                                return false;
                            }
                            var raw = DataKeys.RawText(e.Key);
                            if (raw == null) continue;
                            if (hashed ? hasher!.Hash(raw) == key : raw == key) { value = e.Value; return true; }
                        }
                        why = hashed ? $"there is no key matching {key} (it holds {d.Count} entries)" : $"there is no key '{key}' (it holds {d.Count} entries)";
                        return false;
                    }
                    case System.Collections.IList l:
                        if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var ix))
                        {
                            why = $"a list is read by a whole-number index, not '{key}'";
                            return false;
                        }
                        if (ix >= l.Count)
                        {
                            why = $"index {ix} is past the end (it holds {l.Count} items)";
                            return false;
                        }
                        value = l[ix];
                        return true;
                    default:
                        why = $"is a {container.GetType().Name}, not a dictionary, list or array";
                        return false;
                }
            }
            catch (Exception e)
            {
                why = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        /// <summary>The key type of a (generic) dictionary type, or null.</summary>
        internal static Type? DictionaryKeyType(Type t)
        {
            foreach (var i in new[] { t }.Concat(t.GetInterfaces()))
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                    return i.GetGenericArguments()[0];
            return null;
        }

        /// <summary>Write the value a dotted path points at, coercing from the raw string. A path with <c>[key]</c> is
        /// read-only: <c>set</c> writes members, never through an entry or an index.</summary>
        public static bool TrySetPath(string path, string raw, out string error)
        {
            if (path.IndexOf('[') >= 0)
            {
                error = $"'{path}' reads an entry or an item ([key]) — `set` writes a member by a dotted path only";
                return false;
            }
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

    /// <summary>
    /// Kit 0.13.2 — THE DATA PATH GRAMMAR a <c>get</c> reads and a snapshot proposes, one rule on both sides (the
    /// website's <c>CHECK_PATH_RE</c> in <c>apps/renderer/src/game-ads/learn/tick-list.ts</c>, held together by
    /// <c>Editor/Tests/Fixtures/tick-list.cases.json</c>):
    /// <code>
    ///   path  := name "." name ( "." name | "[" key "]" )*
    ///   name  := [A-Za-z_][A-Za-z0-9_]*
    ///   key   := [A-Za-z0-9_-]{1,64}
    /// </code>
    /// <c>FirebaseSystem.UserData.Currencies.Currencies[topaz].Amount</c> (a dictionary entry by key),
    /// <c>Save.Player.Levels[3].DidClear</c> (a list item by index), <c>Save.Player.Levels.Count</c>. The key is written
    /// BARE, never quoted: the bridge splits a command at its quotes (<c>ReflectionCheatBridge.SplitCommand</c>), so a
    /// quoted key would reach <c>get</c> as three words. A key holding anything else (a space, a dot, a quote) cannot be
    /// addressed, and a snapshot does not propose it (it counts it as unaddressable). At most
    /// <see cref="CheatCheck.MaxPathLength"/> characters.
    /// </summary>
    /// <summary>
    /// Kit 0.13.2 (the fresh audit, M6) — DICTIONARY KEYS THAT MUST NOT LEAVE THE MACHINE IN CLEAR. A snapshot sends paths,
    /// and a path holds a dictionary's key; a key may be an identifier (another player's uid, a device id). So a key that
    /// LOOKS like one, or that the grammar cannot write, is sent as <c>#</c> + the first 12 hex digits of
    /// HMAC-SHA256(this project's salt, key). The salt is 32 random bytes made once per project at
    /// <c>Library/AdRelay/snapshot-key</c> and never sent; a <c>get</c> of a hashed path resolves it by hashing the
    /// dictionary's keys here (<see cref="GameReflection.TryIndex"/>), so a proposal stays a check the kit can run.
    ///
    /// LOOKS LIKE AN IDENTIFIER: 16 or more hex digits and dashes (a uuid, a hex id); 16 or more characters of
    /// letters, digits, <c>+/=_-</c> holding an upper-case letter, a lower-case letter and a digit (a base64-ish uid);
    /// or a whole number of 7 or more digits. Ordinary ids (<c>topaz</c>, <c>w2</c>, <c>gold_bar</c>) stay readable. An
    /// enum key is its name.
    /// </summary>
    public static class DataKeys
    {
        public const int HashHexLength = 12;
        public const int MaxHashedScan = 100_000;
        /// <summary>For the tests: the project whose salt is used (else the open project).</summary>
        internal static string? ProjectRootForTests;
        private static readonly object SaltLock = new();
        private static byte[]? _salt;
        private static string? _saltPath;

        private static readonly System.Text.RegularExpressions.Regex HashedRe = new("^#[0-9a-f]{12}\\z");
        private static readonly System.Text.RegularExpressions.Regex HexId = new("^[0-9a-fA-F-]{16,}\\z");
        private static readonly System.Text.RegularExpressions.Regex Base64ish = new("^[A-Za-z0-9+/=_-]{16,}\\z");
        private static readonly System.Text.RegularExpressions.Regex LongNumber = new("^[0-9]{7,}\\z");

        public static bool IsHashed(string? key) => key != null && HashedRe.IsMatch(key);

        /// <summary>Null, or why this session's salt is not the project's stored one (a salt file that cannot be read is
        /// never replaced: a salt kept in memory for this session is used instead, and a proof says so).</summary>
        public static string? SaltNote { get; private set; }

        /// <summary>For the tests: forget the cached salt, as a domain reload would.</summary>
        internal static void ForgetSaltForTests()
        {
            lock (SaltLock) { _salt = null; _saltPath = null; SaltNote = null; }
        }

        /// <summary>A key's own text (a string, an enum's name, a whole number), or null for any other key type.</summary>
        public static string? RawText(object? key) => key switch
        {
            string s => s,
            Enum e => e.ToString(),
            byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToString(key, CultureInfo.InvariantCulture),
            _ => null,
        };

        public static bool LooksLikeAnIdentifier(string text) =>
            HexId.IsMatch(text) || LongNumber.IsMatch(text)
            || (Base64ish.IsMatch(text) && text.Any(char.IsUpper) && text.Any(char.IsLower) && text.Any(char.IsDigit));

        /// <summary>A key as a SENT path writes it: the key itself, or its hash (see the class), or null for a key type the
        /// grammar has no form for. Pass a <see cref="Hasher"/> when hashing many keys (one keyed HMAC for all of them).</summary>
        public static string? PathKey(object? key, Hasher? hasher = null)
        {
            var text = RawText(key);
            if (text == null) return null;
            if (key is Enum && DataPath.IsKey(text)) return text;
            if (DataPath.IsKey(text) && !LooksLikeAnIdentifier(text)) return text;
            return hasher != null ? hasher.Hash(text) : Hash(text);
        }

        /// <summary>The most milliseconds one hashed-key lookup may scan a dictionary for (the second fresh check, M3).</summary>
        public const int LookupBudgetMs = 100;

        /// <summary>One keyed HMAC over the cached salt, for many keys in one read. Dispose it.</summary>
        public sealed class Hasher : IDisposable
        {
            private readonly System.Security.Cryptography.HMACSHA256 _h;
            internal Hasher(byte[] salt) => _h = new System.Security.Cryptography.HMACSHA256(salt);
            public string Hash(string keyText)
            {
                var bytes = _h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(keyText));
                return "#" + string.Concat(bytes.Take(HashHexLength / 2).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
            public void Dispose() => _h.Dispose();
        }

        public static Hasher NewHasher() => new(Salt());

        public static string Hash(string keyText)
        {
            using var h = NewHasher();
            return h.Hash(keyText);
        }

        /// <summary>Where the salt lives: ALWAYS <c>&lt;project&gt;/Library/AdRelay/snapshot-key</c> — under <c>Library/</c>,
        /// which Unity projects never commit, never the legacy project-root <c>AdRelay/</c> a pre-0.3 session may use.</summary>
        public static string SaltPath(string projectRoot) => System.IO.Path.Combine(projectRoot, "Library", "AdRelay", "snapshot-key");

        /// <summary>
        /// THE SALT, cached per project for the domain (no file is touched on a cached call). Made once: 32 random bytes,
        /// written with the kit's <see cref="AtomicFile"/> (never delete-then-move). A salt file that EXISTS but cannot be
        /// read is never replaced — a salt kept in memory for this session is used, and <see cref="SaltNote"/> says so (a
        /// proof reports it). The salt is per machine: a hashed check made on one editor does not match on another's.
        /// </summary>
        private static byte[] Salt()
        {
            lock (SaltLock)
            {
                var path = SaltPath(ProjectRootForTests ?? KitProject.Root());
                if (_salt != null && _saltPath == path) return _salt;
                byte[]? salt = null;
                var exists = false;
                try
                {
                    exists = System.IO.File.Exists(path);
                    if (exists)
                    {
                        var text = System.IO.File.ReadAllText(path).Trim();
                        if (text.Length == 64 && text.All(Uri.IsHexDigit))
                            salt = Enumerable.Range(0, 32).Select(i => Convert.ToByte(text.Substring(i * 2, 2), 16)).ToArray();
                    }
                }
                catch (Exception) { salt = null; }
                if (salt == null)
                {
                    salt = new byte[32];
                    using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(salt);
                    if (exists)
                        SaltNote = $"the key-hashing salt {path} could not be read, so it was left as it is and a salt for this " +
                                   "editor session was used — hashed keys ([#…]) from this proof will not match after a restart";
                    else
                        try { AtomicFile.Write(path, string.Concat(salt.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)))); }
                        catch (Exception) { SaltNote = $"the key-hashing salt {path} could not be written — a salt for this editor session was used"; }
                }
                _salt = salt;
                _saltPath = path;
                return salt;
            }
        }
    }

    public static class DataPath
    {
        public const int MaxKeyLength = 64;
        /// <summary>A key is written bare (1–64 of <c>[A-Za-z0-9_-]</c>) or, for a key the snapshot must not send in clear,
        /// as its salted short hash <c>#</c> + 12 hex digits (<see cref="DataKeys"/>).</summary>
        public static readonly System.Text.RegularExpressions.Regex Re = new(
            "^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)+(\\.[A-Za-z_][A-Za-z0-9_]*|\\[(?:[A-Za-z0-9_-]{1,64}|#[0-9a-f]{12})\\])*\\z");
        private static readonly System.Text.RegularExpressions.Regex KeyRe = new("^[A-Za-z0-9_-]{1,64}\\z");
        private static readonly System.Text.RegularExpressions.Regex NameRe = new("^[A-Za-z_][A-Za-z0-9_]*\\z");

        public static bool IsPath(string? path) => path != null && path.Length <= CheatCheck.MaxPathLength && Re.IsMatch(path);
        public static bool IsKey(string? key) => key != null && KeyRe.IsMatch(key);
        public static bool IsName(string? name) => name != null && NameRe.IsMatch(name);

        public readonly struct Step
        {
            public Step(string text, bool isIndex) { Text = text; IsIndex = isIndex; }
            public string Text { get; }
            public bool IsIndex { get; }
        }

        public static bool TryParse(string path, out List<Step> steps, out string error)
        {
            steps = new List<Step>();
            error = "";
            if (!Re.IsMatch(path))
            {
                error = $"'{path}' is not a data path (Type.member, then .member or [key] — a key of letters, digits, '_' or '-')";
                return false;
            }
            var i = 0;
            while (i < path.Length)
            {
                if (path[i] == '.') { i++; continue; }
                if (path[i] == '[')
                {
                    var end = path.IndexOf(']', i);
                    steps.Add(new Step(path.Substring(i + 1, end - i - 1), true));
                    i = end + 1;
                    continue;
                }
                var start = i;
                while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
                steps.Add(new Step(path.Substring(start, i - start), false));
            }
            return true;
        }
    }

    /// <summary>
    /// Builds <see cref="GameReflection"/>'s name index once per domain OUTSIDE any director pump (the fifteenth audit,
    /// S1): on the editor's first delayCall after a load or a domain reload. 137–422 ms once in the test host (in the
    /// audits' and fold's runs, contention included), next to a reload that takes seconds; so a delivered `ui-dump` never
    /// pays it. NOT IN BATCH MODE since v3 P2 (v6.1 §1 B: the kit is inert in a studio's headless builds and CI); the kit's
    /// own batch-mode suite warms the index where a test needs it warm.
    /// </summary>
    [UnityEditor.InitializeOnLoad]
    internal static class GameReflectionWarmup
    {
        /// <summary>Whether the warm-up was scheduled — false in batch mode (v6.1 §1 B: the kit is inert there).</summary>
        internal static bool Booted { get; private set; }

        static GameReflectionWarmup()
        {
            if (UnityEngine.Application.isBatchMode) return;
            Booted = true;
            UnityEditor.EditorApplication.delayCall += GameReflection.WarmForEditor;
        }
    }
}
