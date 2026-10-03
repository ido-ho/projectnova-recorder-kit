using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P5 (§3.5 step 2) — WHAT AN <c>auto-try</c> CLAIM CARRIES: the screen need, the ONLY buttons the kit
    /// may press for it (READ's names that match — chosen by the box, never by the kit), the export names arrival counts
    /// against, the closes it may tap, where it starts (a declared start, or lobby after boot) and its caps. Mirror of
    /// <c>StudioAutoTryClaim</c> (<c>apps/api/src/studio/studio-capture-job.ts</c>).
    /// </summary>
    public sealed class AutoTryRequest
    {
        public const int MaxCandidates = 6;
        public const int MaxTries = 3;
        public const double MaxSeconds = 300;

        public string NeedKind { get; private set; } = "screen";
        public string NeedName { get; private set; } = "";
        public IReadOnlyList<string> Candidates { get; private set; } = Array.Empty<string>();
        public HashSet<string> AuthoredNames { get; private set; } = new(StringComparer.Ordinal);
        public IReadOnlyList<string> Dismiss { get; private set; } = Array.Empty<string>();
        public StartClaim? Start { get; private set; }
        public int Tries { get; private set; } = MaxTries;
        public double Seconds { get; private set; } = MaxSeconds;

        public const string MissingReason =
            "this auto-try arrived without what to press (no complete `autoTry` on the claim) — nothing was pressed";

        public static AutoTryRequest? FromClaim(string body)
        {
            try { return FromJson(NovaJson.ParseObject(body)["autoTry"]); }
            catch (Exception) { return null; }
        }

        /// <summary>Null unless whole: a need, 1–6 candidate selectors, the names, the closes, caps within the kit's own.</summary>
        public static AutoTryRequest? FromJson(JToken? t)
        {
            if (t is not JObject o || o["need"] is not JObject n) return null;
            var kind = ProbeRequest.Str(n["kind"]);
            var name = ProbeRequest.Str(n["name"]);
            if (kind == null || name == null) return null;
            if (o["candidates"] is not JArray c || c.Count == 0 || c.Count > MaxCandidates || c.Any(x => x.Type != JTokenType.String || string.IsNullOrWhiteSpace(x.Value<string>())))
                return null;
            if (o["authoredNames"] is not JArray a || a.Any(x => x.Type != JTokenType.String)) return null;
            if (o["dismiss"] is not JArray d || d.Any(x => x.Type != JTokenType.String)) return null;
            StartClaim? start = null;
            var st = o["start"];
            if (st != null && st.Type != JTokenType.Null)
            {
                start = StartClaim.FromJson(st);
                if (start == null) return null;
            }
            var caps = o["caps"] as JObject;
            var tries = caps != null && NovaJson.TryNumber(caps["tries"], out var tv) ? (int)tv : MaxTries;
            var seconds = caps != null && NovaJson.TryNumber(caps["seconds"], out var sv) ? sv : MaxSeconds;
            return new AutoTryRequest
            {
                NeedKind = kind,
                NeedName = name,
                Candidates = c.Values<string>().Select(x => x!).ToList(),
                AuthoredNames = new HashSet<string>(a.Values<string>().Select(x => x!), StringComparer.Ordinal),
                Dismiss = d.Values<string>().Select(x => x!).ToList(),
                Start = start,
                // the kit's own caps bound whatever the claim says
                Tries = Math.Clamp(tries, 1, MaxTries),
                Seconds = Math.Clamp(seconds, 1, MaxSeconds),
            };
        }

        public JObject ToJson() => new()
        {
            ["need"] = new JObject { ["kind"] = NeedKind, ["name"] = NeedName },
            ["candidates"] = new JArray(Candidates),
            ["authoredNames"] = new JArray(AuthoredNames.OrderBy(x => x, StringComparer.Ordinal)),
            ["dismiss"] = new JArray(Dismiss),
            ["start"] = Start == null ? JValue.CreateNull() : Start.ToJson(),
            ["caps"] = new JObject { ["tries"] = Tries, ["seconds"] = Seconds },
        };
    }

    /// <summary>
    /// Learn-and-drive v3 P5 (§3.5 step 2) — AUTO-TRY ONE SCREEN NEED, no person: press, one at a time, ONLY the candidate
    /// buttons the claim carried (the allowlist is the fence — <see cref="PressGuard.Refusal"/> with the candidates and the
    /// closes; the risky-word fence second), the dismiss loop before each press, and after each press ask whether at least
    /// two of the export's names are NEW (arrival by names, never assumed — a lobby widget wired in code reports a click that
    /// opens nothing). THE STOP RULE: arrived → stop; the screen CHANGED without arriving → STOP (the kit is on a screen
    /// nobody named — pressing the next candidate from there would be a guess, and it may be a spend screen); nothing
    /// changed → the next candidate. Capped by tries and seconds. The kit reports; the box grades (<c>gradeAutoTry</c>)
    /// against the names IT sent. $0: no screen check is asked (<c>visionCalls: 0</c> is reported, measured).
    /// </summary>
    public sealed class AutoTryRun
    {
        public const double AfterPressSec = 1.5;
        public const double BootWaitSec = StartPathRun.BootWaitSec;

        private readonly AutoTryRequest _request;
        private readonly HashSet<string> _allowlist;

        public JArray Attempts { get; } = new();
        public PressLog Presses { get; } = new();
        public bool Found { get; private set; }
        public string? Stopped { get; private set; }
        public bool Finished { get; private set; }
        public double Seconds { get; private set; }

        /// <summary>Fresh audit of P5, K8 — the declared start's presses (played by the director before this run), reported
        /// with this run's through the same caps, their flags beside its own.</summary>
        public PressLog? StartPresses { get; set; }

        /// <param name="readWatch">How a watched path is read (the director's gated read, <see cref="AdDirector.WatchReader"/>);
        /// null = no watched values (K8: with it, a close here that moves one is flagged, as the start path's are).</param>
        public AutoTryRun(AutoTryRequest request, Func<string, string?>? readWatch = null)
        {
            _request = request;
            if (readWatch != null && request.Start != null)
            {
                Presses.Watch = request.Start.Watch;
                Presses.ReadWatch = readWatch;
            }
            _allowlist = new HashSet<string>(request.Candidates.Select(PressGuard.NameOf).Concat(request.Dismiss.Select(PressGuard.NameOf)), StringComparer.Ordinal);
            _allowlist.Remove("");
        }

        public IEnumerable Run(IReplayScreen screen, Func<double> now, bool waitForBoot)
        {
            var started = now();
            if (waitForBoot)
            {
                var by = now() + BootWaitSec;
                while (!_request.Candidates.Any(screen.Exists) && now() < by) yield return null;
            }
            var tries = 0;
            foreach (var press in _request.Candidates)
            {
                if (tries >= _request.Tries) { Stopped = $"used its {_request.Tries} tries"; break; }
                if (now() - started >= _request.Seconds) { Stopped = $"used its {_request.Seconds:0} s"; break; }
                foreach (var _ in DismissLoop.Run(screen, _request.Dismiss, _allowlist, now, Presses)) yield return null;
                var attempt = new JObject { ["press"] = press, ["pressed"] = false, ["refused"] = JValue.CreateNull(), ["before"] = JValue.CreateNull(), ["after"] = JValue.CreateNull() };
                Attempts.Add(attempt);
                var refusal = PressGuard.Refusal(press, _allowlist);
                if (refusal != null) { attempt["refused"] = refusal; continue; }
                if (!screen.Exists(press)) { attempt["refused"] = $"'{press}' is not on screen"; continue; }
                var before = screen.Observe();
                if (!screen.Click(press)) { attempt["refused"] = $"'{press}' is on screen but the click did not land"; continue; }
                tries++;
                attempt["pressed"] = true;
                var settle = now() + AfterPressSec;
                while (now() < settle) yield return null;
                var after = screen.Observe();
                Presses.Edges.Add(before.Key, press, after.Key);
                attempt["before"] = ScreenSignature.BeforeJson(before);
                attempt["after"] = ScreenSignature.AfterJson(before, after);
                var arrived = ScreenSignature.Added(before, after).Count(_request.AuthoredNames.Contains);
                if (arrived >= TeachAnalysis.MinArrival)
                {
                    Found = true;
                    Stopped = "arrived";
                    break;
                }
                if (after.Key != before.Key)
                {
                    Stopped = $"'{press}' changed the screen without arriving — stopped, never pressing on from a screen nobody named";
                    break;
                }
                // Fresh audit of P5, K4: a game that keeps every panel under one wrapper never changes its roots, so "the screen
                // changed" is also NEW NAMES on screen. Something opened that is not the need — the next candidate would be
                // pressed on a screen nobody named (it may be a spend popup), so the run stops instead.
                if (ScreenSignature.Added(before, after).Count > 0)
                {
                    Stopped = $"'{press}' put something new on screen without arriving (the panel roots did not change, so the kit cannot tell which screen it is) — stopped, never pressing on from a screen nobody named";
                    break;
                }
            }
            Stopped ??= "every candidate was pressed";
            Seconds = now() - started;
            Finished = true;
        }

        public string ToFactsJson(string kitVersion, string? gameCommit, JObject? start, string? startSha256)
        {
            var edges = new DirectorEdges();
            edges.AddAll(StartPresses?.Edges);
            edges.AddAll(Presses.Edges);
            var flags = (StartPresses?.Flags ?? new List<DismissFlag>()).Concat(Presses.Flags);
            return new JObject
        {
            ["kitVersion"] = kitVersion,
            ["gameCommit"] = gameCommit == null ? JValue.CreateNull() : gameCommit,
            ["attempts"] = Attempts,
            ["found"] = Found,
            ["stopped"] = Stopped == null ? JValue.CreateNull() : Stopped,
            ["seconds"] = Math.Round(Seconds, 2),
            // $0 by construction — MEASURED: this job asks no screen check
            ["visionCalls"] = 0,
            ["startSha256"] = startSha256 == null ? JValue.CreateNull() : startSha256,
            ["start"] = start == null ? JValue.CreateNull() : start,
            ["edges"] = edges.ToJson(),
            ["edgesDropped"] = edges.Dropped,
            ["dismissFlags"] = new JArray(flags.Select(f => f.ToJson())),
        }.ToString(Formatting.None);
        }
    }

    /// <summary>
    /// Learn-and-drive v3 P5 (§3.6a item 6) — A <c>recipe-try</c>: a recipe's chain played from the anchor, recording off, to
    /// RE-PROVE it before a capture batch (remote config and live-ops add popups with no game commit). The claim carries the
    /// start (<see cref="StartClaim"/>); the director plays it with zero shots; the facts say what the start path did. The box
    /// grades each recipe (<c>gradeRecipeTry</c>); a failure goes to a short re-teach, never an AI repair loop.
    /// </summary>
    public static class RecipeTryJob
    {
        public static StartClaim? FromClaim(string body)
        {
            try { return NovaJson.ParseObject(body)["recipeTry"] is JObject o ? StartClaim.FromJson(o["start"]) : null; }
            catch (Exception) { return null; }
        }

        public const string MissingReason =
            "this recipe Try arrived without the recipe to play (no complete `recipeTry` on the claim) — nothing was played";

        /// <summary>The director's settings for a start played alone: the kit-job settings (the gate forced on, one attempt),
        /// no recording, the chain, the watched paths, every press an edge.</summary>
        public static AdDirector.Options OptionsFor(string projectRoot, StartPlan plan)
        {
            var options = KitJobRun.DirectorOptions(projectRoot);
            options.Recorder = new NoRecordingDriver(projectRoot);
            options.StartPath = plan.Chain;
            options.WatchGets = plan.Watch;
            options.RecordEdges = true;
            return options;
        }

        public static string FactsJson(string kitVersion, string? gameCommit, StartPlan plan, AdDirector? director) => new JObject
        {
            ["kitVersion"] = kitVersion,
            ["gameCommit"] = gameCommit == null ? JValue.CreateNull() : gameCommit,
            ["startSha256"] = plan.Sha256,
            ["start"] = director?.StartPath?.ToJson() ?? (JToken)JValue.CreateNull(),
            ["log"] = new JArray((director?.LogLines ?? Array.Empty<string>()).TakeLast(50)),
            ["edges"] = director?.Presses.Edges.ToJson() ?? new JArray(),
            ["edgesDropped"] = director?.Presses.Edges.Dropped ?? 0,
            ["dismissFlags"] = new JArray((director?.Presses.Flags ?? new List<DismissFlag>()).Select(f => f.ToJson())),
        }.ToString(Formatting.None);
    }
}
