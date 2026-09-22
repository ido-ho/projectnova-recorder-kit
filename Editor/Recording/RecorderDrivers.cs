namespace ProjectNova.RecorderKit
{
    /// <summary>Clip recording surface. Start/Stop bracket one clip; Stop must no-op when idle.</summary>
    public interface IRecorderDriver
    {
        /// <summary>Project-relative folder finished clips land in.</summary>
        string OutputDir { get; }
        bool IsRecording { get; }

        /// <summary>
        /// Pixel size this driver writes. Declared on the interface so the director can compare it
        /// against the live Game View aspect: when they disagree, world-space content silently leaves
        /// the frame while UI still fits (see CaptureAspect). Without this the director had no way to
        /// know what shape its own footage would be.
        /// </summary>
        int OutputWidth { get; }
        int OutputHeight { get; }

        void Start(string clipName);
        void Stop();
    }

    /// <summary>
    /// Driver selection. The Unity Recorder sub-assembly (compiled only when com.unity.recorder
    /// is installed) registers itself here from [InitializeOnLoad]; without it the kit falls back
    /// to ScreenCapture frames + ffmpeg.
    /// </summary>
    /// <summary>Which capture path a game wants. See RecorderDrivers.For.</summary>
    public enum RecorderPreference
    {
        /// <summary>Unity Recorder when installed, else ScreenCapture. Correct for most games.</summary>
        Auto,

        /// <summary>Force Unity Recorder (fails over to ScreenCapture if the package is absent).</summary>
        UnityRecorder,

        /// <summary>
        /// Force the ScreenCapture path. Needed by games whose world-space rendering does not survive
        /// Unity Recorder's Game View capture — see RecorderDrivers.For.
        /// </summary>
        ScreenCapture,
    }

    /// <summary>
    /// Driver selection. The Unity Recorder sub-assembly (compiled only when com.unity.recorder
    /// is installed) registers itself here from [InitializeOnLoad]; without it the kit falls back
    /// to ScreenCapture frames + ffmpeg.
    /// </summary>
    public static class RecorderDrivers
    {
        public static IRecorderDriver? Registered;
        public static IRecorderDriver Best => Registered ?? Fallback;
        private static ScreenCaptureDriver? _fallback;
        private static ScreenCaptureDriver Fallback => _fallback ??= new ScreenCaptureDriver();

        /// <summary>
        /// The driver for a stated preference.
        ///
        /// WHY A GAME MAY NEED TO OVERRIDE: Unity Recorder is the better path in general (real encoding,
        /// exact output size, audio) but it is NOT universally correct. On one game it recorded the canvas
        /// UI perfectly and dropped the world-space content entirely — a match-3 clip with its HUD intact
        /// and no gem board — while ScreenCapture of the same live frame at the same 1080x1920 was
        /// flawless. Aspect was ruled out by matching it exactly and seeing no change, so the difference
        /// is the capture path itself, on a URP project.
        ///
        /// That failure is invisible to every automated signal: the state settle passes, the shot's
        /// anchors pass, captured:true, and a Phase 0 screenshot proof passes because screenshots use the
        /// OTHER path. Only opening the clip shows it. So the remedy is per-game selection plus the
        /// standing rule that a shot is proven by vision-reading its CLIP, never its return value.
        /// </summary>
        public static IRecorderDriver For(RecorderPreference preference) => preference switch
        {
            RecorderPreference.ScreenCapture => Fallback,
            RecorderPreference.UnityRecorder => Registered ?? Fallback,
            _ => Best,
        };
    }
}
