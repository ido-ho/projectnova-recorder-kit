using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Parse, snapshot file, refuse paths, and injectable <c>beginCameraRendering</c> hold
    /// for <c>camera-pose</c> / <c>camera-release</c>. Live SNL tick-rate is not this suite.
    /// </summary>
    public class CameraPoseTests
    {
        private string _root = null!;
        private GameObject _camGo = null!;
        private Camera _cam = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "novakit-camerapose-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
            _camGo = new GameObject("pose-cam");
            _cam = _camGo.AddComponent<Camera>();
            _cam.fieldOfView = 20f;
        }

        [TearDown]
        public void TearDown()
        {
            CameraPose.ResetForTests();
            if (_camGo != null)
                UnityEngine.Object.DestroyImmediate(_camGo);
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        /// <summary>
        /// CLI order is pitch yaw roll, matching <c>Quaternion.Euler(pitch, yaw, roll)</c>.
        /// Probe-2 board euler is (40.84, 135, 0). The old prompt wrote yaw pitch roll; a
        /// parser that still reads that way would build Euler(135, 40.84, 0) and this
        /// fixture would silently "succeed" against the wrong rotation.
        /// </summary>
        [Test]
        public void Parse_PitchYawRoll_MatchesUnityEuler_NotTheOldYawPitchPrompt()
        {
            var expected = Quaternion.Euler(40.84f, 135f, 0f);
            var transposed = Quaternion.Euler(135f, 40.84f, 0f);
            Assert.Greater(Quaternion.Angle(expected, transposed), 10f,
                "probe-2 (40.84, 135, 0) must not be silently equal to a yaw/pitch swap");

            Assert.IsTrue(
                CameraPose.TryParse(
                    new[] { "1", "2", "3", "40.84", "135", "0", "20" },
                    out var pose, out var err),
                err);
            Assert.AreEqual(1f, pose.Position.x, 0.0001f);
            Assert.AreEqual(2f, pose.Position.y, 0.0001f);
            Assert.AreEqual(3f, pose.Position.z, 0.0001f);
            Assert.AreEqual(20f, pose.Fov, 0.0001f);
            Assert.IsNull(pose.TypeName);

            var got = Quaternion.Euler(pose.Euler.x, pose.Euler.y, pose.Euler.z);
            Assert.Less(Quaternion.Angle(got, expected), 0.2f,
                $"parsed Euler ({pose.Euler.x}, {pose.Euler.y}, {pose.Euler.z}) must match Quaternion.Euler(40.84, 135, 0)");
            Assert.Greater(Quaternion.Angle(got, transposed), 10f);
        }

        [Test]
        public void Parse_OptionalType_ThenNumbers()
        {
            Assert.IsTrue(
                CameraPose.TryParse(
                    new[] { "CameraView", "1", "2", "3", "40.84", "135", "0", "14" },
                    out var pose, out var err),
                err);
            Assert.AreEqual("CameraView", pose.TypeName);
            Assert.AreEqual(14f, pose.Fov, 0.0001f);
            Assert.AreEqual(40.84f, pose.Euler.x, 0.0001f);
            Assert.AreEqual(135f, pose.Euler.y, 0.0001f);
        }

        [Test]
        public void Parse_WrongArity_Fails()
        {
            Assert.IsFalse(CameraPose.TryParse(new[] { "1", "2", "3" }, out _, out var err));
            StringAssert.Contains("pitch yaw roll", err);
        }

        [Test]
        public void Parse_NonNumeric_Fails()
        {
            Assert.IsFalse(CameraPose.TryParse(
                new[] { "1", "2", "3", "pitch", "135", "0", "20" }, out _, out var err));
            StringAssert.Contains("must be numbers", err);
        }

        [Test]
        public void Parse_DecimalPoint_IsCultureInvariant()
        {
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.IsTrue(
                    CameraPose.TryParse(
                        new[] { "1", "2", "3", "40.84", "135", "0", "20" },
                        out var pose, out var err),
                    err);
                Assert.AreEqual(40.84f, pose.Euler.x, 0.0001f);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        [Test]
        public void Snapshot_RoundTripsUnderTempAdRelay()
        {
            var snap = new CameraPose.Snapshot(
                new Vector3(10.5f, 20.25f, 30f),
                new Vector3(40.84f, 135f, 0f),
                20f,
                "CameraView");
            CameraPose.WriteSnapshot(_root, snap);

            var path = CameraPose.SnapshotPath(_root);
            StringAssert.Contains("Library", path.Replace('\\', '/'));
            StringAssert.Contains("AdRelay", path.Replace('\\', '/'));
            Assert.IsTrue(File.Exists(path));

            Assert.IsTrue(CameraPose.TryReadSnapshot(_root, out var loaded, out var err), err);
            Assert.AreEqual("CameraView", loaded.ViewType);
            Assert.AreEqual(10.5f, loaded.Position.x, 0.0001f);
            Assert.AreEqual(20.25f, loaded.Position.y, 0.0001f);
            Assert.AreEqual(30f, loaded.Position.z, 0.0001f);
            Assert.AreEqual(40.84f, loaded.Euler.x, 0.0001f);
            Assert.AreEqual(135f, loaded.Euler.y, 0.0001f);
            Assert.AreEqual(0f, loaded.Euler.z, 0.0001f);
            Assert.AreEqual(20f, loaded.Fov, 0.0001f);
        }

        [Test]
        public void Release_WithNoFile_IsOk()
        {
            var result = CameraPose.Release(Host(playing: true));
            Assert.IsTrue(result.Ok, result.Error);
            Assert.IsFalse(File.Exists(CameraPose.SnapshotPath(_root)));
        }

        [Test]
        public void Release_DeletesSnapshotFile()
        {
            CameraPose.WriteSnapshot(_root, new CameraPose.Snapshot(
                Vector3.zero, new Vector3(40.84f, 135f, 0f), 20f, "CameraView"));
            Assert.IsTrue(File.Exists(CameraPose.SnapshotPath(_root)));

            var result = CameraPose.Release(Host(playing: true));
            Assert.IsTrue(result.Ok, result.Error);
            Assert.IsFalse(File.Exists(CameraPose.SnapshotPath(_root)));
        }

        [Test]
        public void SweepOrphaned_NoFile_IsOk()
        {
            var result = CameraPose.SweepOrphaned(Host(playing: true));
            Assert.IsTrue(result.Ok, result.Error);
            Assert.IsFalse(File.Exists(CameraPose.SnapshotPath(_root)));
        }

        [Test]
        public void SweepOrphaned_DeletesLeftoverJsonWhenNotArmed()
        {
            CameraPose.WriteSnapshot(_root, new CameraPose.Snapshot(
                Vector3.zero, new Vector3(40.84f, 135f, 0f), 20f, "CameraView"));
            Assert.IsFalse(CameraPose.HoldArmed);
            var result = CameraPose.SweepOrphaned(Host(playing: true));
            Assert.IsTrue(result.Ok, result.Error);
            Assert.IsFalse(File.Exists(CameraPose.SnapshotPath(_root)),
                "leftover camera-hold.json after an abort must not survive ping / Play enter");
        }

        [Test]
        public void SweepOrphaned_LeavesArmedHoldAndFileAlone()
        {
            WritePerspectiveAdapter();
            var posed = CameraPose.Pose(
                new[] { "1", "2", "3", "40.84", "135", "0", "20" },
                Host(
                    playing: true,
                    findType: _ => typeof(Camera),
                    instances: new object[] { new object() },
                    readLive: _ => new CameraPose.Snapshot(
                        Vector3.zero, new Vector3(40.84f, 135f, 0f), 20f, "CameraView")));
            Assert.IsTrue(posed.Ok, posed.Error);
            Assert.IsTrue(CameraPose.HoldArmed);
            Assert.IsTrue(File.Exists(CameraPose.SnapshotPath(_root)));

            var sweep = CameraPose.SweepOrphaned(Host(playing: true));
            Assert.IsTrue(sweep.Ok, sweep.Error);
            Assert.IsTrue(CameraPose.HoldArmed, "stills stay posed — ping must not drop an armed hold");
            Assert.IsTrue(File.Exists(CameraPose.SnapshotPath(_root)));
        }

        [Test]
        public void Pose_MissingAdapterCamera_Refuses()
        {
            WriteAdapter(@"{""gameId"":""demo""}");
            var result = CameraPose.Pose(
                new[] { "1", "2", "3", "40.84", "135", "0", "20" },
                Host(playing: true));
            Assert.IsFalse(result.Ok);
            StringAssert.Contains("camera.viewType required", result.Error);
        }

        [Test]
        public void Pose_MissingViewType_RefusesEvenWhenCameraBlockExists()
        {
            WriteAdapter(@"{""camera"":{""projection"":""perspective""}}");
            var result = CameraPose.Pose(
                new[] { "1", "2", "3", "40.84", "135", "0", "20" },
                Host(playing: true));
            Assert.IsFalse(result.Ok);
            StringAssert.Contains("camera.viewType required", result.Error);
        }

        [Test]
        public void Pose_NonPerspectiveFov_Refuses()
        {
            WriteAdapter(@"{""camera"":{""viewType"":""PixelCam"",""projection"":""orthographic""}}");
            var result = CameraPose.Pose(
                new[] { "1", "2", "3", "40.84", "135", "0", "20" },
                Host(playing: true, findType: _ => typeof(Camera), instances: new object[] { new object() }));
            Assert.IsFalse(result.Ok);
            StringAssert.Contains("perspective", result.Error);
        }

        [Test]
        public void Pose_NotPlaying_Refuses()
        {
            WritePerspectiveAdapter();
            var result = CameraPose.Pose(
                new[] { "1", "2", "3", "40.84", "135", "0", "20" },
                Host(playing: false, findType: _ => typeof(Camera), instances: new object[] { new object() }));
            Assert.IsFalse(result.Ok);
            StringAssert.Contains("Play Mode", result.Error);
        }

        [Test]
        public void Pose_CountNotOne_Refuses()
        {
            WritePerspectiveAdapter();
            var none = CameraPose.Pose(
                new[] { "1", "2", "3", "40.84", "135", "0", "20" },
                Host(playing: true, findType: _ => typeof(Camera), instances: Array.Empty<object>()));
            Assert.IsFalse(none.Ok);
            StringAssert.Contains("exactly 1", none.Error);

            var two = CameraPose.Pose(
                new[] { "1", "2", "3", "40.84", "135", "0", "20" },
                Host(playing: true, findType: _ => typeof(Camera),
                    instances: new object[] { new object(), new object() }));
            Assert.IsFalse(two.Ok);
            StringAssert.Contains("exactly 1", two.Error);
            StringAssert.Contains("2", two.Error);
        }

        [Test]
        public void Pose_MissingProjection_Refuses()
        {
            WriteAdapter(@"{""camera"":{""viewType"":""CameraView""}}");
            var result = CameraPose.Pose(
                new[] { "1", "2", "3", "40.84", "135", "0", "20" },
                Host(playing: true));
            Assert.IsFalse(result.Ok);
            StringAssert.Contains("camera.projection required", result.Error);
        }

        [Test]
        public void Pose_CliType_OverridesAdapterViewType()
        {
            WritePerspectiveAdapter();
            string? asked = null;
            var live = new CameraPose.Snapshot(
                Vector3.zero, new Vector3(40.84f, 135f, 0f), 20f, "OtherView");
            var result = CameraPose.Pose(
                new[] { "OtherView", "1", "2", "3", "40.84", "135", "0", "20" },
                Host(
                    playing: true,
                    findType: name =>
                    {
                        asked = name;
                        return typeof(Camera);
                    },
                    instances: new object[] { new object() },
                    readLive: _ => live));
            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual("OtherView", asked);
        }

        [Test]
        public void Pose_CorruptSnapshot_RewritesFromLive()
        {
            WritePerspectiveAdapter();
            var path = CameraPose.SnapshotPath(_root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{");
            var live = new CameraPose.Snapshot(
                new Vector3(9, 8, 7), new Vector3(40.84f, 135f, 0f), 20f, "CameraView");
            var result = CameraPose.Pose(
                new[] { "1", "2", "3", "10", "20", "0", "14" },
                Host(
                    playing: true,
                    findType: _ => typeof(Camera),
                    instances: new object[] { new object() },
                    readLive: _ => live));
            Assert.IsTrue(result.Ok, result.Error);
            Assert.IsTrue(CameraPose.TryReadSnapshot(_root, out var loaded, out var err), err);
            Assert.AreEqual(9f, loaded.Position.x, 0.0001f);
            Assert.AreEqual(20f, loaded.Fov, 0.0001f);
        }

        [Test]
        public void DefaultReadLive_PrefersNamedCamera_OverEarlierChild()
        {
            var root = new GameObject("view");
            try
            {
                var noise = new GameObject("noise");
                noise.transform.SetParent(root.transform, false);
                noise.AddComponent<Camera>().fieldOfView = 50f;

                var wantedGo = new GameObject("wanted");
                wantedGo.transform.SetParent(root.transform, false);
                var wanted = wantedGo.AddComponent<Camera>();
                wanted.fieldOfView = 20f;

                var view = root.AddComponent<ViewWithNamedCamera>();
                view.Camera = wanted;

                var snap = CameraPose.DefaultReadLive(view);
                Assert.IsNotNull(snap);
                Assert.AreEqual(20f, snap.Value.Fov, 0.0001f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DefaultReadLive_NoCamera_ReturnsNull()
        {
            var root = new GameObject("empty");
            try
            {
                var view = root.AddComponent<ViewWithNamedCamera>();
                Assert.IsNull(CameraPose.DefaultReadLive(view));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Pose_FirstArm_WritesLiveSnapshot_SecondArm_KeepsIt()
        {
            WritePerspectiveAdapter();
            var live = new CameraPose.Snapshot(
                new Vector3(9, 8, 7), new Vector3(40.84f, 135f, 0f), 20f, "CameraView");
            var reads = 0;
            var host = Host(
                playing: true,
                findType: _ => typeof(Camera),
                instances: new object[] { new object() },
                readLive: _ =>
                {
                    reads++;
                    return live;
                });

            var first = CameraPose.Pose(new[] { "1", "2", "3", "10", "20", "0", "14" }, host);
            Assert.IsTrue(first.Ok, first.Error);
            Assert.AreEqual(1, reads);
            Assert.IsTrue(CameraPose.TryReadSnapshot(_root, out var afterFirst, out var err), err);
            Assert.AreEqual(9f, afterFirst.Position.x, 0.0001f);
            Assert.AreEqual(20f, afterFirst.Fov, 0.0001f);

            var second = CameraPose.Pose(new[] { "4", "5", "6", "1", "2", "3", "10" }, host);
            Assert.IsTrue(second.Ok, second.Error);
            Assert.AreEqual(1, reads, "nested pose must not re-read / replace the snapshot");
            Assert.IsTrue(CameraPose.TryReadSnapshot(_root, out var afterSecond, out err), err);
            Assert.AreEqual(9f, afterSecond.Position.x, 0.0001f);
            Assert.AreEqual(20f, afterSecond.Fov, 0.0001f);
        }

        [Test]
        public void Pose_MissingCamera_Refuses()
        {
            WritePerspectiveAdapter();
            var result = CameraPose.Pose(
                new[] { "1", "2", "3", "40.84", "135", "0", "20" },
                Host(
                    playing: true,
                    findType: _ => typeof(Camera),
                    instances: new object[] { new object() },
                    readLive: _ => new CameraPose.Snapshot(
                        Vector3.zero, new Vector3(40.84f, 135f, 0f), 20f, "CameraView"),
                    resolveCamera: _ => null));
            Assert.IsFalse(result.Ok);
            StringAssert.Contains("could not resolve Camera", result.Error);
            Assert.IsFalse(CameraPose.HoldArmed);
            Assert.IsFalse(File.Exists(CameraPose.SnapshotPath(_root)),
                "failed pose must not leave camera-hold.json");
        }

        [Test]
        public void Pose_ApplyFails_DoesNotWriteSnapshot()
        {
            WritePerspectiveAdapter();
            var result = CameraPose.Pose(
                new[] { "1", "2", "3", "40.84", "135", "0", "20" },
                Host(
                    playing: true,
                    findType: _ => typeof(Camera),
                    instances: new object[] { new object() },
                    readLive: _ => new CameraPose.Snapshot(
                        Vector3.zero, new Vector3(40.84f, 135f, 0f), 20f, "CameraView"),
                    apply: (_, _, _) => "SetRotation missing"));
            Assert.IsFalse(result.Ok);
            StringAssert.Contains("SetRotation missing", result.Error);
            Assert.IsFalse(CameraPose.HoldArmed);
            Assert.IsFalse(File.Exists(CameraPose.SnapshotPath(_root)));
        }

        [Test]
        public void DefaultResolveCamera_IgnoresUnnamedChild()
        {
            var root = new GameObject("view");
            try
            {
                var noise = new GameObject("noise");
                noise.transform.SetParent(root.transform, false);
                noise.AddComponent<Camera>().fieldOfView = 50f;
                var view = root.AddComponent<ViewWithNamedCamera>();
                Assert.IsNull(CameraPose.DefaultResolveCamera(view));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Release_RestoreFails_KeepsHoldAndSnapshot()
        {
            WritePerspectiveAdapter();
            var result = CameraPose.Pose(
                new[] { "1", "2", "3", "40.84", "135", "0", "20" },
                Host(
                    playing: true,
                    findType: _ => typeof(Camera),
                    instances: new object[] { new object() },
                    readLive: _ => new CameraPose.Snapshot(
                        Vector3.zero, new Vector3(40.84f, 135f, 0f), 20f, "CameraView"),
                    restoreRotation: (_, _, _, _) => "restore blew up"));
            Assert.IsTrue(result.Ok, result.Error);
            Assert.IsTrue(File.Exists(CameraPose.SnapshotPath(_root)));
            Assert.IsTrue(CameraPose.HoldArmed);

            var released = CameraPose.Release(Host(
                playing: true,
                restoreRotation: (_, _, _, _) => "restore blew up"));
            Assert.IsFalse(released.Ok);
            StringAssert.Contains("restore blew up", released.Error);
            Assert.IsTrue(CameraPose.HoldArmed, "failed restore must not drop the hold");
            Assert.IsTrue(File.Exists(CameraPose.SnapshotPath(_root)));
        }

        [Test]
        public void Hold_AppliesRotationThenPositionThenFov_OnPosedCameraOnly()
        {
            WritePerspectiveAdapter();
            var go = new GameObject("probe-view");
            var otherGo = new GameObject("other-cam");
            Action<ScriptableRenderContext, Camera>? handler = null;
            try
            {
                var view = go.AddComponent<ApplyProbeView>();
                var other = otherGo.AddComponent<Camera>();
                var host = Host(
                    playing: true,
                    findType: _ => typeof(ApplyProbeView),
                    instances: new object[] { view },
                    readLive: _ => new CameraPose.Snapshot(
                        new Vector3(9, 8, 7), new Vector3(40.84f, 135f, 0f), 20f, "ApplyProbeView"),
                    subscribeBegin: h => handler = h,
                    unsubscribeBegin: h => { if (handler == h) handler = null; },
                    resolveCamera: _ => _cam,
                    apply: CameraPose.DefaultApply,
                    restoreRotation: CameraPose.DefaultRestoreRotation);

                var posed = CameraPose.Pose(
                    new[] { "1", "2", "3", "40.84", "135", "0", "20" }, host);
                Assert.IsTrue(posed.Ok, posed.Error);
                Assert.IsTrue(CameraPose.HoldArmed);
                Assert.IsNotNull(handler);
                CollectionAssert.AreEqual(
                    new[] { "SetRotation", "SetPosition", "SetFov" }, view.Calls);
                Assert.Less(Quaternion.Angle(view.LastRot, Quaternion.Euler(40.84f, 135f, 0f)), 0.2f);
                Assert.AreEqual(1f, view.LastPos.x, 0.0001f);
                Assert.AreEqual(20f, view.LastFov, 0.0001f);

                view.Calls.Clear();
                handler!(default, other);
                Assert.AreEqual(0, view.Calls.Count, "other cameras in the URP stack must not be posed");

                handler!(default, _cam);
                CollectionAssert.AreEqual(
                    new[] { "SetRotation", "SetPosition", "SetFov" }, view.Calls);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(otherGo);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Release_Unsubscribes_RestoresRotationThenCurrentPosition_NotFov()
        {
            WritePerspectiveAdapter();
            var go = new GameObject("probe-view");
            Action<ScriptableRenderContext, Camera>? handler = null;
            try
            {
                var view = go.AddComponent<ApplyProbeView>();
                var current = new Vector3(11, 12, 13);
                var host = Host(
                    playing: true,
                    findType: _ => typeof(ApplyProbeView),
                    instances: new object[] { view },
                    readLive: _ => new CameraPose.Snapshot(
                        current, new Vector3(40.84f, 135f, 0f), 20f, "ApplyProbeView"),
                    subscribeBegin: h => handler = h,
                    unsubscribeBegin: h => { if (handler == h) handler = null; },
                    resolveCamera: _ => _cam,
                    apply: CameraPose.DefaultApply,
                    restoreRotation: CameraPose.DefaultRestoreRotation);

                Assert.IsTrue(CameraPose.Pose(
                    new[] { "1", "2", "3", "10", "20", "0", "14" }, host).Ok);
                view.Calls.Clear();

                var released = CameraPose.Release(host);
                Assert.IsTrue(released.Ok, released.Error);
                Assert.IsFalse(CameraPose.HoldArmed);
                Assert.IsNull(handler);
                CollectionAssert.AreEqual(new[] { "SetRotation", "SetPosition" }, view.Calls);
                Assert.Less(Quaternion.Angle(view.LastRot, Quaternion.Euler(40.84f, 135f, 0f)), 0.2f);
                Assert.AreEqual(11f, view.LastPos.x, 0.0001f);
                Assert.AreEqual(14f, view.LastFov, 0.0001f,
                    "release must not write FOV — follow owns the latch");
                StringAssert.Contains("rotation restored", string.Join(" ", released.Lines));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void NestedPose_ReplacesHoldTarget_NotSnapshot()
        {
            WritePerspectiveAdapter();
            var go = new GameObject("probe-view");
            Action<ScriptableRenderContext, Camera>? handler = null;
            try
            {
                var view = go.AddComponent<ApplyProbeView>();
                var host = Host(
                    playing: true,
                    findType: _ => typeof(ApplyProbeView),
                    instances: new object[] { view },
                    readLive: _ => new CameraPose.Snapshot(
                        new Vector3(9, 8, 7), new Vector3(40.84f, 135f, 0f), 20f, "ApplyProbeView"),
                    subscribeBegin: h => handler = h,
                    unsubscribeBegin: h => { if (handler == h) handler = null; },
                    resolveCamera: _ => _cam,
                    apply: CameraPose.DefaultApply);

                Assert.IsTrue(CameraPose.Pose(
                    new[] { "1", "2", "3", "10", "20", "0", "14" }, host).Ok);
                Assert.IsTrue(CameraPose.Pose(
                    new[] { "4", "5", "6", "1", "2", "3", "10" }, host).Ok);

                view.Calls.Clear();
                handler!(default, _cam);
                Assert.AreEqual(4f, view.LastPos.x, 0.0001f);
                Assert.AreEqual(10f, view.LastFov, 0.0001f);
                Assert.IsTrue(CameraPose.TryReadSnapshot(_root, out var snap, out var err), err);
                Assert.AreEqual(9f, snap.Position.x, 0.0001f);
                Assert.AreEqual(20f, snap.Fov, 0.0001f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private void WritePerspectiveAdapter() =>
            WriteAdapter(
                @"{""camera"":{""viewType"":""CameraView"",""projection"":""perspective"",""setPosition"":""SetPosition"",""setRotation"":""SetRotation"",""setFov"":""SetFov""}}");

        private void WriteAdapter(string json)
        {
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            File.WriteAllText(Path.Combine(RelayPaths.NovaDir(_root), "adapter.json"), json);
        }

        private sealed class ViewWithNamedCamera : MonoBehaviour
        {
            public Camera Camera = null!;
        }

        private sealed class ApplyProbeView : MonoBehaviour
        {
            public readonly List<string> Calls = new();
            public Quaternion LastRot;
            public Vector3 LastPos;
            public float LastFov = -1f;

            public void SetRotation(Quaternion rotation)
            {
                Calls.Add("SetRotation");
                LastRot = rotation;
            }

            public void SetPosition(Vector3 position)
            {
                Calls.Add("SetPosition");
                LastPos = position;
            }

            public void SetFov(float fov)
            {
                Calls.Add("SetFov");
                LastFov = fov;
            }
        }

        private CameraPose.Host Host(
            bool playing,
            Func<string, Type?>? findType = null,
            object[]? instances = null,
            Func<object, CameraPose.Snapshot?>? readLive = null,
            Action<Action<ScriptableRenderContext, Camera>>? subscribeBegin = null,
            Action<Action<ScriptableRenderContext, Camera>>? unsubscribeBegin = null,
            Func<object, Camera?>? resolveCamera = null,
            Func<object, CameraPose.Parsed, CameraAdapter, string?>? apply = null,
            Func<object, CameraPose.Snapshot, CameraAdapter, Vector3, string?>? restoreRotation = null) =>
            new(
                _root,
                isPlaying: () => playing,
                findType: findType ?? (_ => typeof(Camera)),
                findInstances: _ => instances ?? Array.Empty<object>(),
                loadAdapter: () => AdapterJson.Load(_root),
                readLive: readLive,
                subscribeBegin: subscribeBegin ?? (_ => { }),
                unsubscribeBegin: unsubscribeBegin ?? (_ => { }),
                resolveCamera: resolveCamera ?? (_ => _cam),
                apply: apply ?? ((_, _, _) => null),
                restoreRotation: restoreRotation ?? ((_, _, _, _) => null));
    }
}
