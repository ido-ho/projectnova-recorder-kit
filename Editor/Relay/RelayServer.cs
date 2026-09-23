using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>A running (or finished) shot capture the relay can poll. Task 7's director implements it.</summary>
    public interface IShotRunHandle
    {
        bool IsFinished { get; }
        bool AllCaptured { get; }
        string Summary { get; }
        /// <summary>Step 4: the kept take's step times as the sidecar JSON, or null (a ready-gate
        /// run, a handle that records none).</summary>
        string? StepMarksJson { get; }
    }

    /// <summary>Everything the relay touches in the editor, injectable for tests.</summary>
    public sealed class RelayEnv
    {
        public Func<bool> IsPlaying = () => EditorApplication.isPlayingOrWillChangePlaymode;
        public Action<bool> SetPlaying = v => EditorApplication.isPlaying = v;
        public Action RequestRecompile = () =>
        {
            AssetDatabase.Refresh();
            CompilationPipeline.RequestScriptCompilation();
        };
        public Func<GameAdapter> Adapter = () => AdapterRegistry.Current;
        /// <summary>Absolute output path in, capture requested. Production uses ScreenCapture.</summary>
        public Action<string> CaptureScreenshot = path => ScreenCapture.CaptureScreenshot(path);
        public Func<double> Now = () => EditorApplication.timeSinceStartup;

        /// <summary>
        /// Open a scene by asset path; returns the opened scene's path, or null if it could not be
        /// opened. Returning the RESULT rather than void is deliberate: EditorSceneManager throws or
        /// no-ops on a bad path, and a scene action that reports success without changing the scene
        /// would send an operator hunting a capture bug that is really a wrong path.
        /// </summary>
        public Func<string, string?>? OpenScene = path =>
        {
            try
            {
                var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path);
                return scene.IsValid() ? scene.path : null;
            }
            catch (Exception)
            {
                return null;
            }
        };
        /// <summary>
        /// Start a shot with its parameter bindings. The dictionary is empty for an unparameterized
        /// run, which is every shot authored before this existed.
        /// </summary>
        public Func<AdShot, IReadOnlyDictionary<string, string>, IShotRunHandle?>? StartShot;

        /// <summary>
        /// Run the adapter's ready gate on its own — the director with zero shots. Exists because
        /// EVERYTHING outside a shot (driving a screen by hand, ui-dumping a menu, answering a vision
        /// request) otherwise starts from whatever state the game boots into, which for a game that
        /// re-enters its tutorial on every Play Mode start means doing discovery on a screen no shot
        /// will ever capture. Without this, each game has to hand-write its own "reach the baseline"
        /// cheat; with it, every game gets one for free from the gate it already wrote.
        /// </summary>
        public Func<IShotRunHandle?>? StartReady;
    }

    /// <summary>
    /// The kit's own transport: scans AdRelay/commands/, executes, answers in AdRelay/results/.
    /// Protocol rules (all load-bearing, from the spike):
    ///  - every JSON protocol file (commands/results/processed markers) is temp-file-then-rename
    ///    (AtomicFile) — binary capture payloads (screenshots) go through the injected
    ///    CaptureScreenshot callback instead, which does not carry the same atomicity guarantee;
    ///  - a command whose result or processed marker already exists is NEVER re-executed;
    ///  - reload-triggering commands (play/stop/recompile/run-shot) are marked processed BEFORE
    ///    acting, so the reborn watcher can't replay them — a dropped in-flight command times out
    ///    on the client, which re-sends under a NEW id;
    ///  - recompile is REFUSED while playing (recompiling in Play Mode corrupts the session).
    /// CAVEAT: this class is a plain object with no cross-domain-reload persistence. A reload
    /// while a shot or screenshot is pending (_pendingShot/_pendingScreenshot) silently drops that
    /// in-memory state — the original command is already moved to processed, so no result is ever
    /// written, and the client's only recourse is its own timeout + a fresh-id retry with no
    /// visibility into what (if anything) actually ran. Accepted for this task (StartShot is inert
    /// here); Task 7, which wires a real shot runner, must decide deliberately whether that's
    /// still acceptable once run-shot is live, not assume it's solved by this comment.
    /// </summary>
    public sealed class RelayServer
    {
        private const double SCREENSHOT_TIMEOUT_SEC = 10;

        private readonly string _root;
        private readonly RelayEnv _env;

        private (string Id, IShotRunHandle Handle)? _pendingShot;
        /// <summary>True when _pendingShot is a bare ready-gate run, so the result names the right field.</summary>
        private bool _pendingIsReady;
        private (string Id, string Path, double Deadline)? _pendingScreenshot;

        public RelayServer(string projectRoot, RelayEnv env)
        {
            _root = projectRoot;
            _env = env;
            RelayPaths.EnsureDirs(projectRoot);
        }

        /// <summary>
        /// First ping of a session is the other leftover-hold door (Play enter is in
        /// <c>RelayBoot</c>). Armed holds are left alone so a stills pose survives ping.
        /// Ping itself must still answer if the sweep fails.
        /// </summary>
        private void SweepOrphanedCameraHold()
        {
            try
            {
                var sweep = CameraPose.SweepOrphaned(CameraPose.Host.ForProject(_root));
                if (!sweep.Ok)
                    Debug.LogWarning($"[RecorderKit] leftover camera-hold: {sweep.Error}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RecorderKit] leftover camera-hold sweep threw: {e.Message}");
            }
        }

        public void PumpOnce()
        {
            CheckPendingShot();
            CheckPendingScreenshot();
            foreach (var file in Directory.GetFiles(RelayPaths.Commands(_root), "*.json").OrderBy(f => f))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (File.Exists(RelayPaths.Result(_root, id)) ||
                    File.Exists(RelayPaths.ProcessedMarker(_root, id)))
                {
                    TryDelete(file); // stale duplicate — answered or in flight before a reload
                    continue;
                }
                try
                {
                    Execute(file, id);
                }
                catch (Exception e)
                {
                    // A per-game adapter is by definition the least-tested code in the system — it
                    // is authored fresh against an unfamiliar codebase. Without this, one throwing
                    // seam (a state probe reading a member that does not exist yet, a cheat hitting
                    // a null manager) escapes the pump, so the command is NEVER marked processed and
                    // is retried every editor frame forever. The status heartbeat keeps ticking from
                    // its own update handler, so the operator sees a healthy editor and a command
                    // that times out with "is the editor open with the Recorder Kit installed?" —
                    // pointing at the transport instead of the adapter. Answer the ONE command with
                    // the real exception and move on. Live-found onboarding roguelegend 2026-07-28.
                    Debug.LogError($"[RecorderKit] command {id} threw: {e}");
                    WriteResult(id, false, null, $"adapter or command threw: {e.GetType().Name}: {e.Message}");
                    MoveToProcessed(file, id);
                }
            }
        }

        private void Execute(string file, string id)
        {
            string json;
            try { json = File.ReadAllText(file); }
            catch (IOException) { return; } // mid-write; retry next pump

            var cmd = RelayEnvelope.ParseCommand(id, json);
            if (cmd == null)
            {
                WriteResult(id, false, null, "malformed command");
                MoveToProcessed(file, id);
                return;
            }

            switch (cmd.Action)
            {
                case "ping":
                {
                    SweepOrphanedCameraHold();
                    var adapter = _env.Adapter();
                    // Load-bearing order: Shots() must run before shotLoadErrors is read below — Shots()
                    // is what refreshes JsonShotLoader.LastErrors as a side effect of re-parsing shots.json.
                    var shots = adapter.Shots();
                    WriteResult(id, true, new JObject
                    {
                        ["pong"] = true,
                        ["kitVersion"] = KitInfo.Version,
                        ["gameId"] = adapter.GameId,
                        ["adapterRegistered"] = !ReferenceEquals(adapter, GameAdapter.Null),
                        ["isPlaying"] = _env.IsPlaying(),
                        ["shots"] = new JArray(shots.Select(s => s.Name)),
                        // Shot-load errors ride on PING, deliberately not on status.json: status is
                        // written on a 2s heartbeat and its TS mirror (relay-protocol.ts) is
                        // documented as requiring byte-for-byte parity, "change both or neither".
                        // ping is already adapter-aware and its TS client parses loosely.
                        ["shotLoadErrors"] = new JArray(JsonShotLoader.LastErrors),
                    }, null);
                    MoveToProcessed(file, id);
                    break;
                }
                case "probe-state":
                {
                    WriteResult(id, true, new JObject
                    {
                        ["state"] = _env.Adapter().StateProbe.CurrentStateName,
                        ["isPlaying"] = _env.IsPlaying(),
                    }, null);
                    MoveToProcessed(file, id);
                    break;
                }
                case "run-cheat":
                {
                    if (!RequirePlaying(file, id)) break;
                    var command = cmd.Args["command"]?.Value<string>();
                    if (string.IsNullOrEmpty(command))
                    {
                        WriteResult(id, false, null, "run-cheat needs args.command");
                        MoveToProcessed(file, id);
                        break;
                    }
                    var ok = _env.Adapter().CheatBridge.Run(command!);
                    WriteResult(id, true, new JObject { ["cheatOk"] = ok }, null);
                    MoveToProcessed(file, id);
                    break;
                }
                case "screenshot":
                {
                    if (!RequirePlaying(file, id)) break;
                    if (_pendingScreenshot != null)
                    {
                        WriteResult(id, false, null, "busy: a screenshot is already pending");
                        MoveToProcessed(file, id);
                        break;
                    }
                    var path = RelayPaths.Screenshot(_root, id);
                    MoveToProcessed(file, id);
                    _env.CaptureScreenshot(path);
                    _pendingScreenshot = (id, path, _env.Now() + SCREENSHOT_TIMEOUT_SEC);
                    break;
                }
                case "run-shot":
                {
                    if (!RequirePlaying(file, id)) break;
                    var name = cmd.Args["name"]?.Value<string>();
                    var adapter = _env.Adapter();
                    var shots = adapter.Shots();
                    var shot = shots.FirstOrDefault(s => s.Name == name);
                    if (_env.StartShot == null)
                    {
                        WriteResult(id, false, null, "no shot runner wired");
                        MoveToProcessed(file, id);
                        break;
                    }
                    if (shot == null)
                    {
                        WriteResult(id, false, null,
                            $"unknown shot '{name}'. Registered: {string.Join(", ", shots.Select(s => s.Name))}");
                        MoveToProcessed(file, id);
                        break;
                    }
                    if (_pendingShot != null)
                    {
                        WriteResult(id, false, null, "busy: a shot is already running");
                        MoveToProcessed(file, id);
                        break;
                    }
                    // Optional `params` object. Absent → empty. Values are validated in the kit's
                    // binding layer, not here, so one rejection message serves every transport.
                    var bindings = new Dictionary<string, string>();
                    if (cmd.Args["params"] is JObject paramsObj)
                    {
                        foreach (var prop in paramsObj.Properties())
                            // ToString() (not Value<string>()) — a nested object/array value would
                            // otherwise throw InvalidCastException here. ToString() never throws for
                            // any JToken shape, and for the scalar case it already validates (string
                            // values round-trip unquoted; see RunShot_PassesTheParamsObjectToTheShotRunner).
                            bindings[prop.Name] = prop.Value?.ToString() ?? "";
                    }
                    MoveToProcessed(file, id); // BEFORE starting — a reload mid-shot must not replay
                    var handle = _env.StartShot(shot, bindings);
                    if (handle == null)
                    {
                        WriteResult(id, false, null, "director refused to start (already running?)");
                        break;
                    }
                    _pendingShot = (id, handle);
                    break;
                }
                case "ready":
                {
                    if (!RequirePlaying(file, id)) break;
                    if (_env.StartReady == null)
                    {
                        WriteResult(id, false, null, "no ready runner wired");
                        MoveToProcessed(file, id);
                        break;
                    }
                    if (_pendingShot != null)
                    {
                        WriteResult(id, false, null, "busy: a shot is already running");
                        MoveToProcessed(file, id);
                        break;
                    }
                    MoveToProcessed(file, id); // BEFORE starting, same reload rule as run-shot
                    var readyHandle = _env.StartReady();
                    if (readyHandle == null)
                    {
                        WriteResult(id, false, null, "director refused to start (already running?)");
                        break;
                    }
                    _pendingShot = (id, readyHandle);
                    _pendingIsReady = true;
                    break;
                }
                case "play":
                {
                    if (_env.IsPlaying())
                    {
                        WriteResult(id, true, new JObject { ["alreadyPlaying"] = true }, null);
                        MoveToProcessed(file, id);
                        break;
                    }
                    MoveToProcessed(file, id); // BEFORE the reload play mode triggers
                    WriteResult(id, true, new JObject { ["accepted"] = true }, null);
                    _env.SetPlaying(true);
                    break;
                }
                case "stop":
                {
                    if (!_env.IsPlaying())
                    {
                        WriteResult(id, true, new JObject { ["alreadyStopped"] = true }, null);
                        MoveToProcessed(file, id);
                        break;
                    }
                    MoveToProcessed(file, id);
                    WriteResult(id, true, new JObject { ["accepted"] = true }, null);
                    _env.SetPlaying(false);
                    break;
                }
                // Open a scene by asset path. Every onboarding needs this at Phase 0: a freshly-opened
                // project sits on whatever scene was last saved (often an empty default), so Play Mode
                // captures a blank frame — the capture pipeline is fine, there is simply nothing in the
                // scene. Until now the skill told the OPERATOR to open the boot scene by hand, which an
                // agent driving the relay cannot do, and which every new game hits.
                case "open-scene":
                {
                    if (_env.IsPlaying())
                    {
                        WriteResult(id, false, null, "refused: exit Play Mode before opening a scene");
                        MoveToProcessed(file, id);
                        break;
                    }
                    var scenePath = cmd.Args["path"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(scenePath))
                    {
                        WriteResult(id, false, null, "open-scene needs {\"path\":\"Assets/.../Scene.unity\"}");
                        MoveToProcessed(file, id);
                        break;
                    }
                    MoveToProcessed(file, id);
                    if (_env.OpenScene == null)
                    {
                        WriteResult(id, false, null, "no scene opener wired");
                        break;
                    }
                    // Report the resulting scene rather than echoing the request, so a silent no-op
                    // (wrong path, scene not in the project) cannot read as success.
                    var opened = _env.OpenScene(scenePath!);
                    WriteResult(id, opened != null, opened == null ? null : new JObject
                    {
                        ["openedScene"] = opened,
                        ["requested"] = scenePath,
                    }, opened == null ? $"could not open '{scenePath}' — is that path in this project?" : null);
                    break;
                }
                case "recompile":
                {
                    if (_env.IsPlaying())
                    {
                        // Load-bearing rule: recompiling in Play Mode corrupts the session.
                        WriteResult(id, false, null, "refused: exit Play Mode first, then recompile");
                        MoveToProcessed(file, id);
                        break;
                    }
                    MoveToProcessed(file, id); // BEFORE the domain reload
                    WriteResult(id, true, new JObject { ["accepted"] = true }, null);
                    _env.RequestRecompile();
                    break;
                }
                default:
                {
                    WriteResult(id, false, null, $"unknown action '{cmd.Action}'");
                    MoveToProcessed(file, id);
                    break;
                }
            }
        }

        private bool RequirePlaying(string file, string id)
        {
            if (_env.IsPlaying())
                return true;
            WriteResult(id, false, null, "requires Play Mode (send 'play' first)");
            MoveToProcessed(file, id);
            return false;
        }

        private void CheckPendingShot()
        {
            if (_pendingShot is not { } p || !p.Handle.IsFinished)
                return;
            var data = new JObject
            {
                [_pendingIsReady ? "ready" : "captured"] = p.Handle.AllCaptured,
                ["summary"] = p.Handle.Summary,
            };
            // Step 4: the bridge (`unity` CLI) writes this beside the take it fetches
            var marks = p.Handle.StepMarksJson;
            if (marks != null) data["stepMarks"] = JToken.Parse(marks);
            WriteResult(p.Id, true, data, null);
            _pendingShot = null;
            _pendingIsReady = false;
        }

        private void CheckPendingScreenshot()
        {
            if (_pendingScreenshot is not { } p)
                return;
            if (File.Exists(p.Path))
            {
                WriteResult(p.Id, true, new JObject { ["path"] = p.Path }, null);
                _pendingScreenshot = null;
            }
            else if (_env.Now() > p.Deadline)
            {
                // Name the cause that is almost always responsible, because the bare timeout reads as a
                // capture bug and sends people into the adapter. ScreenCapture only completes on a
                // rendered frame, so a player that is not rendering never produces one — and with
                // Application.runInBackground false, an unfocused editor stops rendering entirely.
                WriteResult(p.Id, false, null,
                    "screenshot never appeared (timeout). ScreenCapture only completes on a RENDERED " +
                    "frame: check Application.runInBackground is true (the kit forces it on entering " +
                    "Play Mode; PlayerSettings.runInBackground is the separate BUILD setting, read at " +
                    "play start), that Play Mode is running and not paused, and that the Game view is " +
                    "actually rendering. The same cause also makes UI clicks land but never be processed.");
                _pendingScreenshot = null;
            }
        }

        private void WriteResult(string id, bool ok, JObject? data, string? error) =>
            AtomicFile.Write(RelayPaths.Result(_root, id), RelayEnvelope.SerializeResult(id, ok, data, error));

        private void MoveToProcessed(string file, string id)
        {
            try
            {
                var dest = RelayPaths.ProcessedMarker(_root, id);
                if (File.Exists(dest))
                    File.Delete(dest);
                File.Move(file, dest);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"[RecorderKit] could not archive command {id}: {e.Message}");
                TryDelete(file);
            }
        }

        private static void TryDelete(string file)
        {
            try { File.Delete(file); }
            catch (IOException) { }
        }
    }
}
