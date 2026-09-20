using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Kit verbs <c>camera-pose</c> / <c>camera-release</c>: parse a static framing, snapshot
    /// the live camera, hold it via <c>RenderPipelineManager.beginCameraRendering</c> on the
    /// posed camera only (SetRotation then SetPosition then SetFov).
    ///
    /// Why a dedicated verb rather than <c>call CameraView.SetPosition</c>: generic call cannot
    /// coerce Vector3, and unarmed writes lose the fight with follow/pinch by the next frame.
    /// Why not <c>FindFirstObjectByType&lt;Camera&gt;()</c>: order is not contractual (UiVfxCamera
    /// won that lookup). Why not <c>InstanceOf</c>: same first-hit rule, and click-duplicate
    /// CameraViews must refuse rather than pose a random one.
    /// Why <c>beginCameraRendering</c> not LateUpdate: Editor-asmdef MonoBehaviour tick was
    /// unverified; this hook is last-writer before cull and ignores timeScale.
    /// </summary>
    public static class CameraPose
    {
        static Action<ScriptableRenderContext, Camera>? _handler;
        static Action<Action<ScriptableRenderContext, Camera>>? _unsubscribe;
        static Func<object, Parsed, CameraAdapter, string?>? _apply;
        static object? _view;
        static Camera? _posedCamera;
        static Parsed _target;
        static CameraAdapter _spec;
        static Snapshot? _armedSnap;
        static bool _playHooked;
        static bool _applyWarned;

        internal static bool HoldArmed => _handler != null && _posedCamera != null;
        public const string SnapshotFileName = "camera-hold.json";

        public readonly struct Parsed
        {
            public string? TypeName { get; }
            public Vector3 Position { get; }
            public Vector3 Euler { get; }
            public float Fov { get; }

            public Parsed(string? typeName, Vector3 position, Vector3 euler, float fov)
            {
                TypeName = typeName;
                Position = position;
                Euler = euler;
                Fov = fov;
            }
        }

        public readonly struct Snapshot
        {
            public Vector3 Position { get; }
            public Vector3 Euler { get; }
            public float Fov { get; }
            public string ViewType { get; }

            public Snapshot(Vector3 position, Vector3 euler, float fov, string viewType)
            {
                Position = position;
                Euler = euler;
                Fov = fov;
                ViewType = viewType ?? "";
            }
        }

        public readonly struct Outcome
        {
            public bool Ok { get; }
            public string Error { get; }
            public string[] Lines { get; }

            public static Outcome Success(params string[] lines) =>
                new(true, "", lines ?? Array.Empty<string>());

            public static Outcome Fail(string error) =>
                new(false, error ?? "", Array.Empty<string>());

            private Outcome(bool ok, string error, string[] lines)
            {
                Ok = ok;
                Error = error;
                Lines = lines;
            }
        }

        public sealed class Host
        {
            public string ProjectRoot { get; }
            public Func<bool> IsPlaying { get; }
            public Func<string, Type?> FindType { get; }
            public Func<Type, object[]> FindInstances { get; }
            public Func<AdapterJson> LoadAdapter { get; }
            public Func<object, Snapshot?> ReadLive { get; }
            public Action<Action<ScriptableRenderContext, Camera>> SubscribeBegin { get; }
            public Action<Action<ScriptableRenderContext, Camera>> UnsubscribeBegin { get; }
            public Func<object, Camera?> ResolveCamera { get; }
            public Func<object, Parsed, CameraAdapter, string?> Apply { get; }
            public Func<object, Snapshot, CameraAdapter, Vector3, string?> RestoreRotation { get; }

            public Host(
                string projectRoot,
                Func<bool>? isPlaying = null,
                Func<string, Type?>? findType = null,
                Func<Type, object[]>? findInstances = null,
                Func<AdapterJson>? loadAdapter = null,
                Func<object, Snapshot?>? readLive = null,
                Action<Action<ScriptableRenderContext, Camera>>? subscribeBegin = null,
                Action<Action<ScriptableRenderContext, Camera>>? unsubscribeBegin = null,
                Func<object, Camera?>? resolveCamera = null,
                Func<object, Parsed, CameraAdapter, string?>? apply = null,
                Func<object, Snapshot, CameraAdapter, Vector3, string?>? restoreRotation = null)
            {
                ProjectRoot = projectRoot;
                IsPlaying = isPlaying ?? (() => Application.isPlaying);
                FindType = findType ?? GameReflection.FindType;
                FindInstances = findInstances ?? DefaultFindInstances;
                LoadAdapter = loadAdapter ?? (() => AdapterJson.Load(projectRoot));
                ReadLive = readLive ?? DefaultReadLive;
                SubscribeBegin = subscribeBegin ?? DefaultSubscribe;
                UnsubscribeBegin = unsubscribeBegin ?? DefaultUnsubscribe;
                ResolveCamera = resolveCamera ?? DefaultResolveCamera;
                Apply = apply ?? DefaultApply;
                RestoreRotation = restoreRotation ?? DefaultRestoreRotation;
            }

            public static Host ForProject(string projectRoot) => new(projectRoot);
        }

        /// <summary>
        /// CLI is <c>[Type] x y z pitch yaw roll fov</c>. Pitch-yaw-roll is Unity's
        /// <c>Quaternion.Euler</c> argument order. Probe-2 board euler (40.84, 135, 0)
        /// is the fixture: treating those tokens as yaw-then-pitch builds a different pose.
        /// </summary>
        public static bool TryParse(string[] args, out Parsed pose, out string error)
        {
            pose = default;
            error = "";
            if (args == null || (args.Length != 7 && args.Length != 8))
            {
                error = "camera-pose needs 7 numbers (x y z pitch yaw roll fov), optional Type first"
                        + (args == null ? "" : $"; got {args.Length} argument(s)");
                return false;
            }

            string? typeName = null;
            var nums = args;
            if (args.Length == 8)
            {
                typeName = args[0];
                nums = new[] { args[1], args[2], args[3], args[4], args[5], args[6], args[7] };
            }

            if (!TryFloat(nums[0], out var x) || !TryFloat(nums[1], out var y) || !TryFloat(nums[2], out var z)
                || !TryFloat(nums[3], out var pitch) || !TryFloat(nums[4], out var yaw) || !TryFloat(nums[5], out var roll)
                || !TryFloat(nums[6], out var fov))
            {
                error = "x y z pitch yaw roll fov must be numbers";
                return false;
            }

            pose = new Parsed(typeName, new Vector3(x, y, z), new Vector3(pitch, yaw, roll), fov);
            return true;
        }

        public static string SnapshotPath(string projectRoot) =>
            Path.Combine(RelayPaths.Root(projectRoot), SnapshotFileName);

        public static void WriteSnapshot(string projectRoot, Snapshot snap)
        {
            var obj = new JObject
            {
                ["viewType"] = snap.ViewType,
                ["position"] = Vec(snap.Position),
                ["euler"] = Vec(snap.Euler),
                ["fov"] = snap.Fov,
            };
            AtomicFile.Write(SnapshotPath(projectRoot), obj.ToString());
        }

        public static bool TryReadSnapshot(string projectRoot, out Snapshot snap, out string error)
        {
            snap = default;
            error = "";
            var path = SnapshotPath(projectRoot);
            if (!File.Exists(path))
            {
                error = "no camera-hold.json";
                return false;
            }
            try
            {
                var obj = JObject.Parse(File.ReadAllText(path));
                snap = new Snapshot(
                    ReadVec(obj["position"]),
                    ReadVec(obj["euler"]),
                    obj["fov"]?.Value<float>() ?? 0f,
                    (string?)obj["viewType"] ?? "");
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        public static Outcome Pose(string[] args, Host host)
        {
            if (!TryParse(args, out var parsed, out var parseErr))
                return Outcome.Fail(parseErr);
            if (!host.IsPlaying())
                return Outcome.Fail("Play Mode required — posing the camera in Edit Mode is illegal");

            var adapter = host.LoadAdapter();
            var cam = adapter.Camera;
            if (cam == null)
                return Outcome.Fail("adapter.json camera.viewType required");
            var spec = cam.Value;
            var typeName = parsed.TypeName ?? spec.ViewType;
            if (string.IsNullOrWhiteSpace(typeName))
                return Outcome.Fail("adapter.json camera.viewType required");
            var resolvedType = typeName!;
            if (string.IsNullOrWhiteSpace(spec.Projection))
                return Outcome.Fail("adapter.json camera.projection required");
            if (!string.Equals(spec.Projection, "perspective", StringComparison.OrdinalIgnoreCase))
                return Outcome.Fail("setFov is refused when projection is not perspective (orthographic fieldOfView is a no-op on the picture)");

            var type = host.FindType(resolvedType);
            if (type == null)
                return Outcome.Fail($"no loaded type named '{resolvedType}'. If the name is right it may be ambiguous across assemblies — pass the fully-qualified Namespace.Type.");
            if (!typeof(Component).IsAssignableFrom(type))
                return Outcome.Fail($"{type.Name} is not a Component — camera.viewType must be a Component");

            var found = host.FindInstances(type) ?? Array.Empty<object>();
            if (found.Length != 1)
                return Outcome.Fail($"expected exactly 1 live {resolvedType} (inactive excluded), found {found.Length}");

            var posedCam = host.ResolveCamera(found[0]);
            if (posedCam == null)
                return Outcome.Fail($"could not resolve Camera on {resolvedType}");

            var path = SnapshotPath(host.ProjectRoot);
            Snapshot armedSnap;
            var captureLive = !TryReadSnapshot(host.ProjectRoot, out armedSnap, out _);
            if (captureLive)
            {
                var live = host.ReadLive(found[0]);
                if (live == null)
                    return Outcome.Fail($"could not read live pose from {resolvedType}");
                armedSnap = live.Value;
            }

            string? applyErr;
            try { applyErr = host.Apply(found[0], parsed, spec); }
            catch (Exception e) { applyErr = e.Message; }
            if (applyErr is { Length: > 0 })
                return Outcome.Fail(applyErr);

            if (captureLive)
                WriteSnapshot(host.ProjectRoot, armedSnap);

            ArmHold(host, found[0], posedCam, parsed, spec, armedSnap);
            return Outcome.Success(
                $"snapshot at {path}; holding pos {parsed.Position} euler ({parsed.Euler.x}, {parsed.Euler.y}, {parsed.Euler.z}) fov {parsed.Fov}");
        }

        public static Outcome Release(Host host)
        {
            var view = _view;
            var spec = _spec;
            var snap = _armedSnap;
            var path = SnapshotPath(host.ProjectRoot);
            if (snap == null && TryReadSnapshot(host.ProjectRoot, out var fileSnap, out _))
                snap = fileSnap;

            if (view == null && snap == null && !File.Exists(path))
                return Outcome.Success("camera-release: nothing armed");

            var restored = false;
            if (host.IsPlaying() && Alive(view) && snap != null)
            {
                Vector3 current;
                try
                {
                    current = host.ReadLive(view!)?.Position
                              ?? (view is Component c && c != null ? c.transform.position : snap.Value.Position);
                }
                catch (Exception e)
                {
                    return Outcome.Fail($"restore rotation failed: {e.Message}");
                }

                string? restoreErr;
                try { restoreErr = host.RestoreRotation(view!, snap.Value, spec, current); }
                catch (Exception e)
                {
                    return Outcome.Fail($"restore rotation failed: {e.Message}");
                }
                if (restoreErr is { Length: > 0 })
                    return Outcome.Fail(restoreErr);
                restored = true;
            }

            DisarmHold();
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException e)
                {
                    return Outcome.Fail($"could not delete {SnapshotFileName}: {e.Message}");
                }
            }
            if (!restored && view == null && snap == null)
                return Outcome.Success("camera-release: nothing armed");
            return Outcome.Success(restored
                ? "camera-release: rotation restored, snapshot cleared"
                : "camera-release: snapshot cleared");
        }

        /// <summary>
        /// Session leftover: <c>camera-hold.json</c> on disk but the hold is not armed
        /// (Play re-enter, first ping after an abort). Does not touch an armed hold —
        /// stills stay posed until the director or an explicit <c>camera-release</c>.
        /// </summary>
        public static Outcome SweepOrphaned(Host host)
        {
            if (HoldArmed)
                return Outcome.Success("camera-hold still armed");
            var path = SnapshotPath(host.ProjectRoot);
            if (!File.Exists(path))
                return Outcome.Success("camera-release: nothing armed");
            return Release(host);
        }

        /// <summary>Drop the RP handler without deleting the snapshot file. Play Mode exit uses this.</summary>
        internal static void ResetForTests() => DisarmHold();

        private static void ArmHold(
            Host host, object view, Camera posed, Parsed target, CameraAdapter spec, Snapshot snap)
        {
            _view = view;
            _posedCamera = posed;
            _target = target;
            _spec = spec;
            _armedSnap = snap;
            _apply = host.Apply;
            _unsubscribe = host.UnsubscribeBegin;
            _applyWarned = false;
            if (_handler == null)
            {
                _handler = OnBeginCameraRendering;
                host.SubscribeBegin(_handler);
            }
        }

        private static void DisarmHold()
        {
            if (_handler != null && _unsubscribe != null)
                _unsubscribe(_handler);
            _handler = null;
            _unsubscribe = null;
            _apply = null;
            _view = null;
            _posedCamera = null;
            _armedSnap = null;
            _applyWarned = false;
        }

        private static bool Alive(object? view)
        {
            if (view == null) return false;
            return view is not Component c || c != null;
        }

        private static void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera camera)
        {
            if (camera != _posedCamera || _view == null || _apply == null)
                return;
            try
            {
                var err = _apply(_view, _target, _spec);
                if (err is { Length: > 0 } && !_applyWarned)
                {
                    _applyWarned = true;
                    Debug.LogWarning($"[RecorderKit] camera-pose hold apply failed: {err}");
                }
            }
            catch (Exception e)
            {
                if (_applyWarned) return;
                _applyWarned = true;
                Debug.LogWarning($"[RecorderKit] camera-pose hold apply threw: {e.Message}");
            }
        }

        private static void DefaultSubscribe(Action<ScriptableRenderContext, Camera> handler)
        {
            RenderPipelineManager.beginCameraRendering += handler;
            if (_playHooked) return;
            _playHooked = true;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void DefaultUnsubscribe(Action<ScriptableRenderContext, Camera> handler) =>
            RenderPipelineManager.beginCameraRendering -= handler;

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
                DisarmHold();
        }

        /// <summary>
        /// Hold filter is the named Camera member or the component itself — not a child
        /// in the URP stack (applying when a VFX/UI camera begins would pose after Main
        /// has already drawn).
        /// </summary>
        internal static Camera? DefaultResolveCamera(object instance)
        {
            if (instance is not Component c)
                return null;
            return NamedCamera(c) ?? (c as Camera) ?? c.GetComponent<Camera>();
        }

        internal static string? DefaultApply(object view, Parsed parsed, CameraAdapter spec)
        {
            var type = view.GetType();
            var rot = Quaternion.Euler(parsed.Euler);
            if (!TryInvoke(type, spec.SetRotation, view, rot, out var err))
                return err;
            if (!TryInvoke(type, spec.SetPosition, view, parsed.Position, out err))
                return err;
            if (!TryInvoke(type, spec.SetFov, view, parsed.Fov, out err))
                return err;
            return null;
        }

        internal static string? DefaultRestoreRotation(
            object view, Snapshot snap, CameraAdapter spec, Vector3 currentPosition)
        {
            var type = view.GetType();
            var rot = Quaternion.Euler(snap.Euler);
            if (!TryInvoke(type, spec.SetRotation, view, rot, out var err))
                return err;
            if (!TryInvoke(type, spec.SetPosition, view, currentPosition, out err))
                return err;
            return null;
        }

        private static bool TryInvoke(Type type, string method, object view, object arg, out string error)
        {
            try
            {
                if (GameReflection.TryCallRaw(type, method, view, new[] { arg }, out _, out error))
                    return true;
                if (string.IsNullOrEmpty(error))
                    error = $"{type.Name}.{method} failed";
                return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static object[] DefaultFindInstances(Type type) =>
            Object.FindObjectsByType(type, FindObjectsInactive.Exclude, FindObjectsSortMode.None);

        /// <summary>
        /// Named Camera member first (property/field Camera, camera, _camera), then the
        /// component itself / children. Hierarchy order is not the adapter contract — a
        /// project/VFX child Camera would otherwise snapshot the wrong FOV.
        /// </summary>
        internal static Snapshot? DefaultReadLive(object instance)
        {
            if (instance is not Component c)
                return null;
            var cam = CameraOf(c);
            if (cam == null)
                return null;
            return new Snapshot(c.transform.position, c.transform.eulerAngles, cam.fieldOfView, instance.GetType().Name);
        }

        private static Camera? CameraOf(Component c) =>
            NamedCamera(c) ?? (c as Camera) ?? c.GetComponent<Camera>() ?? c.GetComponentInChildren<Camera>();

        private static Camera? NamedCamera(Component c)
        {
            // DeclaredOnly, and stop before Component: Component.camera is a deprecated
            // getter that throws NotSupportedException (Unity 6).
            const BindingFlags declared =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (var t = c.GetType();
                 t != null && t != typeof(MonoBehaviour) && t != typeof(Behaviour)
                 && t != typeof(Component) && t != typeof(Object);
                 t = t.BaseType)
            {
                foreach (var name in new[] { "Camera", "camera", "_camera" })
                {
                    if (t.GetProperty(name, declared)?.GetValue(c) is Camera fromProp)
                        return fromProp;
                    if (t.GetField(name, declared)?.GetValue(c) is Camera fromField)
                        return fromField;
                }
            }
            return null;
        }

        private static bool TryFloat(string raw, out float value)
        {
            if (GameReflection.TryCoerce(raw, typeof(float), out var boxed) && boxed is float f)
            {
                value = f;
                return true;
            }
            value = 0;
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static JObject Vec(Vector3 v) =>
            new() { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };

        private static Vector3 ReadVec(JToken? token)
        {
            if (token is not JObject o)
                return Vector3.zero;
            return new Vector3(
                o["x"]?.Value<float>() ?? 0f,
                o["y"]?.Value<float>() ?? 0f,
                o["z"]?.Value<float>() ?? 0f);
        }
    }
}
