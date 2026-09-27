using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P5 (§3.6a item 5) — THE FREE EDGE LOG, the kit's half: every press a kit-run job makes — a start
    /// path's goal step, a popup close, a shot's click or hold — as (screen signature before, press, screen signature
    /// after). The box appends a run's list to <c>learn/edges.jsonl</c> once (<c>edge-log.ts</c>); nothing plans on it.
    /// Capped here below the box's own caps (<c>EDGE_CAPS</c>: 400 per run, 2,000 characters per signature, 256 per
    /// press), so the box never drops an edge the kit sent.
    /// </summary>
    public sealed class DirectorEdges
    {
        public const int Max = 200;
        public const int MaxKey = 2000;
        public const int MaxPress = 256;
        /// <summary>Fresh audit of P5, K7 — the most UTF-8 bytes the kept edges may take in all. 200 edges of two full
        /// signatures each are ~850 KB, and a capture's <c>done</c> is a JSON body under the API's 100 KB default: past this,
        /// an edge is dropped (and counted), so the report that carries them always fits.</summary>
        public const int MaxBytes = 48 * 1024;

        public readonly List<(string Before, string Press, string After)> List = new();
        /// <summary>Edges not kept past <see cref="Max"/> or <see cref="MaxBytes"/> — said in the facts
        /// (<c>edgesDropped</c>), never silent.</summary>
        public int Dropped { get; private set; }
        /// <summary>The UTF-8 bytes of the kept edges (their three texts).</summary>
        public int Bytes { get; private set; }

        public void Add(string before, string press, string after)
        {
            if (string.IsNullOrEmpty(press)) return;
            var e = (Cut(before, MaxKey), Cut(press, MaxPress), Cut(after, MaxKey));
            var size = SizeOf(e.Item1, e.Item2, e.Item3);
            if (List.Count >= Max || Bytes + size > MaxBytes) { Dropped++; return; }
            List.Add(e);
            Bytes += size;
        }

        /// <summary>Every edge of <paramref name="other"/>, through the same caps, and its own dropped count — how two runs'
        /// presses become one list (K8: never a raw InsertRange past the caps).</summary>
        public void AddAll(DirectorEdges? other)
        {
            if (other == null) return;
            foreach (var (b, p, a) in other.List) Add(b, p, a);
            Dropped += other.Dropped;
        }

        /// <summary>One edge's UTF-8 bytes, as <see cref="Bytes"/> counts it.</summary>
        public static int SizeOf(string before, string press, string after) =>
            System.Text.Encoding.UTF8.GetByteCount(before ?? "") + System.Text.Encoding.UTF8.GetByteCount(press ?? "")
            + System.Text.Encoding.UTF8.GetByteCount(after ?? "");

        private static string Cut(string s, int max) => (s ?? "").Length <= max ? s ?? "" : s!.Substring(0, max);

        public JArray ToJson() => new(List.Select(e => new JObject { ["before"] = e.Before, ["press"] = e.Press, ["after"] = e.After }));
    }

    /// <summary>P5 (§3.6a item 1) — a popup close that CHANGED a watched value (a daily claim, a free reward): flagged, never a
    /// stop — it changes what the next run's popups will be, so a person should know.</summary>
    public sealed class DismissFlag
    {
        public string Dismiss = "";
        public string Path = "";
        public string? Before;
        public string? After;

        public JObject ToJson() => new()
        {
            ["dismiss"] = Dismiss, ["path"] = Path,
            ["before"] = Before == null ? JValue.CreateNull() : Before,
            ["after"] = After == null ? JValue.CreateNull() : After,
        };
    }

    /// <summary>What a run's kit-initiated presses leave behind: the edges, the popups closed, the flags, the log lines.</summary>
    public sealed class PressLog
    {
        public readonly DirectorEdges Edges = new();
        public readonly List<string> Dismissed = new();
        public readonly List<DismissFlag> Flags = new();
        public readonly List<string> Lines = new();
        /// <summary>The <c>get</c> paths whose values a dismiss must not change (the ticked cheats' own check paths).</summary>
        public IReadOnlyList<string> Watch = Array.Empty<string>();
        /// <summary>Reads one watched path: its rendered value, or null when it was not read (not ticked, threw). The director
        /// reads through the lever gate (<c>get &lt;path&gt;</c> is a ticked read — a getter is code), never around it.</summary>
        public Func<string, string?>? ReadWatch;

        internal Dictionary<string, string?>? ReadWatched()
        {
            if (Watch.Count == 0 || ReadWatch == null) return null;
            var d = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var p in Watch)
            {
                try { d[p] = ReadWatch(p); }
                catch (Exception e) { d[p] = null; Lines.Add($"watched '{p}' could not be read: {e.Message}"); }
            }
            return d;
        }
    }

    /// <summary>
    /// Learn-and-drive v3 P5 (§3.5, §3.6a item 1) — THE DISMISS LOOP, one implementation for every caller: tap whichever of
    /// the known close names is on screen, in any order, until two quiet passes (cap <see cref="MaxPresses"/>) — spike B's
    /// 5/5 rule, not a fixed list. EVERY press passes <see cref="PressGuard.Refusal"/> with the caller's allowlist, whether
    /// or not the run's lever gate is live: these presses are the kit's own, never a person's. A close the guard refuses (a
    /// risky word — some popups are spend screens) or whose click does not land is left alone, and its popup stays up — the
    /// caller's unknown-overlay check then stops the run instead of guessing.
    /// </summary>
    public static class DismissLoop
    {
        public const int MaxPresses = 12;
        public const int QuietPassesNeeded = 2;
        public const double QuietPassSec = 0.5;
        public const double AfterDismissSec = 0.8;

        public static IEnumerable Run(IReplayScreen screen, IReadOnlyList<string> names, IReadOnlyCollection<string> allowlist,
            Func<double> now, PressLog log)
        {
            if (names.Count == 0) yield break;
            var quiet = 0;
            var presses = 0;
            var refusedOnce = new HashSet<string>(StringComparer.Ordinal);
            while (quiet < QuietPassesNeeded && presses < MaxPresses)
            {
                var showing = names.FirstOrDefault(d => !refusedOnce.Contains(d) && screen.Exists(d));
                double until;
                if (showing == null)
                {
                    quiet++;
                    until = now() + QuietPassSec;
                }
                else
                {
                    var refusal = PressGuard.Refusal(showing, allowlist);
                    var before = screen.Observe();
                    var watchedBefore = refusal == null ? log.ReadWatched() : null;
                    if (refusal != null || !screen.Click(showing))
                    {
                        refusedOnce.Add(showing);
                        log.Lines.Add($"dismiss '{showing}' not pressed: {refusal ?? "the click did not land"}");
                        continue;
                    }
                    log.Dismissed.Add(showing);
                    presses++;
                    quiet = 0;
                    var settle = now() + AfterDismissSec;
                    while (now() < settle) yield return null;
                    log.Edges.Add(before.Key, showing, screen.Observe().Key);
                    var watchedAfter = log.ReadWatched();
                    if (watchedBefore != null && watchedAfter != null)
                        foreach (var kv in watchedBefore)
                            if (kv.Value != null && watchedAfter.TryGetValue(kv.Key, out var after) && after != null && after != kv.Value)
                                log.Flags.Add(new DismissFlag { Dismiss = showing, Path = kv.Key, Before = kv.Value, After = after });
                    continue;
                }
                while (now() < until) yield return null;
            }
            if (presses >= MaxPresses) log.Lines.Add($"the dismiss loop stopped at its cap of {MaxPresses} presses");
        }
    }

    /// <summary>
    /// Learn-and-drive v3 P5 (§3.6, §3.6a items 1 and 4) — A RECIPE'S DECLARED START, PLAYED: the chain (anchor first,
    /// <see cref="RecipeChain"/>) from "lobby after boot" to the recipe a Try, a capture, an auto-try or a re-proof starts
    /// from. Pure over <see cref="IReplayScreen"/>, a cheat runner and a clock, so the tests pump it with a fake screen.
    ///
    /// <list type="bullet">
    /// <item><b>The dismiss loop before, between and after EVERY step</b> (<see cref="DismissLoop"/>), through the press guard,
    /// with the chain's own names as the allowlist — a popup that rises after step 1 of a 3-step path is closed before
    /// step 2 (spike B ran it only before the goal steps, outside the guard).</item>
    /// <item><b>An unknown overlay stops the run</b>: after each goal step and its dismiss loop, a panel root on screen that
    /// the teach did not see after that step (and no known close removed) → STOP and ask, never guess — 2 of RL's 4 lobby
    /// popups are spend screens. The taught roots were recorded BEFORE the teacher's following closes, so they are a
    /// superset (popups included): fewer roots now is fine; an extra root is the stop. A step with no taught screen to
    /// compare is said in the log and not checked.</item>
    /// <item><b>Arrival, as a teach is confirmed</b>: at least two of the recipe's arrival names (fewer when it has fewer) NEW
    /// after its last goal step — measured against the screen just before that step, never mere presence (a lobby that
    /// already shows an "Equipment" label would pass a presence check while the screen never opened).</item>
    /// <item><b>Chained</b>: the next recipe's start must be on screen right after the last one arrived (a short wait) —
    /// not up = "its start is not the last one's end", a chain failure, never a boot failure.</item>
    /// <item><b>Stuck on Loading is a boot failure</b>: the FIRST recipe's start not up within <see cref="BootWaitSec"/> is
    /// <see cref="KindBootStuck"/> — the caller retries the boot (<see cref="RestartRule"/>), never counts a failed replay.</item>
    /// </list>
    /// It never retries a step (a kit job's one attempt), and a cheat step runs through the caller's lever gate.
    /// </summary>
    public sealed class StartPathRun
    {
        public const double BootWaitSec = 60;
        public const double ChainWaitSec = 5;
        public const double AfterStepSec = 1.5;
        /// <summary>Round 2, R3 — how long after a recipe's last step its arrival is awaited (polled), beyond the step's settle.</summary>
        public const double ArrivalWaitSec = 5;

        public const string KindBootStuck = "boot-stuck";
        public const string KindUnknownOverlay = "unknown-overlay";
        public const string KindStep = "step";
        public const string KindArrival = "arrival";
        public const string KindChain = "chain";

        private readonly IReadOnlyList<RecipeFile.Recipe> _chain;
        private readonly PressLog _log;
        private readonly HashSet<string> _allowlist;
        private readonly List<string> _dismiss;

        public bool Ran { get; private set; }
        public bool Arrived { get; private set; }
        public bool Finished { get; private set; }
        /// <summary>Why the start was not reached (one sentence), or null.</summary>
        public string? Why { get; private set; }
        /// <summary><see cref="KindBootStuck"/>, <see cref="KindUnknownOverlay"/>, <see cref="KindStep"/>,
        /// <see cref="KindArrival"/> or <see cref="KindChain"/>; null when it arrived.</summary>
        public string? FailedKind { get; private set; }
        public string? FailedRecipe { get; private set; }
        /// <summary>0-based index into the failed recipe's steps, or null (a boot, chain or arrival failure).</summary>
        public int? FailedStep { get; private set; }
        /// <summary>The panel roots nobody expected, when the run stopped on an unknown overlay.</summary>
        public List<string> UnknownRoots { get; } = new();
        /// <summary>Per recipe: id, arrived, steps pressed.</summary>
        public JArray Recipes { get; } = new();

        public StartPathRun(IReadOnlyList<RecipeFile.Recipe> chain, PressLog log)
        {
            _chain = chain;
            _log = log;
            _allowlist = new HashSet<string>(StringComparer.Ordinal);
            _dismiss = new List<string>();
            foreach (var r in chain)
            {
                _allowlist.UnionWith(RecipeReplay.AllowlistOf(r.Steps, r.Dismiss));
                foreach (var d in r.Dismiss) if (!_dismiss.Contains(d)) _dismiss.Add(d);
            }
        }

        /// <summary>The names this run may press: every step's and every close's NAME, from the chain alone.</summary>
        public IReadOnlyCollection<string> Allowlist => _allowlist;
        /// <summary>Every close name the chain knows, in first-seen order — the run-wide dismiss list.</summary>
        public IReadOnlyList<string> Dismiss => _dismiss;

        /// <summary>
        /// Fresh audit of P5, K2 — THE DESTINATION'S OWN BUTTONS, never pressed as a close: the clickable names of the LAST
        /// screen the teach recorded (after the teacher's own closes on arrival, when there were any). A name still there
        /// once the teacher had closed what rose on arrival belongs to the destination — a panel's own "Close" is the one a
        /// generic close name would otherwise press. Empty when the recipe carries no screens (a studio recipe).
        /// </summary>
        public static HashSet<string> DestinationButtons(RecipeFile.Recipe r)
        {
            var last = r.Screens.OrderBy(s => s.Step).LastOrDefault();
            return new HashSet<string>(last?.Buttons.Select(PressGuard.NameOf) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        }

        /// <summary>The closes that may be pressed while <paramref name="destination"/>'s buttons are protected.</summary>
        public static List<string> ClosesExcept(IEnumerable<string> closes, IReadOnlyCollection<string> destination) =>
            closes.Where(d => !destination.Contains(PressGuard.NameOf(d))).ToList();

        /// <summary>The last recipe's own buttons (<see cref="DestinationButtons"/>) — what a caller's pass after arrival
        /// must never press.</summary>
        public HashSet<string> ClosesAfterArrivalProtected =>
            _chain.Count == 0 ? new HashSet<string>(StringComparer.Ordinal) : DestinationButtons(_chain[_chain.Count - 1]);

        /// <summary>Fresh audit of P5, K4 — true once a press added names to the screen while its panel ROOTS did not change:
        /// this game keeps its panels under one wrapper, so the kit cannot tell its screens apart and the unknown-overlay
        /// stop cannot fire. Said in the log and in the facts (<c>screensBlind</c>), never guessed around.</summary>
        public bool ScreensBlind { get; private set; }

        /// <summary>The taught panel roots after goal step <paramref name="k"/> of <paramref name="r"/> (the k-th <c>goal</c>
        /// screen), or null when the recipe has none for that step.</summary>
        public static List<string>? TaughtRootsAfter(RecipeFile.Recipe r, int k)
        {
            var goals = r.Screens.Where(s => s.Role == "goal").OrderBy(s => s.Step).ToList();
            return k < goals.Count ? goals[k].Roots : null;
        }

        /// <summary>Is <paramref name="r"/>'s start on screen: its first step's element (or one matching two of its three
        /// parts). A known close on screen does NOT count (fresh audit of P5, K9). A recipe that starts with a cheat (or has no
        /// steps) is up at once.</summary>
        public static bool StartIsUp(RecipeFile.Recipe r, IReplayScreen screen) =>
            new RecipeReplay(r.Steps, r.Dismiss).StartIsUp(screen);

        private void Fail(string kind, string why, string? recipe = null, int? step = null)
        {
            FailedKind = kind;
            Why = why;
            FailedRecipe = recipe;
            FailedStep = step;
            _log.Lines.Add("START STOPPED: " + why);
        }

        public IEnumerable Run(IReplayScreen screen, Func<string, bool> runCheat, Func<double> now)
        {
            Ran = true;
            for (var ri = 0; ri < _chain.Count; ri++)
            {
                var r = _chain[ri];
                var result = new JObject { ["id"] = r.Id, ["arrived"] = false, ["pressed"] = 0 };
                Recipes.Add(result);
                var waitSec = ri == 0 ? BootWaitSec : ChainWaitSec;
                var by = now() + waitSec;
                // fresh audit of P5, K9: the start is up only when its FIRST STEP is — a known close on screen is not (a
                // generic "Close" is on many screens). A close that is showing while the start is awaited is closed.
                // K2: the recipe this one starts from has arrived — its own buttons are never pressed as closes
                var closes = ri == 0 ? _dismiss : ClosesExcept(_dismiss, DestinationButtons(_chain[ri - 1]));
                while (!StartIsUp(r, screen) && now() < by)
                {
                    if (closes.Any(screen.Exists))
                        foreach (var _ in DismissLoop.Run(screen, closes, _allowlist, now, _log)) yield return null;
                    else yield return null;
                }
                if (!StartIsUp(r, screen))
                {
                    if (ri == 0)
                        Fail(KindBootStuck, $"the game never reached the screen recipe {r.Id} starts on ('{FirstName(r)}' was not on screen after {BootWaitSec:0} s) — it may be stuck loading; a boot retry, not a failed replay", r.Id);
                    else
                        Fail(KindChain, $"recipe {r.Id} starts where recipe {_chain[ri - 1].Id} ends, but its start ('{FirstName(r)}') was not on screen after {_chain[ri - 1].Id} arrived — its start is not the last one's end", r.Id);
                    Finished = true;
                    yield break;
                }
                for (var k = 0; k < r.Steps.Count; k++)
                {
                    foreach (var _ in DismissLoop.Run(screen, k == 0 ? closes : _dismiss, _allowlist, now, _log)) yield return null;
                    var step = r.Steps[k] as JObject ?? new JObject();
                    var before = screen.Observe();
                    var kind = step["kind"]?.Value<string>() ?? "";
                    string? refused = null;
                    string press = "";
                    if (kind == "cheat")
                    {
                        var command = step["command"]?.Value<string>() ?? "";
                        press = "cheat " + command;
                        bool ok;
                        try { ok = runCheat(command); }
                        catch (Exception e) { ok = false; _log.Lines.Add($"the cheat '{command}' threw: {e.Message}"); }
                        if (!ok) refused = $"the cheat '{command}' was refused or failed";
                    }
                    else if (kind == "click" || kind == "hold")
                    {
                        var target = RecipeReplay.TargetFor(screen, step, new JObject(), out refused);
                        if (target != null)
                        {
                            press = target;
                            refused = PressGuard.Refusal(target, _allowlist);
                            if (refused == null)
                            {
                                if (kind == "click")
                                {
                                    if (!screen.Click(target)) refused = $"'{target}' is on screen but the click did not land (not interactable, or covered)";
                                }
                                else
                                {
                                    screen.PointerDown(target);
                                    // fresh audit of P5, K11: a run stopped mid-hold still lets go (the director disposes it)
                                    try
                                    {
                                        var until = now() + Math.Max(0, step["seconds"]?.Value<double>() ?? 0);
                                        while (now() < until) yield return null;
                                    }
                                    finally { screen.PointerUp(target); }
                                }
                            }
                        }
                    }
                    else refused = $"a '{kind}' step is not replayed";
                    if (refused != null)
                    {
                        Fail(KindStep, $"recipe {r.Id} step {k + 1}: {refused}", r.Id, k);
                        Finished = true;
                        yield break;
                    }
                    result["pressed"] = k + 1;
                    var settle = now() + AfterStepSec;
                    while (now() < settle) yield return null;
                    var afterStep = screen.Observe();
                    _log.Edges.Add(before.Key, press, afterStep.Key);
                    if (!ScreensBlind && kind != "cheat" && afterStep.Key == before.Key && ScreenSignature.Added(before, afterStep).Count >= TeachAnalysis.MinArrival)
                    {
                        ScreensBlind = true;
                        _log.Lines.Add($"recipe {r.Id} step {k + 1} opened something but the panel roots did not change — this game keeps its panels under one wrapper, so the kit cannot tell its screens apart and cannot stop on a popup nobody taught");
                    }
                    var last = k == r.Steps.Count - 1;
                    // K2: ARRIVAL IS CHECKED BEFORE ANY CLOSE after the last step — a close pressed first could close the destination.
                    // Round 2, R3: POLLED up to ArrivalWaitSec, never one sample — a destination that animates in is not "did
                    // not arrive" because it was slow.
                    if (last && r.Arrival.Count > 0)
                    {
                        var needAtArrival = Math.Min(TeachAnalysis.MinArrival, r.Arrival.Count);
                        List<string> SeenNow() { var a = ScreenSignature.Added(before, screen.Observe()); return r.Arrival.Where(a.Contains).ToList(); }
                        var seenAtArrival = r.Arrival.Where(ScreenSignature.Added(before, afterStep).Contains).ToList();
                        var arriveBy = now() + ArrivalWaitSec;
                        while (seenAtArrival.Count < needAtArrival && now() < arriveBy)
                        {
                            yield return null;
                            seenAtArrival = SeenNow();
                        }
                        if (seenAtArrival.Count < needAtArrival)
                        {
                            Fail(KindArrival, $"recipe {r.Id} did not arrive: {seenAtArrival.Count} of its arrival names appeared after its last step (need {needAtArrival}){(seenAtArrival.Count > 0 ? ": " + string.Join(", ", seenAtArrival) : "")}", r.Id);
                            Finished = true;
                            yield break;
                        }
                    }
                    // the closes that rose with this step (after the last one: never the destination's own), then: is anything
                    // up nobody taught?
                    foreach (var _ in DismissLoop.Run(screen, last ? ClosesExcept(_dismiss, DestinationButtons(r)) : _dismiss, _allowlist, now, _log))
                        yield return null;
                    if (kind == "cheat") continue;
                    var taught = TaughtRootsAfter(r, k);
                    if (taught == null)
                    {
                        _log.Lines.Add($"recipe {r.Id} step {k + 1}: no taught screen to compare, so an overlay cannot be told from the screen");
                        continue;
                    }
                    // NOT excused for having been up before the step: a spend popup already up at step 1 that no known close
                    // removed is exactly what must stop the run
                    var extra = screen.Observe().Roots.Where(x => !taught.Contains(x)).ToList();
                    if (extra.Count > 0)
                    {
                        UnknownRoots.AddRange(extra);
                        Fail(KindUnknownOverlay,
                            $"after recipe {r.Id} step {k + 1}, something the teach never saw is on screen ({string.Join(", ", extra)}) and no known close removed it — stopped and asked, never guessed (it could be a spend screen)",
                            r.Id, k);
                        Finished = true;
                        yield break;
                    }
                }
                result["arrived"] = true;
            }
            Arrived = true;
            Finished = true;
        }

        private static string FirstName(RecipeFile.Recipe r) =>
            r.Steps.Count > 0 && r.Steps[0] is JObject o ? o["name"]?.Value<string>() ?? o["command"]?.Value<string>() ?? "its first step" : "its first step";

        public JObject ToJson() => new()
        {
            ["ran"] = Ran,
            ["arrived"] = Arrived,
            ["failedKind"] = FailedKind == null ? JValue.CreateNull() : FailedKind,
            ["why"] = Why == null ? JValue.CreateNull() : Why,
            ["failedRecipe"] = FailedRecipe == null ? JValue.CreateNull() : FailedRecipe,
            ["failedStep"] = FailedStep == null ? JValue.CreateNull() : FailedStep.Value,
            ["unknownRoots"] = new JArray(UnknownRoots),
            ["recipes"] = new JArray(Recipes),
            ["screensBlind"] = ScreensBlind,
        };
    }

    /// <summary>
    /// Learn-and-drive v3 P5 (§3.6, spike B — Rogue Legend crashed Unity on fast Play restarts) — THE RESTART RULE, for
    /// every game: between takes that play a declared start, reset INSIDE ONE PLAY SESSION first (a ticked reset cheat, or a
    /// back-to-lobby recipe); only when neither exists restart Play, after a settle wait (default 30 s, per project); and a
    /// start that never came up is a BOOT failure — retry the boot a few times, never count it as a failed replay.
    /// </summary>
    public static class RestartRule
    {
        public const int MaxBootRetries = 2;
        public const double DefaultSettleSec = TeachJob.DefaultRestartSettleSec;

        public enum Move { None, ResetCheat, BackRecipe, PlayRestart, BootRetry, GiveUp }

        /// <summary>What to do before take <paramref name="takeIndex"/> (0-based) of a job.</summary>
        public static Move BeforeTake(int takeIndex, bool hasStartPath, string? resetCheat, bool hasBackRecipe)
        {
            if (takeIndex == 0 || !hasStartPath) return Move.None;
            if (!string.IsNullOrWhiteSpace(resetCheat)) return Move.ResetCheat;
            if (hasBackRecipe) return Move.BackRecipe;
            return Move.PlayRestart;
        }

        /// <summary>What to do after a start path stopped with <paramref name="failedKind"/>, having retried the boot
        /// <paramref name="bootRetries"/> times already.</summary>
        public static Move AfterStartStopped(string? failedKind, int bootRetries) =>
            failedKind == StartPathRun.KindBootStuck && bootRetries < MaxBootRetries ? Move.BootRetry : Move.GiveUp;
    }
}
