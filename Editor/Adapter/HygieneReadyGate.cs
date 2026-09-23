using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Footage-hygiene writes declared in <c>Library/Nova/adapter.json</c> <c>ready</c>.
    /// Run the mute command (no claim that SFX survived), force
    /// <c>Time.timeScale == 1</c>, apply resist per upcoming shot. A leftover
    /// timescale-0 or an unmuted bed fails ready.
    ///
    /// Lives in the kit so a data-driven game (no adapter C#) can satisfy invariant 16
    /// the same way a custom <c>IReadyGate</c> does. HUD-off is not here — that needs
    /// a game hook, not a reflection write.
    ///
    /// Re-reads adapter.json on every call (same reason shots are a Func): editing the
    /// ready block must take effect on the next <c>ready</c> / <c>run-shot</c> without
    /// a domain reload.
    /// </summary>
    public sealed class HygieneReadyGate : IReadyGate
    {
        private readonly Func<HygieneSpec> _spec;
        private readonly Func<float> _timeScale;
        private readonly Action<float> _setTimeScale;
        private readonly Func<string?> _lastGetText;

        public HygieneReadyGate(
            Func<HygieneSpec> spec,
            Func<float> timeScale,
            Action<float> setTimeScale,
            Func<string?> lastGetText)
        {
            _spec = spec;
            _timeScale = timeScale;
            _setTimeScale = setTimeScale;
            _lastGetText = lastGetText;
        }

        public IEnumerable WaitUntilReady(DirectorContext ctx)
        {
            var spec = _spec() ?? new HygieneSpec();
            if (spec.Problem is { } problem)
            {
                ctx.Log("ready: " + problem);
                ctx.LastOpSucceeded = false;
                yield break;
            }
            if (!spec.HasAny)
            {
                ctx.LastOpSucceeded = true;
                yield break;
            }

            var scaleIn = _timeScale();
            if (Math.Abs(scaleIn - 1f) > 0.001f)
            {
                ctx.Log($"ready: leftover Time.timeScale={scaleIn} (want 1) — fails ready");
                ctx.LastOpSucceeded = false;
                yield break;
            }

            var board = WantsBoard(ctx.UpcomingShots);
            if (board)
            {
                var mute = spec.Mute;
                if (mute != null && mute.Length > 0)
                {
                    if (!ctx.Cheats.Run(mute))
                    {
                        ctx.Log($"ready: mute write failed — '{mute}'");
                        ctx.LastOpSucceeded = false;
                        yield break;
                    }
                    ctx.Log($"ready: mute wrote '{mute}'");
                }

                // ONE WRITE PER EDITOR TICK (the thirteenth audit): a resist write per upcoming board shot, and "Run all
                // shots" hands this gate every shot in the file — 5,588 writes, each asking the lever gate afresh, took
                // 16.6 s of one tick over a delivered 512 KB file. Same writes, same order, same verdict; a tick after each.
                foreach (var ok in ApplyResist(spec, ctx))
                {
                    if (!ok) yield break;
                    yield return null;
                }
            }

            _setTimeScale(1f);
            yield return null;

            var scale = _timeScale();
            if (Math.Abs(scale - 1f) > 0.001f)
            {
                ctx.Log($"ready: leftover Time.timeScale={scale} (want 1) — fails ready");
                ctx.LastOpSucceeded = false;
                yield break;
            }
            ctx.Log("ready: Time.timeScale is 1");

            if (board && !string.IsNullOrEmpty(spec.MuteGet))
            {
                // A LEVER SINCE THE FOURTEENTH AUDIT: a `get` runs property getters in the studio's game, so on a delivered
                // project it needs a tick like the mute write does. Refused, NOTHING WAS READ — the gate fails, and says so
                // by name, never as a reading of the bed ("unmuted" is the verdict of a read that ran and said false).
                var read = Levers.MuteGetLever(spec.MuteGet!);
                if (!ctx.Cheats.Run(read))
                {
                    ctx.Log(ctx.Cheats is LeverGateBridge { LastRunRefused: true }
                        ? $"ready: muteGet was not read — '{read}' is not ticked on this machine, so whether the bed is muted " +
                          "is unknown; fails ready (tick it in Nova Capture)"
                        : $"ready: could not read muteGet ({spec.MuteGet}) — whether the bed is muted is unknown; fails ready");
                    ctx.LastOpSucceeded = false;
                    yield break;
                }
                var text = _lastGetText() ?? "";
                if (!LooksTrue(text))
                {
                    ctx.Log($"ready: muteGet still not true ({text.Trim()}) — unmuted bed fails ready");
                    ctx.LastOpSucceeded = false;
                    yield break;
                }
            }

            ctx.LastOpSucceeded = true;
        }

        /// <summary>Each resist write in turn, in the upcoming shots' order: true when it held, false when it failed (and
        /// then no more). The caller yields between them.</summary>
        private static IEnumerable<bool> ApplyResist(HygieneSpec spec, DirectorContext ctx)
        {
            var shots = ctx.UpcomingShots;
            if (shots.Count == 0)
            {
                yield return RunResist(spec, ctx, on: true, name: "ready-gate");
                yield break;
            }

            foreach (var shot in shots)
            {
                if (shot.Baseline == AdShot.BaselineLobby)
                    continue;
                var ok = RunResist(spec, ctx, on: shot.Resist ?? true, name: shot.Name);
                yield return ok;
                if (!ok)
                    yield break;
            }
        }

        private static bool RunResist(HygieneSpec spec, DirectorContext ctx, bool on, string name)
        {
            var cmd = on ? spec.ResistOn : spec.ResistOff;
            if (cmd == null || cmd.Length == 0)
                return true;
            if (!ctx.Cheats.Run(cmd))
            {
                ctx.Log($"ready: resist write failed for {name} — '{cmd}'");
                ctx.LastOpSucceeded = false;
                return false;
            }
            ctx.Log(on
                ? $"ready: resist on for {name}"
                : $"ready: resist off for {name}");
            return true;
        }

        /// <summary>Bare ready and any board shot need battle hygiene. Lobby-only skips it
        /// because TitleScene has no <c>BattleSceneManager</c> / <c>Player</c>.</summary>
        internal static bool WantsBoard(IReadOnlyList<UpcomingShot> shots) =>
            shots.Count == 0 || !System.Linq.Enumerable.All(shots, s => s.Baseline == AdShot.BaselineLobby);

        /// <summary>Parse a <c>get</c> probe line such as <c>Type.member = True</c>.</summary>
        internal static bool LooksTrue(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var cut = text.LastIndexOf('=');
            var token = (cut >= 0 ? text.Substring(cut + 1) : text).Trim();
            return bool.TryParse(token, out var value) && value;
        }
    }

    /// <summary>The <c>ready</c> object in <c>Library/Nova/adapter.json</c>. Missing or
    /// malformed file → empty spec (same load-never-throws contract as shots.json).</summary>
    public sealed class HygieneSpec
    {
        public string? Mute;
        public string? MuteGet;
        public string? ResistOn;
        public string? ResistOff;
        /// <summary>True when adapter.json contained a <c>ready</c> key. A present-but-empty
        /// block fails ready (fail closed). No key at all is the no-op for other games.</summary>
        public bool Declared;

        public bool HasAny =>
            !string.IsNullOrEmpty(Mute) || !string.IsNullOrEmpty(MuteGet)
            || !string.IsNullOrEmpty(ResistOn) || !string.IsNullOrEmpty(ResistOff);

        /// <summary>The ready gate's own refusal of a block it reads, in its words.</summary>
        internal const string DeclaredButEmpty = "adapter.json ready block is present but empty or unreadable";

        /// <summary>
        /// Why the ready gate fails every capture on this block, or null: a <c>ready</c> key that names no command the gate
        /// reads (<see cref="DeclaredButEmpty"/>). <see cref="HygieneReadyGate.WaitUntilReady"/> fails on it, and
        /// <c>SyncNova</c>'s delivery check refuses a delivered adapter.json on it before anything is written (the eleventh
        /// audit, ruling 1: the delivery check is the reader), through <see cref="From"/> — the function <see cref="Parse"/>
        /// runs after its parse.
        /// </summary>
        internal string? Problem => Declared && !HasAny ? DeclaredButEmpty : null;

        public static HygieneSpec Load(string projectRoot)
        {
            var path = Path.Combine(RelayPaths.NovaDir(projectRoot), "adapter.json");
            if (!File.Exists(path))
                return new HygieneSpec();
            try
            {
                return Parse(File.ReadAllText(path));
            }
            catch
            {
                return new HygieneSpec();
            }
        }

        public static HygieneSpec Parse(string json)
        {
            try
            {
                // The same reader the rest of the kit uses: a `mute` command that LOOKS like a
                // date must stay the text it is, or the ready block runs one command while the
                // lever list asks about another (see NovaJson).
                return From(NovaJson.ParseObject(json));
            }
            catch
            {
                return new HygieneSpec();
            }
        }

        /// <summary>The same reader, over a document already parsed (the eleventh audit: <c>SyncNova</c>'s delivery check).</summary>
        internal static HygieneSpec From(Newtonsoft.Json.Linq.JObject obj)
        {
            var spec = new HygieneSpec();
            if (obj["ready"] == null)
                return spec;
            spec.Declared = true;
            var ready = obj["ready"] as Newtonsoft.Json.Linq.JObject;
            if (ready == null)
                return spec;
            spec.Mute = Str(ready["mute"]);
            spec.MuteGet = Str(ready["muteGet"]);
            spec.ResistOn = Str(ready["resistOn"]);
            spec.ResistOff = Str(ready["resistOff"]);
            return spec;
        }

        private static string? Str(Newtonsoft.Json.Linq.JToken? token)
        {
            var s = (string?)token;
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
    }
}
