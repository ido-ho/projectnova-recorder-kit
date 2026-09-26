using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// A ready-made <see cref="ICheatBridge"/> carrying every DISCOVERY command, so a newly-onboarded
    /// game inherits the whole toolkit and writes only its own named cheats.
    ///
    /// Why discovery belongs in the cheat bridge at all: <c>Run(string)</c> is the ONLY channel the
    /// relay opens into a live editor. With these commands the entire method-API dump happens over
    /// the relay against a running game; without them, every question costs a file edit and a domain
    /// reload. Proven live on a real game, then promoted here — the alternative is ~600 lines
    /// re-derived per onboarding with each trap rediscovered the hard way.
    ///
    /// Built-in commands:
    ///   singletons                     live singleton-ish managers — the auto-discovered cheat surface
    ///   dump &lt;Type&gt;                     game-declared property + method signatures
    ///   get &lt;Type&gt;.&lt;a&gt;[.&lt;b&gt;…]         read a value (chained paths supported)
    ///   set &lt;Type&gt;.&lt;a&gt;[.&lt;b&gt;…] &lt;value&gt;  write a value, coerced to the member's type
    ///   call &lt;Type&gt;.&lt;Method&gt; [args…]    invoke, coercing args to the parameter types
    ///   ui-dump                        every active interactable + custom-component candidates
    ///   click &lt;name&gt; [index]           click through the kit's IUiDriver
    ///   invoke-button &lt;name&gt; [index]   fire Button.onClick directly, bypassing pointer routing
    ///   raycast-at &lt;x&gt; &lt;y&gt;             report every GameObject a REAL tap at this screen point would
    ///                                   hit, in hit order — the one check that goes through Unity's
    ///                                   actual input pipeline instead of reflection
    ///   tap-at &lt;x&gt; &lt;y&gt;                 click whatever raycast-at's TOP hit is, via IPointerClickHandler
    ///                                   — immune to duplicate-name ambiguity, unlike invoke-button
    ///   input-probe [&lt;x&gt; &lt;y&gt;]          which input world this screen (or point) lives in, ending in
    ///                                   a tier of ugui/backend/seam/unreachable. Run BEFORE authoring.
    ///   press-at / release-at &lt;x&gt; &lt;y&gt;   virtual pointer for `backend`-tier content the EventSystem
    ///   tap-through &lt;x&gt; &lt;y&gt;             cannot reach. QUEUED, not performed: the gesture plays out one
    ///   drag &lt;x1&gt; &lt;y1&gt; &lt;x2&gt; &lt;y2&gt; [ms]  event per editor tick, so wait on its EFFECT, not on the verb
    ///   content-scan                    every declared ContentSources type: every resolved id
    ///   help                           list all commands, including the game's own
    ///
    /// Subclasses add named cheats by overriding <see cref="RunGameCheat"/> — needed whenever a lever
    /// takes a CONSTRUCTED argument, since no string command can build one (a generic method's type
    /// parameter, an IList of domain models). Expect a handful per game and budget for it.
    ///
    /// Every command writes its outcome to <c>Library/AdRelay/probe/</c>, INCLUDING failures: the filesystem
    /// is the only channel back, so a warning-only failure reaches the operator as an unexplained
    /// <c>cheatOk: false</c>.
    /// </summary>
    public abstract class ReflectionCheatBridge : ICheatBridge
    {
        private static readonly string[] BuiltIns =
            { "singletons", "dump", "get", "set", "call", "ui-dump", "click", "invoke-button",
              "raycast-at", "tap-at", "input-probe", "press-at", "release-at", "tap-through", "drag",
              "hide-ui", "show-ui", "content-scan", "camera-pose", "camera-release", "help", "raw" };

        /// <summary>
        /// The `backend`-tier pointer. Created once and lazily: it resolves its types on first use,
        /// so constructing it here costs nothing in a game that never uses it.
        /// </summary>
        private readonly IPointerInjector _pointer = new PreferredPointerInjector(
            new InputSystemPointerInjector(), new UguiPointerInjector());

        private readonly string _probeDir;
        private readonly string _projectRoot;
        protected readonly IUiDriver Ui;

        protected ReflectionCheatBridge(string projectRoot, IUiDriver ui)
        {
            _projectRoot = projectRoot;
            _probeDir = Path.Combine(RelayPaths.Root(projectRoot), "probe");
            Ui = ui;
        }

        /// <summary>Named cheats this game adds. Return false for an unrecognised verb.</summary>
        protected abstract bool RunGameCheat(string verb, string[] args);

        /// <summary>
        /// Set by <see cref="Report"/> and <see cref="Fail"/>. Distinguishes "the game HANDLED this verb
        /// and the answer was false" from "the game does not know this verb" — which
        /// <see cref="RunGameCheat"/>'s bool cannot express on its own.
        /// </summary>
        private bool _reported;

        /// <summary>
        /// Guard a verb's argument count, explaining the shortfall instead of returning a bare false.
        ///
        /// The old `args.Length >= 2 && Set(...)` form produced `cheatOk: false` with NOTHING in the
        /// probe directory — the precise symptom Fail() was written to eliminate. `set` with one
        /// argument was indistinguishable from `set` against a type that does not exist.
        /// </summary>
        protected bool Need(string verb, string[] args, int count, string usage) =>
            args.Length >= count
            || Fail(verb, $"'{verb}' needs {count} argument(s), got {args.Length}. Usage: {verb} {usage}");

        /// <summary>
        /// Dispatch to the game's cheats, reporting "unknown command" ONLY when the game never answered.
        ///
        /// The bug this fixes: the old code was
        /// `RunGameCheat(verb, args) || Fail("unknown", UnknownVerb(verb))`, so any game cheat that
        /// deliberately returns FALSE had "unknown command" written over its real explanation —
        /// `Fail` writes both `probe/last.txt` and its own file, so the operator read
        /// "unknown command 'auto-resolve'" for a verb that exists, ran correctly, and was reporting
        /// that nothing was open. That is exactly the failure the ladder's "read the command's OWN
        /// response" rule warns about, produced by the tool meant to give that response.
        ///
        /// A false return with no Report/Fail behind it is still treated as unknown, which is the
        /// documented contract for an unrecognised verb.
        /// </summary>
        private bool GameCheat(string verb, string[] args)
        {
            var ok = RunGameCheat(verb, args);
            if (ok || _reported) return ok;
            return Fail("unknown", UnknownVerb(verb));
        }

        /// <summary>Verbs <see cref="RunGameCheat"/> handles, for `help`. Override to document them.</summary>
        protected virtual IEnumerable<string> GameCheatHelp() => Array.Empty<string>();

        public bool Run(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return false;
            var parts = SplitCommand(command);
            // `""` is one empty token (quoted-empty still emits — `set T.Name ""` needs that).
            // Length==0 is quotes-only with nothing emitted; empty verb is the same parse fail.
            if (parts.Length == 0 || string.IsNullOrEmpty(parts[0]))
                return Fail("parse", "empty command after split (quoted-empty or quotes-only)");
            // Match built-ins case-insensitively, but hand the game its verb with the ORIGINAL
            // casing. A passthrough cheat surface parses the string itself, and a case-sensitive
            // parser would silently reject "rolltargettype" for "RollTargetType". Caught live: SNL's
            // panel happens to be case-insensitive, so this would have stayed hidden until it broke
            // on a game whose parser is not.
            var verb = parts[0];
            var lower = verb.ToLowerInvariant();
            var args = parts.Skip(1).ToArray();
            _reported = false;

            try
            {
                switch (lower)
                {
                    case "singletons": return Report("singletons", GameReflection.LiveSingletons());
                    case "dump":       return Need(lower, args, 1, "<Type>") && Dump(args[0]);
                    case "get":        return Need(lower, args, 1, "<Type>.<member>…") && Get(args[0]);
                    case "set":        return Need(lower, args, 2, "<Type>.<member>… <value>") && Set(args[0], args[1]);
                    case "call":       return Need(lower, args, 1, "<Type>.<Method> [args…]")
                                              && Call(args[0], args.Skip(1).ToArray());
                    case "ui-dump":    return Report("ui-dump", UiCandidates());
                    case "content-scan": return Report("content-scan",
                                              ContentScan.Report(AdapterRegistry.Current?.ContentSources
                                                                 ?? System.Array.Empty<string>()));
                    case "click":      return Need(lower, args, 1, "<name>[@Label] [index]")
                                              && Targeted(args, Click);
                    case "invoke-button": return Need(lower, args, 1, "<name>[@Label] [index]")
                                              && Targeted(args, InvokeButton);
                    case "raycast-at": return Need(lower, args, 2, "<screenX> <screenY>")
                                              && RaycastAt(args[0], args[1]);
                    case "tap-at":     return Need(lower, args, 2, "<screenX> <screenY>")
                                              && TapAt(args[0], args[1]);
                    // READ-ONLY, and the point is optional: with no args it reports the SCENE's input
                    // capability, which is still the useful half of the answer. So unlike raycast-at it
                    // never fails on missing or unparseable args — it degrades to the scene-level report.
                    case "input-probe":
                        return Report("input-probe", args.Length >= 2
                            && float.TryParse(args[0], out var ipx)
                            && float.TryParse(args[1], out var ipy)
                            ? InputProbe.Report(ipx, ipy)
                            : InputProbe.Report(null, null));
                    // The `backend`-tier verbs. Each QUEUES its events and returns; the gesture plays
                    // out one event per editor tick, so wait on its EFFECT, never on the verb.
                    case "press-at":   return Need(lower, args, 2, "<screenX> <screenY>")
                                              && Pointer(lower, args, p => _pointer.Press(p));
                    case "release-at": return Need(lower, args, 2, "<screenX> <screenY>")
                                              && Pointer(lower, args, p => _pointer.Release(p));
                    case "tap-through": return Need(lower, args, 2, "<screenX> <screenY>")
                                              && Pointer(lower, args,
                                                     p => _pointer.Press(p) && _pointer.Release(p));
                    case "drag":       return Need(lower, args, 4,
                                                   "<x1> <y1> <x2> <y2> [durationMs=250]")
                                              && Drag(args);
                    case "hide-ui":    return Need(lower, args, 1, "<name>[@Label] [index]")
                                              && Targeted(args, HideUi);
                    case "show-ui":    return ShowUi();
                    case "camera-pose": return Need(lower, args, 7, "[Type] x y z pitch yaw roll fov")
                                                && CameraVerb("camera-pose", CameraPose.Pose(args, CameraPose.Host.ForProject(_projectRoot)));
                    case "camera-release": return CameraVerb("camera-release",
                                                CameraPose.Release(CameraPose.Host.ForProject(_projectRoot)));
                    case "help":       return Report("help", HelpLines());
                    // Escape hatch for verb SHADOWING: a game whose own cheat is named `get`, `set`,
                    // `dump` … would otherwise be permanently unreachable, because the built-in wins.
                    // `raw <command>` always goes straight to the game's cheats, unmodified. Matters
                    // most when the game cheat surface is a passthrough to its own console.
                    case "raw":        return Need(lower, args, 1, "<command> [args…]")
                                              && GameCheat(args[0], args.Skip(1).ToArray());
                    default:           return GameCheat(verb, args);
                }
            }
            catch (Exception e)
            {
                return Fail(verb, $"{e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// Split a UI-targeting argument list into (selector, index) and hand it to <paramref name="act"/>.
        ///
        /// The command line is split on spaces, so a NAME CONTAINING A SPACE has to be reassembled — and
        /// Unity object names contain spaces constantly. Found live: `hide-ui Settings Button` searched
        /// for "Settings" and reported "no active UI object", because the old inline parsing took args[0]
        /// as the whole name. `click` and `invoke-button` carried the identical defect, silently: they
        /// would have clicked an object called "Settings" if one existed, and otherwise blamed the game.
        ///
        /// One rule covers every case, and it is unambiguous because an index is always an integer:
        ///   · any arg contains '@'  → the whole remainder is a Name@Label selector, index 0
        ///   · the LAST arg parses as an int → that is the index, the rest joined is the name
        ///   · otherwise → the whole remainder is the name, index 0
        /// So `click Button`, `click Button 2` and `click Button@Level 1` all behave exactly as before,
        /// and `click Settings Button` now targets what it says.
        /// </summary>
        internal static bool Targeted(string[] args, Func<string, int, bool> act)
        {
            var (selector, index) = ParseTarget(args);
            return act(selector, index);
        }

        /// <summary>
        /// Split a cheat line on whitespace, keeping double-quoted spans as one token.
        /// Needed for <c>call Type.Method "SetEnergy 25" _</c> — without quotes the
        /// out-param door looks like a 3-arg overload that does not exist.
        /// Unquoted input is unchanged, so <c>hide-ui Settings Button</c> still
        /// reassembles via <see cref="ParseTarget"/>.
        /// </summary>
        internal static string[] SplitCommand(string command)
        {
            var parts = new List<string>();
            var cur = new System.Text.StringBuilder();
            var inQuotes = false;
            foreach (var c in command)
            {
                if (c == '"')
                {
                    if (inQuotes)
                    {
                        // Closing quote always emits, including "". `set T.Name ""`
                        // is two args; dropping empty made it look like a missing arg.
                        parts.Add(cur.ToString());
                        cur.Clear();
                    }
                    inQuotes = !inQuotes;
                    continue;
                }
                if (!inQuotes && char.IsWhiteSpace(c))
                {
                    if (cur.Length > 0)
                    {
                        parts.Add(cur.ToString());
                        cur.Clear();
                    }
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0) parts.Add(cur.ToString());
            return parts.ToArray();
        }

        /// <summary>Pure half of <see cref="Targeted"/>, so the parsing rule is unit-testable without a scene.</summary>
        internal static (string Selector, int Index) ParseTarget(string[] args)
        {
            if (args.Any(a => a.Contains("@")))
                return (string.Join(" ", args), 0);
            if (args.Length >= 2 && int.TryParse(args[args.Length - 1], out var ix))
                return (string.Join(" ", args.Take(args.Length - 1)), ix);
            return (string.Join(" ", args), 0);
        }

        private IEnumerable<string> HelpLines() =>
            BuiltIns.Select(b => $"(built-in) {b}").Concat(GameCheatHelp().Select(g => $"(game)     {g}"));

        private string UnknownVerb(string verb) =>
            $"unknown command '{verb}'. Built-ins: {string.Join(" | ", BuiltIns)}. " +
            "Run `help` for this game's own cheats too; `raw <command>` bypasses the built-ins if one " +
            "shadows a game cheat of the same name.";

        private bool Dump(string typeName)
        {
            var t = GameReflection.FindType(typeName);
            return t == null
                ? Fail($"dump-{typeName}", NoSuchType(typeName))
                : Report($"dump-{typeName}", GameReflection.DescribeApi(t));
        }

        private bool Get(string path)
        {
            if (!GameReflection.TryGetPath(path, out var value, out var error))
                return Fail($"get-{path}", error);
            return Report($"get-{path}", new[] { $"{path} = {GameReflection.Render(value)}" });
        }

        private bool Set(string path, string raw)
        {
            if (!GameReflection.TrySetPath(path, raw, out var error))
                return Fail($"set-{path}", error);
            return Report($"set-{path}", new[] { $"{path} := {raw}" });
        }

        private bool Call(string path, string[] args)
        {
            var dot = path.LastIndexOf('.');
            if (dot <= 0 || dot == path.Length - 1)
                return Fail("call", $"expected <Type>.<Method>, got '{path}'");
            var typeName = path.Substring(0, dot);
            var method = path.Substring(dot + 1);

            var t = GameReflection.FindType(typeName);
            if (t == null)
                return Fail($"call-{path}", NoSuchType(typeName));
            if (!GameReflection.TryCall(t, method, args, out var result, out var error))
                return Fail($"call-{path}", error);
            // Report, not just log: a call whose RETURN VALUE is the point (a getter method — the
            // read half of before/after cheat proof) is useless if it only reaches the console.
            return Report($"call-{path}",
                new[] { $"{typeName}.{method}({string.Join(", ", args)}) -> {GameReflection.Render(result)}" });
        }

        /// <summary>
        /// A simple type name can match several types across loaded assemblies, and the lookup takes
        /// the first — which may have none of the members you expected. Name the escape hatch.
        /// </summary>
        private static string NoSuchType(string typeName) =>
            $"no loaded type named '{typeName}'. If the name is right it may be ambiguous across " +
            "assemblies — pass the fully-qualified Namespace.Type. Use `singletons` for live manager names.";

        private bool Click(string name, int index)
        {
            if (!Ui.Exists(name))
                return Fail($"click-{name}", $"no active UI object named '{name}' — run `ui-dump` to see " +
                                             "what is actually on screen");
            if (!Ui.Click(name, index))
                return Fail($"click-{name}", $"'{name}' exists but the click did not land (index {index}) — " +
                                             "it may be non-interactable or covered by a blocker");
            return Report($"click-{name}", new[] { $"clicked {name}[{index}]" });
        }

        /// <summary>
        /// Fire <c>Button.onClick.Invoke()</c> on the named object directly, bypassing pointer-event
        /// routing entirely.
        ///
        /// Why this exists next to <c>click</c>. The synthetic-pointer path can silently do nothing on
        /// a button that LOOKS ordinary: measured live on Rogue Legend's lobby, every floating widget
        /// (GameModes_Widget, BossChallenge_Widget, Quest_Widget) reported a successful click and
        /// opened nothing, while plain tabs on the same screen worked — their handling is registered
        /// in code somewhere the ExecuteEvents walk does not reach (an input-blocking FTUE overlay,
        /// a custom input manager, a parent swallowing the event). onClick.Invoke() is the listener
        /// list itself, so if ANY code path is subscribed, this reaches it.
        ///
        /// The honesty trade-off, stated plainly: this is NOT a click. It skips interactability,
        /// blockers, and everything else the player experiences, so a shot that uses it to get PAST a
        /// screen is fine, but footage of the tap itself still needs the real click path. It refuses
        /// non-interactable buttons anyway — driving a button the player could not press is how a
        /// capture drifts away from the truth — and reports the persistent-listener count so an empty
        /// onClick (a code-wired widget listening elsewhere) is a visible finding, not a shrug.
        /// </summary>
        private bool InvokeButton(string name, int index)
        {
            var matches = UguiDriver.MatchesInClickOrder(name);
            if (index < 0 || index >= matches.Count)
                return Fail($"invoke-button-{name}",
                    matches.Count == 0
                        ? $"no active UI object named '{name}' — run `ui-dump` to see what is actually on screen"
                        : $"'{name}' has {matches.Count} match(es), index {index} is out of range");

            var target = matches[index];
            // Search the object THEN its children: widgets routinely carry the visual name on a
            // container and the Button on a child. Never parents — invoking an ancestor's button
            // because the child had none silently fires the wrong thing.
            var button = target.GetComponent<UnityEngine.UI.Button>()
                         ?? target.GetComponentInChildren<UnityEngine.UI.Button>();
            if (button == null)
                return Fail($"invoke-button-{name}",
                    $"'{name}' has no Button component on itself or its children — `click` (pointer " +
                    "events) is the only lever for non-Button handlers");
            if (!button.IsInteractable())
                return Fail($"invoke-button-{name}",
                    $"'{name}' Button is not interactable — invoking it would drive UI the player " +
                    "cannot press");

            button.onClick.Invoke();
            return Report($"invoke-button-{name}", new[]
            {
                $"invoked {name}[{index}] Button.onClick " +
                $"(on '{button.gameObject.name}', {button.onClick.GetPersistentEventCount()} persistent " +
                "listener(s) — runtime listeners are uncountable, so 0 here does not mean none fired)",
            });
        }

        /// <summary>
        /// Runs a REAL Unity UI raycast at a screen point — the exact query
        /// EventSystem/StandaloneInputModule performs for an actual pointer click — and reports every
        /// GameObject it hit, in hit order (nearest/topmost first).
        ///
        /// WHY THIS VERB HAS TO EXIST NEXT TO `ui-dump`/`invoke-button`. Neither of those goes anywhere
        /// near Unity's actual input pipeline: `invoke-button` calls a Button's `onClick` DIRECTLY via
        /// reflection, and `ui-dump` enumerates known component types by walking the scene graph. So
        /// both can report "clean" (a target button exists, is enabled, nothing else found) while a
        /// REAL tap in the Game View still cannot land on anything — proven live on Rogue Legend,
        /// 2026-08-05: every reflection check kept reporting the Lobby as clean while the operator
        /// reported nothing was clickable at all. The root cause was an invisible full-screen FTUE
        /// raycast blocker plus a backlog of stacked popups — neither is a Button, so neither shows up
        /// in a component-type walk, and this is the one check that asks the question a real tap asks.
        ///
        /// Three distinct answers, each diagnostic of a different failure:
        ///   EventSystem.current == null   → NOTHING in the scene can receive input, full stop. Most
        ///                                    likely cause: a scene/domain reload silently left no live
        ///                                    EventSystem. Explains a GLOBAL "nothing is clickable".
        ///   results.Count == 0            → no GraphicRaycaster covers this point at all (a Canvas
        ///                                    with no raycaster, or this point genuinely has no UI under
        ///                                    it).
        ///   results non-empty             → read the TOP hit's name. If it is the button you expected,
        ///                                    input is fine and the problem is elsewhere (OS/Editor
        ///                                    window focus is the next thing to check). If it is
        ///                                    something else — an invisible full-screen Image with
        ///                                    raycastTarget=true and NO Button component would show up
        ///                                    here and NOWHERE in ui-dump, since ui-dump's walker only
        ///                                    recognises interactable component types.
        ///
        /// Read-only: constructs a PointerEventData and calls RaycastAll, never Press/Click/Release, so
        /// it cannot itself trigger anything on screen.
        /// </summary>
        /// <summary>
        /// Task 0 measured a one-cell swipe (110 px) failing and a two-cell one (220 px) working, so
        /// a drag needs BOTH enough travel and enough frames. This is the floor on frames; travel is
        /// the caller's business, which is why `drag` takes explicit endpoints instead of a direction.
        /// </summary>
        private const int DRAG_MIN_STEPS = 8;

        /// <summary>Shared shape for the single-point pointer verbs: parse, check, queue, say so.</summary>
        private bool Pointer(string verb, string[] args, Func<Vector2, bool> queue)
        {
            if (!float.TryParse(args[0], out var x) || !float.TryParse(args[1], out var y))
                return Fail(verb, $"'{args[0]}','{args[1]}' — screenX/screenY must be numbers");
            if (!_pointer.Available)
                return Fail(verb, _pointer.Unavailable);
            if (!queue(new Vector2(x, y)))
                return Fail(verb, _pointer.Unavailable);
            return Report(verb, new[]
            {
                $"queued {verb} at ({x},{y}). The gesture plays out one event per editor tick — wait " +
                "on its EFFECT (a settle condition), never on this line.",
            });
        }

        private bool Drag(string[] args)
        {
            if (!float.TryParse(args[0], out var x1) || !float.TryParse(args[1], out var y1)
                || !float.TryParse(args[2], out var x2) || !float.TryParse(args[3], out var y2))
                return Fail("drag", "x1/y1/x2/y2 must be numbers");
            var durationMs = 250;
            if (args.Length >= 5 && !int.TryParse(args[4], out durationMs))
                return Fail("drag", $"'{args[4]}' — durationMs must be a whole number of milliseconds");
            if (!_pointer.Available)
                return Fail("drag", _pointer.Unavailable);

            var from = new Vector2(x1, y1);
            var to = new Vector2(x2, y2);
            // durationMs is ADVISORY: the pump emits one event per editor tick, so the real duration
            // is the step count times the editor's tick period, not a wall-clock promise. ~16ms is the
            // 60fps tick this converts against.
            var steps = Math.Max(DRAG_MIN_STEPS, durationMs / 16);
            if (!_pointer.Press(from))
                return Fail("drag", _pointer.Unavailable);
            for (var i = 1; i <= steps; i++)
                _pointer.Move(Vector2.Lerp(from, to, (float)i / steps));
            _pointer.Release(to);
            return Report("drag", new[]
            {
                $"queued drag ({x1},{y1}) → ({x2},{y2}): {Vector2.Distance(from, to):F0} px over " +
                $"{steps} move events. Travel is what clears the game's own drag threshold — a swipe " +
                "that is too short reports queued here and still does nothing.",
                "Plays out one event per editor tick; wait on its EFFECT, never on this line.",
            });
        }

        private bool RaycastAt(string xArg, string yArg)
        {
            if (!float.TryParse(xArg, out var x) || !float.TryParse(yArg, out var y))
                return Fail("raycast-at", $"'{xArg}','{yArg}' — screenX/screenY must be numbers");

            var es = EventSystem.current;
            if (es == null)
                return Report("raycast-at", new[]
                {
                    "EventSystem.current is NULL — nothing in the scene can receive input at all. " +
                    "This is a global failure, not a per-button one.",
                });

            var ped = new PointerEventData(es) { position = new Vector2(x, y) };
            var results = new List<RaycastResult>();
            es.RaycastAll(ped, results);

            if (results.Count == 0)
                return Report("raycast-at", new[]
                {
                    $"({x},{y}): NOTHING hit — no GraphicRaycaster covers this point, or there is no " +
                    "UI under it at all",
                });

            var lines = results.Select((r, i) =>
                $"[{i}] {r.gameObject.name} (depth {r.depth}, module {r.module?.GetType().Name}, " +
                $"raycaster {r.module?.name})").ToArray();
            return Report("raycast-at", lines);
        }

        /// <summary>
        /// Clicks whatever a real tap at this screen point would ACTUALLY hit — the raycast's own TOP
        /// result, dispatched through Unity's real IPointerClickHandler interface (ExecuteEvents),
        /// never a name lookup. Fixes the exact failure RaycastAt's docstring predicts: when more than
        /// one object shares a name, `invoke-button &lt;name&gt;` can silently resolve to the WRONG
        /// instance. Proven live on Rogue Legend, 2026-08-05, clearing a real backlog of stacked
        /// popups: three simultaneously-live instances were each named "BlackScreen" (from three
        /// different popup systems), and invoke-button started returning cheatOk:false with the
        /// raycast underneath completely unchanged the moment a second instance existed.
        ///
        /// Still not a literal OS-level tap — it goes through ExecuteEvents rather than the OS input
        /// queue — but it is the closest an Editor script gets: real raycast, real target, real
        /// IPointerClickHandler dispatch, exactly the interface every UI Button already implements.
        /// ExecuteEvents.GetEventHandler walks UP from the raycast hit to find the nearest ancestor
        /// implementing the interface, which is what real pointer events do too (the raycast often
        /// hits a child Image, not the parent Button).
        /// </summary>
        private bool TapAt(string xArg, string yArg)
        {
            if (!float.TryParse(xArg, out var x) || !float.TryParse(yArg, out var y))
                return Fail("tap-at", $"'{xArg}','{yArg}' — screenX/screenY must be numbers");

            var es = EventSystem.current;
            if (es == null)
                return Fail("tap-at", "EventSystem.current is NULL — nothing can receive input");

            var ped = new PointerEventData(es) { position = new Vector2(x, y) };
            var results = new List<RaycastResult>();
            es.RaycastAll(ped, results);
            if (results.Count == 0)
                return Fail("tap-at", $"({x},{y}): nothing hit — no UI under this point");

            var target = results[0].gameObject;
            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            if (handler == null)
                return Report("tap-at", new[]
                {
                    $"({x},{y}) hit {target.name}, but neither it nor any parent implements " +
                    "IPointerClickHandler — it may only block, not respond to clicks",
                });

            ExecuteEvents.Execute(handler, ped, ExecuteEvents.pointerClickHandler);
            return Report("tap-at", new[]
            {
                $"({x},{y}) -> clicked {handler.name} (raycast top hit {target.name})",
            });
        }

        /// <summary>
        /// Objects hidden by `hide-ui`, so `show-ui` can put them back.
        ///
        /// A record is kept rather than re-finding by name, because the UI matcher only walks ACTIVE
        /// canvases (by design — a dump should report what is on screen). The moment an object is
        /// hidden it becomes unreachable by name, so a hide with no ledger would be irreversible for
        /// the rest of the session.
        ///
        /// Static because it must outlive the bridge instance across a mid-session recompile of the
        /// GAME's assemblies. Entries are null-checked on restore: a scene load destroys the objects
        /// and also un-hides them, since SetActive is not serialized back to the prefab.
        /// </summary>
        private static readonly List<GameObject> Hidden = new List<GameObject>();

        /// <summary>
        /// Hide one UI object — a purely VISUAL, purely local suppression for capture.
        ///
        /// WHY THIS EXISTS. Every game has HUD elements an ad should not show: dev chrome (a settings
        /// gear, an FPS counter, a build label), and worse, elements that CONTRADICT the ad's framing.
        /// Rogue Legend's battle screen prints "Round 3/30", the combat-turn counter, in an ad whose
        /// spine is "survive 30 rolls" — a viewer reads it as "three rolls in" and the boss's arrival
        /// looks like a lie. Without this the only lever is cropping the element out of frame, and on
        /// footage already captured at the composition's exact size a crop is an UPSCALE: that fix
        /// cost 1.33x of resolution and threw away the HP bar sitting next to it.
        ///
        /// SCOPE — this HIDES, it never rewrites. Suppressing a confusingly-labelled element is
        /// framing, the same as cropping it out. Editing what a counter READS would put a number on
        /// screen the game never produced, which is fabricating game state, so no verb here does that.
        ///
        /// CAVEAT worth knowing before relying on it: if the game calls SetActive(true) on the element
        /// itself each time it refreshes, the hide is undone and has to be re-issued — AdStep.CheatUntil
        /// is the lever for that. Hiding a label the game only ever SetText()s sticks.
        /// </summary>
        private bool HideUi(string name, int index)
        {
            var matches = UguiDriver.MatchesInClickOrder(name);
            if (index < 0 || index >= matches.Count)
                return Fail($"hide-ui-{name}",
                    matches.Count == 0
                        ? $"no active UI object named '{name}' — run `ui-dump` to see what is actually " +
                          "on screen (already hidden? the matcher only walks ACTIVE canvases). " +
                          "A second hide of the same name fails this way — prefix setup with `show-ui` then hide."
                        : $"'{name}' has {matches.Count} match(es), index {index} is out of range");

            var target = matches[index];
            target.SetActive(false);
            // Read back rather than trusting the call: a hide that silently did nothing is exactly the
            // class of failure this kit exists to make loud.
            if (target.activeSelf)
                return Fail($"hide-ui-{name}", $"SetActive(false) did not take on '{name}' — still active");

            if (!Hidden.Contains(target)) Hidden.Add(target);
            return Report($"hide-ui-{name}", new[]
            {
                $"hid {name}[{index}] ('{target.name}') — visual only, reverts on scene load; " +
                $"{Hidden.Count} object(s) now hidden, `show-ui` restores them",
            });
        }

        /// <summary>Restore everything `hide-ui` hid. Destroyed entries are dropped, not reported as failures.</summary>
        private bool ShowUi()
        {
            var restored = 0;
            var gone = 0;
            foreach (var go in Hidden)
            {
                if (go == null) { gone++; continue; }   // Unity's null-op: destroyed by a scene load
                go.SetActive(true);
                restored++;
            }
            Hidden.Clear();
            return Report("show-ui", new[]
            {
                $"restored {restored} object(s)" +
                (gone > 0 ? $"; {gone} had already been destroyed by a scene load (which un-hides them anyway)" : ""),
            });
        }

        /// <summary>
        /// Everything on screen worth clicking. Navigation is unavoidable for surfaces that render in
        /// another scene, and prefab-derived names are guesses.
        ///
        /// Deliberately looks PAST UnityEngine.UI.Selectable. A Selectable-only dump reports "nothing
        /// clickable" for things filling half the display and you will believe it — a real capture
        /// stalled on an already-open screen whose tab was a custom AnimatedTabDisplay. So:
        ///  - non-interactable Selectables are listed and marked [locked], because absence only tells
        ///    you something once you know it means locked rather than missing (it is also a fast read
        ///    on what progression still blocks);
        ///  - objects whose clickability comes from a custom component are surfaced as candidates by
        ///    name, with their component list so they can be identified.
        /// </summary>
        private static IEnumerable<string> UiCandidates()
        {
            var found = OnScreenRows();
            return found.Count == 0 ? new[] { NothingOnScreen } : (IEnumerable<string>)found;
        }

        /// <summary>The line `ui-dump` prints when nothing is on screen — a sentence, not a name.</summary>
        internal const string NothingOnScreen = "(nothing active on screen)";

        /// <summary>
        /// Slice E.1 — THE ROWS `ui-dump` PRINTS, as data: the same walk, the same formatting, the
        /// same order, and an EMPTY list rather than the "(nothing active on screen)" sentence when
        /// there is nothing. A <c>probe</c> reports these as the names on screen at the moment its
        /// shot stopped; it must be the SAME source a person reads with `ui-dump`, or the website
        /// would be comparing a shot against a list nobody can reproduce. Static, and reads the
        /// scene only: it works whatever cheat bridge the game registered, and it writes nothing
        /// (the command's probe file is the command's side effect, not this list's).
        ///
        /// E.2's fifth audit — THE DUMP MARKER: a row whose printed name and index do NOT reach its
        /// own object ends with <see cref="NotReachedMarker"/> (<see cref="ReachMarker"/>). The rows
        /// stay in one SortedSet and sort by their FULL text, the marker included, as every row does:
        /// a marked row sorts right after the same text unmarked, and two objects that printed one
        /// identical row before (one reached, one not) are now two rows.
        /// </summary>
        internal static IReadOnlyList<string> OnScreenRows()
        {
            var found = new SortedSet<string>();
            // ONE WALK FOR THE WHOLE DUMP (the fourteenth audit, S2): each candidate's index suffix used to walk every canvas
            // again — 150 ms in one pump at 300 Selectables over 3,000 nodes, for a command that needs no tick.
            var clickOrder = UguiDriver.ClickOrderIndex.Build();

            foreach (var s in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Selectable>(
                         FindObjectsSortMode.None))
            {
                if (s == null || !s.gameObject.activeInHierarchy)
                    continue;
                var printed = IndexSuffix(s.gameObject, clickOrder);
                found.Add($"{s.gameObject.name}{printed.Suffix} [{s.GetType().Name}]" +
                          $"{(s.interactable ? "" : " [locked]")}{LabelOf(s.gameObject)}" +
                          ReachMarker(s.gameObject, printed, clickOrder));
            }

            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (t == null || !t.gameObject.activeInHierarchy)
                    continue;
                if (!Regex.IsMatch(t.gameObject.name, "Tab|Button|Btn|Toggle", RegexOptions.IgnoreCase))
                    continue;
                if (t.GetComponent<UnityEngine.UI.Selectable>() != null)
                    continue; // already listed with its real component type
                var printed = IndexSuffix(t.gameObject, clickOrder);
                found.Add($"{t.gameObject.name}{printed.Suffix} " +
                          $"[candidate: {ComponentSummary(t.gameObject)}]{LabelOf(t.gameObject)}" +
                          ReachMarker(t.gameObject, printed, clickOrder));
            }

            return found.ToList();
        }

        /// <summary>What a row ends with when the name and index it prints do NOT reach that row's
        /// object — two spaces, then this text, after everything else in the row (after the label's
        /// closing quote, or after the type bracket when there is no label). A contract with the
        /// website's reader: these exact bytes.</summary>
        internal const string NotReachedMarker = "  (not what this name reaches)";

        /// <summary>
        /// E.2's fifth audit — DOES THE PRINTED NAME REACH THIS OBJECT? The row prints a selector (the
        /// object's name, plus "@label" when that is the suffix it printed) and an index (the i of
        /// " #i", else 0); the kit asks its OWN finder, <see cref="UguiDriver.Find"/> — the object
        /// <c>click selector index</c> presses — whether that is this object. "" when it is, else
        /// <see cref="NotReachedMarker"/>: null (the object is outside every active canvas and nothing
        /// of that name is inside one) or another object (a same-named object the click search does
        /// reach, printed or not; an "@label" whose one match is someone else; a name holding "@",
        /// which the finder reads as name@label). Right by construction: no matching is re-implemented
        /// here. Before it, the kit printed the SAME row text for an object its click reaches and one
        /// it does not, so no rule over the text could tell them apart.
        ///
        /// Asked of the dump's ONE-WALK snapshot (<see cref="UguiDriver.ClickOrderIndex.Find"/>, stack pass 4): calling
        /// <see cref="UguiDriver.Find"/> per row walked every canvas again per row — the N walks per dump A2′'s fourteenth
        /// audit (S2) removed. Still the kit's own finder's answer, not a second matcher: the snapshot's
        /// <c>Matches</c> is held to <see cref="UguiDriver.MatchesInClickOrder"/> by
        /// <c>TheOneWalkIndexListsEverySelectorsMatchesInTheClickOrder</c>.
        /// </summary>
        private static string ReachMarker(GameObject go, (string Suffix, string Selector, int Index) printed,
            UguiDriver.ClickOrderIndex clickOrder) =>
            ReferenceEquals(clickOrder.Find(printed.Selector, printed.Index), go) ? "" : NotReachedMarker;

        /// <summary>
        /// " #i" when more than one active object shares this name, where i is the index `click name i`
        /// selects — otherwise "".
        ///
        /// Without it a generically-named UI is untargetable: one game's level button, main-menu button
        /// and spin button were ALL just called "Button", the dump printed three identical-looking rows,
        /// and picking between them was guesswork. The index comes from UguiDriver's own click-order
        /// enumeration so the number printed is the number that works.
        ///
        /// Also note the dump collects into a SortedSet of formatted lines, so two same-named objects
        /// with the same type and label would otherwise collapse into ONE row — the suffix keeps them
        /// distinct, which matters because a silently-missing row reads as "that element isn't there".
        /// (Two such objects of which only ONE is in the click search get no suffix; the dump marker
        /// keeps those two rows apart — <see cref="ReachMarker"/>.)
        ///
        /// Returns the suffix it prints AND what that row then tells a shot to write: the selector (the
        /// name, plus "@label" when that is the suffix) and the index (the i of " #i", else 0) — what
        /// <see cref="ReachMarker"/> hands the kit's finder.
        /// </summary>
        private static (string Suffix, string Selector, int Index) IndexSuffix(GameObject go, UguiDriver.ClickOrderIndex clickOrder)
        {
            var matches = clickOrder.Matches(go.name);
            if (matches.Count < 2)
                return ("", go.name, 0);

            // Prefer the LABEL selector, and print it in the exact form a shot should use. The index is
            // an artifact of Unity's unordered FindObjectsByType enumeration and DOES change between
            // sessions — live, "Level 1" was #2 in one run and #1 in the next, so a shot pinned to an
            // index clicked the wrong button. A label is authored content and stays put.
            var label = UguiDriver.LabelOf(go);
            if (label != null && clickOrder.Matches($"{go.name}@{label}").Count == 1)
                return ($"@{label}", $"{go.name}@{label}", 0);

            for (var i = 0; i < matches.Count; i++)
                if (ReferenceEquals(matches[i], go))
                    return ($" #{i} (unstable — index order is not guaranteed)", go.name, i);
            return ("", go.name, 0);
        }

        /// <summary>
        /// A label from EITHER uGUI Text or TextMeshPro. A UI.Text-only lookup returns empty labels on
        /// any modern game, leaving bare GameObject names to guess from. TMP is reached reflectively so
        /// this works whether or not the package is present.
        /// </summary>
        private static string LabelOf(GameObject go)
        {
            var text = go.GetComponentInChildren<UnityEngine.UI.Text>();
            if (text != null && !string.IsNullOrWhiteSpace(text.text))
                return $"  \"{text.text.Trim()}\"";

            var tmpType = GameReflection.FindType("TMP_Text") ?? GameReflection.FindType("TextMeshProUGUI");
            if (tmpType != null)
            {
                var comp = go.GetComponentInChildren(tmpType);
                if (comp != null && tmpType.GetProperty("text")?.GetValue(comp) is string s
                    && !string.IsNullOrWhiteSpace(s))
                    return $"  \"{s.Trim()}\"";
            }
            return "";
        }

        private static string ComponentSummary(GameObject go)
        {
            var names = go.GetComponents<Component>().Where(c => c != null && c is not Transform)
                .Select(c => c.GetType().Name).Take(4);
            var joined = string.Join("+", names);
            return string.IsNullOrEmpty(joined) ? "no components" : joined;
        }

        private bool CameraVerb(string name, CameraPose.Outcome result) =>
            result.Ok ? Report(name, result.Lines) : Fail(name, result.Error);

        /// <summary>Write output where the operator can read it, and echo to the console.</summary>
        protected bool Report(string name, IEnumerable<string> lines)
        {
            var text = string.Join("\n", lines);
            _reported = true;
            WriteProbe(name, text);
            Debug.Log($"[RecorderKit] {name}:\n{text}");
            return true;
        }

        /// <summary>
        /// Record WHY a command failed, to the probe file as well as the console — otherwise the
        /// operator sees only an unexplained `cheatOk: false`, which is exactly how a dump that
        /// resolved the wrong type looked like nothing happening at all.
        /// </summary>
        protected bool Fail(string name, string reason)
        {
            _reported = true;
            WriteProbe($"FAILED-{name}", "FAILED: " + reason);
            Debug.LogWarning($"[RecorderKit] {name}: {reason}");
            return false;
        }

        private void WriteProbe(string name, string text)
        {
            try
            {
                Directory.CreateDirectory(_probeDir);
                var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                File.WriteAllText(Path.Combine(_probeDir, "last.txt"), text);
                File.WriteAllText(Path.Combine(_probeDir, safe + ".txt"), text);
            }
            catch (IOException e)
            {
                // Never fail a cheat because a debug artifact could not be written.
                Debug.LogWarning($"[RecorderKit] could not write probe output: {e.Message}");
            }
        }
    }
}
