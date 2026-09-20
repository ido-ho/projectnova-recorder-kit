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
            if (spec.Declared && !spec.HasAny)
            {
                ctx.Log("ready: adapter.json ready block is present but empty or unreadable");
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

                if (!ApplyResist(spec, ctx))
                    yield break;
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
                if (!ctx.Cheats.Run("get " + spec.MuteGet))
                {
                    ctx.Log($"ready: could not read muteGet ({spec.MuteGet}) — unmuted bed fails ready");
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

        private static bool ApplyResist(HygieneSpec spec, DirectorContext ctx)
        {
            var shots = ctx.UpcomingShots;
            if (shots.Count == 0)
                return RunResist(spec, ctx, on: true, name: "ready-gate");

            foreach (var shot in shots)
            {
                if (shot.Baseline == AdShot.BaselineLobby)
                    continue;
                if (!RunResist(spec, ctx, on: shot.Resist ?? true, name: shot.Name))
                    return false;
            }
            return true;
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
            var spec = new HygieneSpec();
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
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
            }
            catch
            {
                return new HygieneSpec();
            }
            return spec;
        }

        private static string? Str(Newtonsoft.Json.Linq.JToken? token)
        {
            var s = (string?)token;
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
    }
}
