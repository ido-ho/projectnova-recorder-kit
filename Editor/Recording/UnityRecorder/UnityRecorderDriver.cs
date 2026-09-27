using System.IO;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Unity Recorder driver: Game View at 1080x1920 / 60fps H.264 MP4 with audio into the
    /// project's <c>Library/Nova/Recordings</c> folder (<see cref="RecorderPaths.OutputDir"/>). Registers itself as the
    /// preferred driver on load, in an interactive editor only (<see cref="UnityRecorderDriverBoot"/>).
    /// </summary>
    public sealed class UnityRecorderDriver : IRecorderDriver
    {
        public const int WIDTH = 1080;
        public const int HEIGHT = 1920;
        public const float FPS = 60f;

        public string OutputDir => RecorderPaths.OutputDir;
        public int OutputWidth => WIDTH;
        public int OutputHeight => HEIGHT;
        public bool IsRecording => _controller != null && _controller.IsRecording();

        private RecorderController? _controller;

        /// <summary>Builds controller + movie settings for a clip. Pure — safe to unit-test.</summary>
        public static (RecorderControllerSettings controller, MovieRecorderSettings movie) BuildSettings(string clipName)
        {
            var movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
            movie.name = "RecorderKit_Movie";
            movie.Enabled = true;
            movie.ImageInputSettings = new GameViewInputSettings
            {
                OutputWidth = WIDTH,
                OutputHeight = HEIGHT,
            };
            movie.EncoderSettings = new CoreEncoderSettings
            {
                Codec = CoreEncoderSettings.OutputCodec.MP4,
                EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High,
                EncodingProfile = CoreEncoderSettings.H264EncodingProfile.High,
            };
            movie.CaptureAudio = true;
            movie.FileNameGenerator.Root = OutputPath.Root.Project;
            movie.FileNameGenerator.Leaf = RecorderPaths.OutputDir;
            movie.FileNameGenerator.FileName = clipName + "_<Take>";

            var controller = ScriptableObject.CreateInstance<RecorderControllerSettings>();
            controller.name = "RecorderKit_Controller";
            controller.AddRecorderSettings(movie);
            controller.SetRecordModeToManual();
            controller.FrameRate = FPS;
            controller.CapFrameRate = true;

            return (controller, movie);
        }

        public void Start(string clipName)
        {
            if (IsRecording)
            {
                Debug.LogWarning("[RecorderKit] Already recording; ignoring start.");
                return;
            }
            Directory.CreateDirectory(OutputDir);
            var (settings, _) = BuildSettings(clipName);
            _controller = new RecorderController(settings);
            _controller.PrepareRecording();
            if (_controller.StartRecording())
                Debug.Log($"[RecorderKit] Recording started: {clipName} ({WIDTH}x{HEIGHT}@{FPS}).");
            else
                Debug.LogError("[RecorderKit] Failed to start recording.");
        }

        public void Stop()
        {
            if (_controller == null || !_controller.IsRecording())
                return;
            _controller.StopRecording();
            Debug.Log($"[RecorderKit] Recording stopped. Output in {Path.GetFullPath(OutputDir)}");
            _controller = null;
        }
    }

    /// <summary>
    /// Registers the Unity Recorder driver when an editor loads. INERT IN BATCH MODE (v6.1 §1 B): the kit changes
    /// nothing in a studio's headless builds and CI runs, and there is nothing to record there.
    /// <see cref="Booted"/> says whether it acted, for the kit's own test.
    /// </summary>
    [InitializeOnLoad]
    public static class UnityRecorderDriverBoot
    {
        public static bool Booted { get; private set; }

        static UnityRecorderDriverBoot()
        {
            if (Application.isBatchMode) return;
            RecorderDrivers.Registered = new UnityRecorderDriver();
            Booted = true;
        }
    }
}
