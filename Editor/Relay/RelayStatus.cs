using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Writes AdRelay/status.json — the client's view of the editor across domain reloads:
    ///  - a fresh bootId per domain (this class is constructed from [InitializeOnLoad], so a NEW
    ///    bootId in the file means a new domain came up — the "recompile done" signal);
    ///  - compile FAILURE keeps the old domain alive, and the old domain writes compileErrors —
    ///    the client's failure signal (no new bootId will ever arrive);
    ///  - compileGeneration, incremented at the START of every compile pass, so a client can tell
    ///    WHICH pass the errors on disk belong to (see the field's comment);
    ///  - a heartbeat timestamp so the client can tell a live editor from a dead file.
    /// </summary>
    public sealed class RelayStatus
    {
        private const double HEARTBEAT_EVERY_SEC = 2;

        private readonly string _projectRoot;
        private readonly string _bootId = Guid.NewGuid().ToString("N");
        private readonly List<string> _compileErrors = new();

        /// <summary>
        /// Counts compile passes within this domain. Load-bearing for the client: after asking for
        /// a recompile there is a window in which compilation has not started yet, so status.json
        /// still carries the PREVIOUS pass's errors under an unchanged bootId — indistinguishable
        /// from "your recompile failed" without a way to date the errors. A client that captured
        /// this counter before its request treats same-bootId errors as its own only once the
        /// counter has advanced. Resetting to 0 on a domain reload is harmless: a reload also
        /// changes bootId, which the client checks first. (Live-found 2026-07-28: `admiral relay
        /// recompile` reported a successful fix as still-failing.)
        /// </summary>
        private int _compileGeneration;

        private double _nextHeartbeat;

        public RelayStatus(string projectRoot)
        {
            _projectRoot = projectRoot;
        }

        public static string BuildStatusJson(string bootId, bool isPlaying,
            IReadOnlyList<string>? compileErrors, int compileGeneration, string gameId,
            string heartbeatUtc)
        {
            return new JObject
            {
                ["bootId"] = bootId,
                ["heartbeatUtc"] = heartbeatUtc,
                ["isPlaying"] = isPlaying,
                ["compileErrors"] = compileErrors == null || compileErrors.Count == 0
                    ? (JToken)JValue.CreateNull()
                    : new JArray(compileErrors),
                ["compileGeneration"] = compileGeneration,
                ["kitVersion"] = KitInfo.Version,
                ["gameId"] = gameId,
            }.ToString();
        }

        public static IReadOnlyList<string> ErrorMessages(CompilerMessage[] messages) =>
            messages.Where(m => m.type == CompilerMessageType.Error).Select(m => m.message).ToList();

        public void Attach()
        {
            Write();
            EditorApplication.playModeStateChanged += _ => Write();
            // Cleared per compile pass, not per domain: without this, a fixed error from an
            // earlier failed save-compile-fail cycle would linger in compileErrors forever
            // (nothing else resets the list short of a full domain reload, which never happens
            // while compilation keeps failing).
            // Write() immediately (not just clear in memory): a client that issues fix-then-
            // recompile polls status.json right away, and without this the stale error from the
            // PREVIOUS failed pass is still on disk under the same (not-yet-reloaded) bootId —
            // indistinguishable from "failed again." Live-caught during Task 11's recompile drill.
            // The generation bump is what closes the REST of that window — the stretch between the
            // client's request and this event firing, when the clear has not happened yet.
            CompilationPipeline.compilationStarted += _ =>
            {
                _compileGeneration++;
                _compileErrors.Clear();
                Write();
            };
            CompilationPipeline.assemblyCompilationFinished += (_, messages) =>
                _compileErrors.AddRange(ErrorMessages(messages));
            CompilationPipeline.compilationFinished += _ => Write();
            EditorApplication.update += HeartbeatTick;
        }

        private void HeartbeatTick()
        {
            if (EditorApplication.timeSinceStartup < _nextHeartbeat)
                return;
            _nextHeartbeat = EditorApplication.timeSinceStartup + HEARTBEAT_EVERY_SEC;
            Write();
        }

        private void Write()
        {
            AtomicFile.Write(RelayPaths.StatusFile(_projectRoot), BuildStatusJson(
                _bootId,
                EditorApplication.isPlayingOrWillChangePlaymode,
                _compileErrors,
                _compileGeneration,
                AdapterRegistry.Current.GameId,
                DateTime.UtcNow.ToString("o")));
        }
    }
}
