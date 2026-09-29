using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// A6 step 10c — <c>Tools/Recorder Kit/Nova Capture</c>. The whole studio-side setup: paste the
    /// API URL and the studio key, Connect, tick "run capture jobs". The key lives in EditorPrefs
    /// (per project), never in a file under the studio's repo.
    /// </summary>
    public sealed class NovaCaptureWindow : EditorWindow
    {
        private string _baseUrl = "";
        private string _key = "";
        private bool _loaded;

        [MenuItem("Tools/Recorder Kit/Nova Capture")]
        public static void Open()
        {
            var w = GetWindow<NovaCaptureWindow>("Nova Capture");
            w.minSize = new Vector2(380, 300);
            w.Show();
        }

        private void OnEnable()
        {
            _baseUrl = NovaCaptureAgent.BaseUrl;
            _key = NovaCaptureAgent.StudioKey;
            _loaded = true;
            EditorApplication.update += Repaint;
        }

        private void OnDisable() => EditorApplication.update -= Repaint;

        private Vector2 _scroll;
        private string _leverFilter = "";

        private void OnGUI()
        {
            if (!_loaded) return;
            // kit 0.14.1 (audit H1): the start check's ask is ALWAYS visible, at the top, never scrolled away
            DrawStartAsk();
            // The whole window scrolls: a cheat search proposes ~100 lever rows (Rogue Legend, 2026-09-27), and without a
            // scroll view the rows below the window's height could not be reached, so they could not be ticked.
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            // Invariant 190 (kit 0.14.2) — THE MAIN VIEW IS THREE THINGS (the owner's approved mockup, 2026-09-29): the ask
            // (the start check's, above, and a "Show me once" request), ONE "Allow these N" box, and a one-line connection
            // summary. Everything else — the key fields, the workspace pick, the non-production tick, the poll toggle, every
            // lever row, the settle field, the hints — is folded under Details, each fold remembered per project. Nothing was
            // removed; it was only folded.
            DrawTeach();
            DrawAllowNeeded();
            DrawConnectionLine();
            if (Fold(FoldDetails, "Details", !NovaCaptureAgent.Configured, bold: false))
            {
                EditorGUI.indentLevel++;
                if (Fold(FoldConnection, "Connection", !NovaCaptureAgent.Configured)) DrawWindow();
                if (Fold(FoldSafety, "Safety — the non-production tick", false)) DrawNonProduction();
                if (Fold(FoldLevers, $"All levers ({_leverRows.Count})", false)) DrawLevers();
                else RefreshLeverRows(); // the count above stays true while folded
                if (Fold(FoldStatus, "Status", false)) DrawStatus();
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndScrollView();
        }

        // ---- invariant 190: the foldouts, remembered per project (EditorPrefs) ----------------------------------------------
        internal const string FoldDetails = "Details";
        internal const string FoldConnection = "Connection";
        internal const string FoldSafety = "Safety";
        internal const string FoldLevers = "Levers";
        internal const string FoldStatus = "Status";
        internal const string FoldTeachRecipe = "TeachRecipe";
        internal const string FoldTeachSettings = "TeachSettings";

        private static bool Fold(string name, string label, bool fallback, bool bold = true)
        {
            var open = NovaCaptureAgent.FoldoutOpen(name, fallback);
            var next = EditorGUILayout.Foldout(open, label, true, bold ? EditorStyles.foldoutHeader : EditorStyles.foldout);
            if (next != open) NovaCaptureAgent.SetFoldoutOpen(name, next);
            return next;
        }

        /// <summary>
        /// Invariant 190 — THE ONE-LINE CONNECTION SUMMARY: "Connected · &lt;game&gt; · kit &lt;v&gt;", or what is missing, in
        /// words. Pure, so a test reads what the window says.
        /// </summary>
        internal static string ConnectionSummary(bool configured, string? accountName, string? workspaceName, bool enabled, string kitVersion)
        {
            if (!configured) return "Not connected — open Details › Connection and paste the API URL and studio key";
            if (string.IsNullOrEmpty(accountName)) return $"Not connected yet — press Connect under Details › Connection · kit {kitVersion}";
            var game = string.IsNullOrEmpty(workspaceName) ? "no game picked for this project" : workspaceName;
            return $"Connected · {game} · kit {kitVersion}" + (enabled ? "" : " · capture jobs are off");
        }

        private static void DrawConnectionLine()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(ConnectionSummary(NovaCaptureAgent.Configured, NovaCaptureAgent.AccountName,
                NovaCaptureAgent.BoundWorkspaceName, NovaCaptureAgent.Enabled, KitInfo.Version), EditorStyles.wordWrappedMiniLabel);
            // what a job is doing now, and a failure, stay in the main view — never folded away
            if (NovaCaptureAgent.CurrentRunId != null || NovaCaptureAgent.Busy)
                EditorGUILayout.LabelField(NovaCaptureAgent.Status, EditorStyles.wordWrappedMiniLabel);
            if (!string.IsNullOrEmpty(NovaCaptureAgent.LastError))
                EditorGUILayout.HelpBox(NovaCaptureAgent.LastError, MessageType.Warning);
        }

        // ---- invariant 190: "Allow these N" --------------------------------------------------------------------------------
        private double _allowReadAt = double.MinValue;
        private AllowNeeded.View _allow = new();
        private string? _allowError;
        private string? _allowDone;
        /// <summary>Audit F5 — the list as the person last SAW it (the last Repaint), handed to the press.</summary>
        private AllowNeeded.View _allowShown = new();

        /// <summary>
        /// Invariant 190 — "N cheats your ads need" and ONE button. The set is the website's (<c>tick-list.json</c>
        /// <c>needed</c>), read from disk once a second, listed WHOLE (the window scrolls) — each name with its exact command
        /// under it (audit F4); the press is <see cref="AllowNeeded.AllowAll"/>, handed the list as last drawn, which reads it
        /// from disk again, writes nothing if it changed (audit F5), and otherwise writes each fixed command through the one
        /// writer, exactly as a row's tick does. A risky one is named in one sentence: it is ticked and waits at the gate until
        /// the non-production tick (Details › Safety).
        /// </summary>
        private void DrawAllowNeeded()
        {
            var root = KitProject.Root();
            if (EditorApplication.timeSinceStartup - _allowReadAt > 1.0)
            {
                try { _allow = AllowNeeded.For(root); }
                finally { _allowReadAt = EditorApplication.timeSinceStartup; }
            }
            var v = _allow;
            if (!string.IsNullOrEmpty(v.Unreadable))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("The website's cheat list could not be read: " + v.Unreadable, MessageType.Warning);
                return;
            }
            if (v.NeededTotal == 0 && _allowDone == null) return;
            EditorGUILayout.Space();
            if (AllowNeeded.Heading(v) is not { } heading)
            {
                EditorGUILayout.LabelField(_allowDone ?? $"All {v.NeededTotal} cheats your ads need are allowed.", EditorStyles.wordWrappedMiniLabel);
            }
            else
            {
                _allowDone = null; // a newer send asks for more: the last press's line is over
                EditorGUILayout.LabelField(heading, EditorStyles.boldLabel);
                // audit F4: EVERY command the press writes, its exact command under its name — nothing approved unseen
                foreach (var (title, command) in AllowNeeded.RowsOf(v))
                {
                    EditorGUILayout.LabelField(title, EditorStyles.wordWrappedLabel);
                    EditorGUI.indentLevel++;
                    EditorGUILayout.SelectableLabel(command, EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    EditorGUI.indentLevel--;
                }
                if (Event.current.type == EventType.Repaint) _allowShown = v;
                if (GUILayout.Button(AllowNeeded.ButtonLabel(v), GUILayout.Height(28)))
                {
                    // audit F5: the list the person saw — refused with "The list changed — look again." if disk moved on
                    _allowError = AllowNeeded.AllowAll(root, _allowShown.ToAllow, out var written);
                    _allowDone = _allowError == null ? $"Allowed {written} — your ads can use them now." : null;
                    _allowReadAt = double.MinValue;
                    _leversReadAt = double.MinValue;
                }
            }
            if (AllowNeeded.HeldSentence(v) is { } held)
                EditorGUILayout.HelpBox(held, MessageType.Info);
            if (!string.IsNullOrEmpty(_allowError))
                EditorGUILayout.HelpBox(_allowError, MessageType.Warning);
        }

        private static void DrawStatus()
        {
            EditorGUILayout.LabelField("Status", NovaCaptureAgent.Status);
            if (NovaCaptureAgent.CurrentRunId != null)
                EditorGUILayout.LabelField("Run", $"{NovaCaptureAgent.CurrentRunId} — item {NovaCaptureAgent.ItemIndex}/{NovaCaptureAgent.ItemTotal}");
            if (NovaCaptureAgent.LastPollUtc is { } t)
                EditorGUILayout.LabelField("Last poll", t.ToLocalTime().ToString("HH:mm:ss"));
            EditorGUILayout.LabelField("Kit", KitInfo.Version);
        }

        /// <summary>Details › Connection — the key fields, the workspace pick and the poll toggle (before invariant 190, the
        /// whole window).</summary>
        private void DrawWindow()
        {
            EditorGUILayout.LabelField("ProjectNova — capture jobs from the cockpit", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This editor records the shots the cockpit asks for and uploads the takes. " +
                "It only ever calls out; nothing connects in. Keep the editor open and unfocused-safe " +
                "(the kit forces run-in-background).",
                MessageType.None);

            EditorGUILayout.Space();
            var url = EditorGUILayout.TextField("API base URL", _baseUrl);
            if (url != _baseUrl)
            {
                _baseUrl = url;
                NovaCaptureAgent.BaseUrl = url;
            }
            var key = EditorGUILayout.PasswordField("Studio key", _key);
            if (key != _key)
            {
                _key = key;
                NovaCaptureAgent.StudioKey = key;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!NovaCaptureAgent.Configured || NovaCaptureAgent.Busy))
                {
                    if (GUILayout.Button("Connect"))
                        NovaCaptureAgent.Connect();
                }
                if (GUILayout.Button("Forget key"))
                {
                    NovaCaptureAgent.ForgetKey();
                    _key = "";
                }
            }
            if (!string.IsNullOrEmpty(NovaCaptureAgent.AccountName))
                EditorGUILayout.LabelField("Account", NovaCaptureAgent.AccountName);

            DrawWorkspaceBinding();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!NovaCaptureAgent.Configured))
            {
                var enabled = EditorGUILayout.ToggleLeft("Run capture jobs (polls every 15 s)", NovaCaptureAgent.Enabled);
                if (enabled != NovaCaptureAgent.Enabled)
                    NovaCaptureAgent.Enabled = enabled;
                using (new EditorGUI.DisabledScope(!enabled || NovaCaptureAgent.Busy))
                {
                    if (GUILayout.Button("Check for jobs now"))
                        NovaCaptureAgent.PollNow();
                }
            }

        }

        /// <summary>
        /// v3 P4 (§3.4) — "SHOW ME ONCE". When the website asked this editor to be shown a screen, the request is here with a
        /// Teach button. Teach starts a FRESH Play session (the restart rule's settle wait first) and records what each press
        /// fires until Stop — only between the two. Invariant 190 (kit 0.14.2): then THE TEACH FINISHES ITSELF — the kit runs
        /// the confirming replay once with no button (<see cref="TeachJob.NextAutomatic"/>; Cancel still stops its start before
        /// anything is pressed) and uploads a recipe the replay confirmed (by names, or one the box can confirm by picture).
        /// Only a recipe it could NOT confirm stops here, with one sentence why and the choice: Teach again · Upload anyway ·
        /// Discard (and Replay to confirm, when the automatic replay went back before pressing anything). What it recorded is
        /// under a foldout. Every button is greyed by the same rule the agent applies (<see cref="TeachJob.Refusal"/>).
        /// </summary>
        private void DrawTeach()
        {
            var t = NovaCaptureAgent.CurrentTeach;
            if (t == null) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Show me once", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox($"The website asks: “{t.Request.Ask}”", MessageType.Info);
            EditorGUILayout.LabelField("Starts from", t.Request.StartKind == TeachAnalysis.StartRecipe
                ? "another recipe — play to its end yourself, then press Teach"
                : t.Request.StartKind == TeachAnalysis.StartHere
                    ? "the screen the game is on now — press Play, go there, then Teach from here (no restart)"
                    : t.IsFromHere
                        ? "the screen the game was on when you pressed Teach from here (no restart)"
                        : "the lobby after boot — Teach restarts Play Mode; or Teach from here: the screen the game is on now",
                EditorStyles.wordWrappedLabel);
            if (Fold(FoldTeachSettings, "Teach settings", false))
            {
                var settle = EditorGUILayout.FloatField("Wait after Play ends (s)", (float)NovaCaptureAgent.TeachSettleSec);
                if (Math.Abs(settle - NovaCaptureAgent.TeachSettleSec) > 0.01) NovaCaptureAgent.TeachSettleSec = settle;
            }

            void Act(string label, TeachAction action)
            {
                using (new EditorGUI.DisabledScope(TeachJob.Refusal(t, action, EditorApplication.isPlaying, TeachRecorder.IsTeaching) != null))
                    if (GUILayout.Button(label)) NovaCaptureAgent.RequestTeachAction(action);
            }

            switch (t.Phase)
            {
                case TeachState.Waiting:
                    EditorGUILayout.LabelField("Nothing is recorded until you press Teach, and only until you press Stop.", EditorStyles.wordWrappedMiniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (t.Request.StartKind != TeachAnalysis.StartHere) Act("Teach", TeachAction.Teach);
                        // kit 0.14.1 (invariant 187): from where the game is now — the kit must already be in Play Mode
                        if (t.Request.StartKind != TeachAnalysis.StartRecipe) Act("Teach from here", TeachAction.TeachHere);
                        Act("Not now", TeachAction.NotNow);
                    }
                    if (t.Request.StartKind != TeachAnalysis.StartRecipe && !EditorApplication.isPlaying)
                        EditorGUILayout.LabelField(TeachJob.HereNeedsPlay + ".", EditorStyles.wordWrappedMiniLabel);
                    break;
                case TeachState.Recording:
                    EditorGUILayout.LabelField("Recording", $"{TeachRecorder.Presses.Count} press(es) — play to the screen, then Stop");
                    Act("Stop", TeachAction.Stop);
                    break;
                case TeachState.Review:
                    // invariant 190: the kit replays and uploads by itself; what reaches here is a recipe it could not confirm
                    EditorGUILayout.HelpBox(TeachJob.NotFinishedWhy(t, EditorApplication.isPlaying, TeachRecorder.IsTeaching, NovaCaptureAgent.EditorSession), MessageType.Warning);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        Act("Teach again", TeachAction.TeachAgain);
                        Act("Upload anyway", TeachAction.Upload);
                        Act("Discard", TeachAction.Discard);
                        // the automatic replay runs once; one that went back before pressing anything is a person's press now
                        if (t.Replay == null && t.AutoReplayed) Act("Replay to confirm", TeachAction.Replay);
                    }
                    if (Fold(FoldTeachRecipe, "What it recorded", false)) DrawTeachReview(t);
                    break;
                case TeachState.Restart:
                case TeachState.Boot:
                    EditorGUILayout.LabelField(NovaCaptureAgent.Status, EditorStyles.wordWrappedMiniLabel);
                    Act("Cancel", TeachAction.Cancel);
                    break;
                default:
                    EditorGUILayout.LabelField(NovaCaptureAgent.Status, EditorStyles.wordWrappedMiniLabel);
                    break;
            }
            if (!string.IsNullOrEmpty(t.Note)) EditorGUILayout.HelpBox(t.Note, MessageType.None);
        }

        /// <summary>
        /// Kit 0.14.1 (invariant 187) — THE START CHECK'S ASK: a Try, a recording or a teach's replay found the game on
        /// another screen than the one it starts on (a game remembers where it was left), and neither a go-home lever nor
        /// waiting fixed it. The person puts the game there and presses Continue; the run checks again. Unanswered, it stops
        /// with this sentence after <see cref="StartCheck.AskWaitSec"/> seconds.
        /// </summary>
        private static void DrawStartAsk()
        {
            if (StartAsk.Current is not { } ask) return;
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(ask, MessageType.Warning);
            if (GUILayout.Button("Continue")) StartAsk.PressContinue();
        }

        /// <summary>The recipe, as it will be sent — shown BEFORE Upload.</summary>
        private static void DrawTeachReview(TeachState t)
        {
            var a = t.Analysis();
            EditorGUILayout.HelpBox(a.Confirmed
                ? "Confirmed: one replay arrived the same way."
                : "Not confirmed: " + (a.Why ?? "no reason given"), a.Confirmed ? MessageType.Info : MessageType.Warning);
            // fix 5b (invariant 185): names cannot tell this screen apart, but the website can confirm it from the two Stop pictures
            if (a.ReplayForPicture)
                EditorGUILayout.HelpBox(a.PictureCheck
                    ? "If your account has a Claude key, the website compares the Stop picture with the replay's end picture to confirm it (one AI read on that key) — Setup says the price, or why it cannot."
                    : "Replay once: then, if your account has a Claude key, the website can confirm it by comparing the Stop picture with the replay's end picture.", MessageType.Info);
            EditorGUILayout.LabelField("Steps", a.Steps.Count == 0 ? "none" : "");
            var k = 0;
            foreach (var step in a.Steps)
            {
                k++;
                var kind = step["kind"]?.ToString() ?? "?";
                var what = step["name"]?.ToString() ?? step["command"]?.ToString()
                    ?? $"{step["x"]}, {step["y"]}";
                EditorGUILayout.LabelField($"  {k}. {kind}", what);
            }
            EditorGUILayout.LabelField("Popup closes", a.Dismiss.Count == 0 ? "none" : string.Join(", ", a.Dismiss));
            EditorGUILayout.LabelField("Arrives at", a.Arrival.Count == 0 ? "—" : string.Join(", ", a.Arrival.Take(8)));
            if (t.Cancelled > 0 || t.Dropped > 0)
                EditorGUILayout.LabelField("Not kept", $"{t.Cancelled} press(es) that fired nothing, {t.Dropped} past the cap");
            EditorGUILayout.LabelField("Sent with it", "the names on screen after each press and a small picture of each");
        }

        /// <summary>
        /// Slice D part 1 (owner decision D10) — "which game is THIS Unity project".
        ///
        /// The studio key belongs to the ACCOUNT, and an account with two games has two
        /// workspaces. Until a game is onboarded nothing on this machine says which project is
        /// which, so the studio says it here, once per project. An explicit pick, never inferred —
        /// even with a single workspace — because "this project is that game" is the statement
        /// being recorded, and the first job a new game gets ("Learn my game") is only ever handed
        /// to a project that has made it.
        /// </summary>
        private static void DrawWorkspaceBinding()
        {
            var boundId = NovaCaptureAgent.BoundWorkspaceId;
            var workspaces = NovaCaptureAgent.Workspaces;

            if (workspaces.Count == 0)
            {
                // Not connected this session (or an older server): say what is stored, change nothing.
                if (!string.IsNullOrEmpty(boundId))
                    EditorGUILayout.LabelField("Workspace",
                        string.IsNullOrEmpty(NovaCaptureAgent.BoundWorkspaceName) ? boundId : NovaCaptureAgent.BoundWorkspaceName);
                else if (!string.IsNullOrEmpty(NovaCaptureAgent.AccountName))
                    EditorGUILayout.LabelField("Workspace", "press Connect to pick the game for this project");
                if (!string.IsNullOrEmpty(NovaCaptureAgent.WorkspacesError))
                    EditorGUILayout.HelpBox(NovaCaptureAgent.WorkspacesError, MessageType.Info);
                return;
            }

            var labels = new string[workspaces.Count + 1];
            labels[0] = "— pick the game for THIS Unity project —";
            var current = 0;
            for (var i = 0; i < workspaces.Count; i++)
            {
                // '/' makes an IMGUI popup build a submenu; a workspace named "A/B" must stay one row.
                labels[i + 1] = workspaces[i].Label.Replace("/", "\u2215");
                if (workspaces[i].Id == boundId) current = i + 1;
            }
            using (new EditorGUI.DisabledScope(NovaCaptureAgent.Busy))
            {
                var picked = EditorGUILayout.Popup("Workspace", current, labels);
                if (picked != current)
                    NovaCaptureAgent.BindWorkspace(picked == 0 ? null : workspaces[picked - 1]);
            }
            if (current == 0 && !string.IsNullOrEmpty(boundId))
                EditorGUILayout.HelpBox(
                    "This project is bound to a workspace this studio key cannot see any more. Pick one again.",
                    MessageType.Warning);
            else if (current == 0)
                EditorGUILayout.HelpBox(
                    "Not picked yet. Recording still works, but \"Learn my game\" is only ever sent to a project " +
                    "that has said which game it is — so a second Unity project on the same key can never answer for this one.",
                    MessageType.Info);
        }

        /// <summary>
        /// Slice A2′ (F16) — "Levers the cloud may use".
        ///
        /// THIS WINDOW IS THE ONLY WRITER of <c>Library/Nova/levers.json</c>: a tick here is the
        /// person saying a delivered shot may make that one write in their game. The agent never
        /// writes it, a job never writes it, and nothing the server sends can.
        ///
        /// The list is computed FROM THE FILES ON DISK every time (throttled to a read a second, not
        /// a read a repaint), never remembered — so it is always the levers the files that are
        /// actually here ask for, whether they arrived from the website or were typed locally.
        ///
        /// WHAT IT SHOWS is <see cref="Levers.Rows"/>, which is three lists rather than one (second
        /// audit, M8): the levers the two files ask for, every OTHER entry of levers.json so an
        /// approval whose command has left the files can still be revoked from here, and the
        /// commands this session's gate refused that no file lists — which is the only way a studio
        /// with its own compiled adapter can ever tick its own ready gate's commands.
        ///
        /// It is only ever RENDERED here: a lever string is a thing to approve, never a thing this
        /// window runs (two of them — `timeScale`, `hide-overlay &lt;Type&gt;` — are not commands
        /// any bridge could run at all).
        /// </summary>
        /// <summary>
        /// Learn-and-drive v3 P2 (§3.1, decided §9.1 = A) — the per-project tick "this editor talks to a non-production
        /// server". THIS WINDOW IS ITS ONLY WRITER (<see cref="Levers.SetNonProduction"/>), as for every tick. Until it is
        /// ticked, a risky cheat (<see cref="CheatRisk"/>) is refused even when its own row is ticked. The hints beside it
        /// (<see cref="NonProductionHints"/>) are SHOWN, never acted on: nothing reads them but this drawing.
        /// </summary>
        private double _nonProductionReadAt = double.MinValue;
        private bool _nonProduction;
        private System.Collections.Generic.IReadOnlyList<string> _nonProductionHints = System.Array.Empty<string>();
        private string? _nonProductionError;

        private void DrawNonProduction()
        {
            var root = KitProject.Root();
            var now = EditorApplication.timeSinceStartup;
            if (now - _nonProductionReadAt > 1.0)
            {
                try
                {
                    _nonProduction = Levers.NonProductionTicked(root);
                    _nonProductionHints = NonProductionHints.FromDefines(ScriptingDefines(), EditorUserBuildSettings.development);
                }
                finally { _nonProductionReadAt = EditorApplication.timeSinceStartup; }
            }

            EditorGUILayout.Space();
            var next = EditorGUILayout.ToggleLeft(char.ToUpperInvariant(CheatRisk.NonProductionLabel[0]) +
                                                  CheatRisk.NonProductionLabel.Substring(1), _nonProduction);
            EditorGUILayout.LabelField(" ",
                "Until this is ticked, risky cheats (reset, give, delete, purchase, server, account…) are refused even " +
                "when their own row is ticked. Tick it only if this editor's game talks to a test/dev backend, never to " +
                "your live players.", EditorStyles.wordWrappedMiniLabel);
            foreach (var hint in _nonProductionHints)
                EditorGUILayout.LabelField(" ", "hint (not acted on): " + hint, EditorStyles.wordWrappedMiniLabel);
            if (next != _nonProduction)
            {
                _nonProductionError = Levers.SetNonProduction(root, next);
                if (_nonProductionError == null) _nonProduction = next;
                _nonProductionReadAt = double.MinValue;
                _leversReadAt = double.MinValue; // the rows' risky notes change with it
                _allowReadAt = double.MinValue; // …and so does what "Allow these N" says is held
            }
            if (!string.IsNullOrEmpty(_nonProductionError))
                EditorGUILayout.HelpBox(_nonProductionError, MessageType.Warning);
        }

        private static string[] ScriptingDefines()
        {
            try
            {
                var target = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
                return PlayerSettings.GetScriptingDefineSymbols(target)
                    .Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries);
            }
            catch (System.Exception)
            {
                return System.Array.Empty<string>();
            }
        }

        private double _leversReadAt = double.MinValue;
        private System.Collections.Generic.IReadOnlyList<Levers.LeverRow> _leverRows =
            System.Array.Empty<Levers.LeverRow>();
        private string? _leversError;

        /// <summary>What this window is about to draw, for the project this Editor has open. Pure
        /// enough to be read by a test — the IMGUI below is not.</summary>
        internal static System.Collections.Generic.IReadOnlyList<Levers.LeverRow> RowsForThisProject() =>
            Levers.Rows(KitProject.Root(), LeverGateBridge.RefusedThisSession);

        /// <summary>The rows, read at most once a second (the folded Details header shows their count too).</summary>
        private void RefreshLeverRows()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - _leversReadAt > 1.0)
            {
                // Stamped AFTER the read (a slow read is not redone on the very next repaint), and in
                // a `finally` (a read that throws is retried once a second, not on every repaint).
                try { _leverRows = RowsForThisProject(); }
                finally { _leversReadAt = EditorApplication.timeSinceStartup; }
            }
        }

        private void DrawLevers()
        {
            // The project this Editor has open — never the working directory, which is process-wide
            // state something else can move while a job is running (audit M4).
            var root = KitProject.Root();
            RefreshLeverRows();
            if (_leverRows.Count == 0)
            {
                EditorGUILayout.LabelField("No lever is asked for yet.", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Levers the cloud may use", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Tick a lever and a shot sent from the website may make that write in your game. " +
                "Untick it and the shot stops with \"lever not approved on this machine\". " +
                "Most levers are cheat commands. \"timeScale\" is any delivered shot changing the " +
                "game's speed, \"hide-overlay <Type>\" is a delivered adapter.json disabling " +
                "that MonoBehaviour during capture, and \"camera-spec …\" is which methods it moves " +
                "your camera with. Shots you wrote on this machine are never gated. Only FIXED commands can be " +
                "ticked — a sent command runs only if it is exactly one you ticked. Rows \"proposed by the website\" " +
                "come from its cheat list and run nothing until you tick them.",
                MessageType.None);
            // Find a row by any part of its command (case-insensitive) — ticked rows always stay listed.
            _leverFilter = EditorGUILayout.TextField("Find", _leverFilter ?? "");
            var shown = LeverFilter.Visible(_leverRows.Select(r => (r.Command, r.Approved)), _leverFilter);
            if (!string.IsNullOrWhiteSpace(_leverFilter))
                EditorGUILayout.LabelField(" ", $"showing {shown.Count} of {_leverRows.Count}", EditorStyles.miniLabel);
            foreach (var row in _leverRows)
            {
                if (!shown.Contains(row.Command)) continue;
                var on = row.Approved;
                bool next;
                using (new EditorGUI.DisabledScope(!row.CanTick))
                    next = EditorGUILayout.ToggleLeft(row.Command, on);
                // Fix 4 audit, M2 (invariant 182): the one template a person ticks says what it grants, where it is ticked
                if (Levers.SetValueGrantLine(row.Command) is { } grant)
                    EditorGUILayout.LabelField(" ", grant, EditorStyles.wordWrappedMiniLabel);
                if (!string.IsNullOrEmpty(row.Note))
                    EditorGUILayout.LabelField(" ", row.Note, EditorStyles.wordWrappedMiniLabel);
                if (next == on || !row.CanTick) continue;
                _leversError = Levers.SetApproved(root, row.Command, next);
                // v3 P3: the check value's read is ticked WITH the cheat (never unticked by it)
                if (_leversError == null && next)
                    foreach (var companion in Levers.CompanionTicks(root, row.Command))
                        _leversError ??= Levers.SetApproved(root, companion, true);
                if (_leversError == null)
                {
                    row.Approved = next;
                    // Re-read on the next repaint: unticking a row nothing asks for takes it off
                    // this list entirely.
                    _leversReadAt = double.MinValue;
                    _allowReadAt = double.MinValue;
                }
            }
            if (!string.IsNullOrEmpty(_leversError))
                EditorGUILayout.HelpBox(_leversError, MessageType.Warning);
        }
    }
}
