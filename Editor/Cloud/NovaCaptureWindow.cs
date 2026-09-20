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

        private void OnGUI()
        {
            if (!_loaded) return;

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

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Status", NovaCaptureAgent.Status);
            if (NovaCaptureAgent.CurrentRunId != null)
                EditorGUILayout.LabelField("Run", $"{NovaCaptureAgent.CurrentRunId} — item {NovaCaptureAgent.ItemIndex}/{NovaCaptureAgent.ItemTotal}");
            if (NovaCaptureAgent.LastPollUtc is { } t)
                EditorGUILayout.LabelField("Last poll", t.ToLocalTime().ToString("HH:mm:ss"));
            if (!string.IsNullOrEmpty(NovaCaptureAgent.LastError))
                EditorGUILayout.HelpBox(NovaCaptureAgent.LastError, MessageType.Warning);
            EditorGUILayout.LabelField("Kit", KitInfo.Version);
        }
    }
}
