using System;
using System.IO;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    public class AdapterJsonTests
    {
        [Test]
        public void Load_ReadsOverlayTypeNames_AndGameId()
        {
            var dir = Path.Combine(Path.GetTempPath(), "nova-adapterjson-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path.Combine(RelayPaths.NovaDir(dir)));
            try
            {
                File.WriteAllText(Path.Combine(RelayPaths.NovaDir(dir), "adapter.json"),
                    @"{""gameId"":""demo"",""overlayTypeNames"":[""FPS"",""DebugStats""]}");
                var loaded = AdapterJson.Load(dir);
                Assert.AreEqual("demo", loaded.GameId);
                CollectionAssert.AreEqual(new[] { "FPS", "DebugStats" }, loaded.OverlayTypeNames);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Load_MissingOrMalformed_YieldsEmptyNames_DoesNotThrow()
        {
            var dir = Path.Combine(Path.GetTempPath(), "nova-adapterjson-miss-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            try
            {
                Assert.DoesNotThrow(() => AdapterJson.Load(dir));
                Assert.IsEmpty(AdapterJson.Load(dir).OverlayTypeNames);
                Directory.CreateDirectory(RelayPaths.NovaDir(dir));
                File.WriteAllText(Path.Combine(RelayPaths.NovaDir(dir), "adapter.json"), "{");
                Assert.IsEmpty(AdapterJson.Load(dir).OverlayTypeNames);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Load_IgnoresNonStringOverlayEntries()
        {
            var dir = Path.Combine(Path.GetTempPath(), "nova-adapterjson-mix-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(RelayPaths.NovaDir(dir));
            try
            {
                File.WriteAllText(Path.Combine(RelayPaths.NovaDir(dir), "adapter.json"),
                    @"{""overlayTypeNames"":[""FPS"", 1, null, "" ""]}");
                CollectionAssert.AreEqual(new[] { "FPS" }, AdapterJson.Load(dir).OverlayTypeNames);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void OverlayNamesFor_PrefersJsonWhenPresent_ElseAdapterField()
        {
            CollectionAssert.AreEqual(new[] { "FPS" },
                AdapterJson.OverlayNamesFor(new[] { "FPS" }, new[] { "Other" }));
            CollectionAssert.AreEqual(new[] { "Other" },
                AdapterJson.OverlayNamesFor(Array.Empty<string>(), new[] { "Other" }));
        }

        [Test]
        public void Load_WhitespaceGameId_IsNull()
        {
            var dir = Path.Combine(Path.GetTempPath(), "nova-adapterjson-ws-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(RelayPaths.NovaDir(dir));
            try
            {
                File.WriteAllText(Path.Combine(RelayPaths.NovaDir(dir), "adapter.json"),
                    @"{""gameId"":""   ""}");
                Assert.IsNull(AdapterJson.Load(dir).GameId);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Load_StringScalarOverlayTypeNames_IsEmpty()
        {
            var dir = Path.Combine(Path.GetTempPath(), "nova-adapterjson-scalar-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(RelayPaths.NovaDir(dir));
            try
            {
                File.WriteAllText(Path.Combine(RelayPaths.NovaDir(dir), "adapter.json"),
                    @"{""overlayTypeNames"":""FPS""}");
                Assert.IsEmpty(AdapterJson.Load(dir).OverlayTypeNames);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Load_ReadsCameraBlock()
        {
            var dir = Path.Combine(Path.GetTempPath(), "nova-adapterjson-cam-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(RelayPaths.NovaDir(dir));
            try
            {
                File.WriteAllText(Path.Combine(RelayPaths.NovaDir(dir), "adapter.json"),
                    @"{""gameId"":""demo"",""camera"":{""viewType"":""CameraView"",""projection"":""perspective"",""setPosition"":""SetPosition"",""setRotation"":""SetRotation"",""setFov"":""SetFov""}}");
                var loaded = AdapterJson.Load(dir);
                Assert.IsNotNull(loaded.Camera);
                Assert.AreEqual("CameraView", loaded.Camera!.Value.ViewType);
                Assert.AreEqual("perspective", loaded.Camera.Value.Projection);
                Assert.AreEqual("SetPosition", loaded.Camera.Value.SetPosition);
                Assert.AreEqual("SetRotation", loaded.Camera.Value.SetRotation);
                Assert.AreEqual("SetFov", loaded.Camera.Value.SetFov);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Load_CameraSetterDefaults_WhenOmitted()
        {
            var dir = Path.Combine(Path.GetTempPath(), "nova-adapterjson-camdef-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(RelayPaths.NovaDir(dir));
            try
            {
                File.WriteAllText(Path.Combine(RelayPaths.NovaDir(dir), "adapter.json"),
                    @"{""camera"":{""viewType"":""CameraView"",""projection"":""perspective""}}");
                var cam = AdapterJson.Load(dir).Camera;
                Assert.IsNotNull(cam);
                Assert.AreEqual("SetPosition", cam!.Value.SetPosition);
                Assert.AreEqual("SetRotation", cam.Value.SetRotation);
                Assert.AreEqual("SetFov", cam.Value.SetFov);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Load_MissingOrMalformedCamera_YieldsNull_DoesNotThrow()
        {
            var dir = Path.Combine(Path.GetTempPath(), "nova-adapterjson-cammiss-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(RelayPaths.NovaDir(dir));
            try
            {
                File.WriteAllText(Path.Combine(RelayPaths.NovaDir(dir), "adapter.json"),
                    @"{""gameId"":""demo""}");
                Assert.IsNull(AdapterJson.Load(dir).Camera);

                File.WriteAllText(Path.Combine(RelayPaths.NovaDir(dir), "adapter.json"),
                    @"{""camera"":""CameraView""}");
                Assert.DoesNotThrow(() => AdapterJson.Load(dir));
                Assert.IsNull(AdapterJson.Load(dir).Camera);

                File.WriteAllText(Path.Combine(RelayPaths.NovaDir(dir), "adapter.json"),
                    @"{""camera"":{""viewType"":1,""projection"":true}}");
                var cam = AdapterJson.Load(dir).Camera;
                Assert.IsNotNull(cam);
                Assert.IsNull(cam!.Value.ViewType);
                Assert.IsNull(cam.Value.Projection);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Load_IgnoresUnknownCameraKeys()
        {
            var dir = Path.Combine(Path.GetTempPath(), "nova-adapterjson-camx-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(RelayPaths.NovaDir(dir));
            try
            {
                File.WriteAllText(Path.Combine(RelayPaths.NovaDir(dir), "adapter.json"),
                    @"{""camera"":{""viewType"":""CameraView"",""projection"":""perspective"",""holds"":false}}");
                var cam = AdapterJson.Load(dir).Camera;
                Assert.IsNotNull(cam);
                Assert.AreEqual("CameraView", cam!.Value.ViewType);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
