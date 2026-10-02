using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Last-resort recorder used when com.unity.recorder is not installed: dumps one PNG per
    /// rendered frame (Time.captureFramerate pins game time to 60fps, so playback speed is
    /// correct even though capture runs slower than realtime) and assembles with ffmpeg on Stop.
    /// CAVEAT: capture is not realtime and captureFramerate changes frame pacing — prefer the
    /// Unity Recorder driver whenever the package can be installed.
    /// </summary>
    public sealed class ScreenCaptureDriver : IRecorderDriver
    {
        public const int FPS = 60;
        public string OutputDir => RecorderPaths.OutputDir;

        // This driver grabs the Game View frame as-is, so its output IS the game view's RENDER size —
        // and it therefore can never mismatch. Deliberately NOT Screen.width/height: in the Editor
        // those report the window (937x991 in the case that taught us) while the view renders at the
        // selected resolution (1080x1920), which made this driver report a mismatch against itself.
        public int OutputWidth => CaptureAspect.GameViewSize().W;
        public int OutputHeight => CaptureAspect.GameViewSize().H;
        public bool IsRecording { get; private set; }

        /// <summary>Overridable for environments where ffmpeg is elsewhere (or absent — see Stop).</summary>
        public static string FfmpegPath = "/opt/homebrew/bin/ffmpeg";

        private string _framesDir = "";
        private string _clipName = "";
        private int _frame;

        public static string FramePath(string framesDir, int frame) =>
            $"{framesDir}/frame_{frame:D5}.png";

        /// <summary>Kit 0.14.3 (KIT-13): both paths QUOTED — a project path with a space split into two arguments, and an
        /// unquoted name could add its own (ffmpeg writes whatever output file an argument names).</summary>
        public static string BuildFfmpegArgs(string framesDir, string outputPath, int fps) =>
            $"-y -framerate {fps} -i {Quote(framesDir + "/frame_%05d.png")} -c:v libx264 -pix_fmt yuv420p {Quote(outputPath)}";

        // a path never holds a double quote here (the temp dir, Library/Nova/Recordings and a checked clip name); one that
        // did is cut at it rather than escaped, so it can never close the argument early
        private static string Quote(string arg) => "\"" + (arg.IndexOf('"') >= 0 ? arg.Substring(0, arg.IndexOf('"')) : arg) + "\"";

        public void Start(string clipName)
        {
            if (IsRecording)
            {
                Debug.LogWarning("[RecorderKit] ScreenCaptureDriver already recording; ignoring start.");
                return;
            }
            // Kit 0.14.3 (KIT-13): the name becomes a temp folder that is deleted recursively, and the output file's name.
            // The loader refuses an unsafe one; this is the recorder's own check — it records nothing rather than walk.
            if (!JsonShotLoader.IsSafeShotName(clipName))
            {
                Debug.LogError($"[RecorderKit] refusing to record '{clipName}': a clip name becomes a file name — letters, digits, '.', '_' and '-' only");
                return;
            }
            _clipName = clipName;
            _framesDir = Path.Combine(Path.GetTempPath(), $"kit-frames-{clipName}");
            if (Directory.Exists(_framesDir))
                Directory.Delete(_framesDir, recursive: true);
            Directory.CreateDirectory(_framesDir);
            Directory.CreateDirectory(OutputDir);
            _frame = 0;
            Time.captureFramerate = FPS;
            EditorApplication.update += CaptureFrame;
            IsRecording = true;
            Debug.Log($"[RecorderKit] fallback recording started: {clipName} (ScreenCapture @ {FPS}fps)");
        }

        public void Stop()
        {
            if (!IsRecording)
                return;
            EditorApplication.update -= CaptureFrame;
            Time.captureFramerate = 0;
            IsRecording = false;

            var output = Path.Combine(OutputDir, $"{_clipName}_fallback.mp4");
            if (!File.Exists(FfmpegPath))
            {
                Debug.LogWarning($"[RecorderKit] ffmpeg not found at {FfmpegPath}; " +
                    $"frames left in {_framesDir} — assemble manually.");
                return;
            }
            var psi = new ProcessStartInfo(FfmpegPath, BuildFfmpegArgs(_framesDir, output, FPS))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            var stderr = new System.Text.StringBuilder();
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                // Drain stderr asynchronously — a synchronous ReadToEnd() here would block
                // indefinitely on a hung process, defeating the WaitForExit timeout below.
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                proc.BeginErrorReadLine();
            }
            var exited = proc != null && proc.WaitForExit(120_000);
            if (!exited || proc!.ExitCode != 0)
            {
                Debug.LogError($"[RecorderKit] ffmpeg failed to assemble {output} " +
                    $"(exited={exited}, exitCode={(exited ? proc!.ExitCode.ToString() : "n/a")}): {stderr}");
                return;
            }
            Debug.Log($"[RecorderKit] fallback recording stopped: {output}");
        }

        private void CaptureFrame()
        {
            if (!Application.isPlaying)
                return;
            ScreenCapture.CaptureScreenshot(FramePath(_framesDir, _frame++));
        }
    }
}
