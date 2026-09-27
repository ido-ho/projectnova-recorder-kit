using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Slice E.3 — WHO A TRY'S SCREEN CHECK IS ASKED OF, AND AS WHOM. Built by the agent from the
    /// job it CLAIMED (<c>NovaCaptureAgent.RunProbe</c>): the run id is the claimed run's, the key is
    /// the studio key that claimed it, and the HTTP transport is the kit's own
    /// (<see cref="IStudioHttp"/>) — so the request carries exactly the headers every other job route
    /// carries (<see cref="UnityStudioHttp.ApplyStudioHeaders"/>): the key, <c>X-Nova-Kit-Version</c>,
    /// and <c>X-Nova-Workspace</c> = the workspace this Unity project is BOUND to, which the agent
    /// has already required to be the job's own before the try ran (<see cref="WorkspaceGuard"/>).
    /// Nothing here is read from a file.
    ///
    /// The three settable seams exist for the kit's tests; production leaves them alone.
    /// </summary>
    public sealed class ProbeVisionWire
    {
        public ProbeVisionWire(IStudioHttp http, string baseUrl, string studioKey, string runId, string frameDir)
        {
            Http = http;
            BaseUrl = baseUrl;
            StudioKey = studioKey;
            RunId = runId;
            FrameDir = frameDir;
        }

        public IStudioHttp Http { get; }
        public string BaseUrl { get; }
        public string StudioKey { get; }
        public string RunId { get; }

        /// <summary>Where each screen check's frame is written before it is sent — the try's own
        /// folder, <c>Library/AdRelay/</c> (<see cref="RelayPaths.Root"/>).</summary>
        public string FrameDir { get; }

        /// <summary>The clock the retries and the frame wait are timed on.</summary>
        public Func<double> Now { get; set; } = () => EditorApplication.timeSinceStartup;

        /// <summary>Ask for ONE frame of the screen at this path; null = asked, else why not. The
        /// frame lands at the END of a frame (Unity's rule), so the channel waits for the file.</summary>
        public Func<string, string?> CaptureFrame { get; set; } = HttpVisionChannel.ScreenCaptureTo;

        /// <summary>Make a frame at or over the cap fit under it (<see cref="VisionFrame.FitUnder"/>).</summary>
        public Func<string, long, VisionFrameFit> FitFrame { get; set; } = VisionFrame.FitUnder;
    }

    /// <summary>
    /// Slice E.3 — THE SCREEN CHECK DURING A TRY, ASKED OF THE SITE. Used ONLY by a <c>probe</c> job
    /// whose shot has <c>vision</c> steps (<c>ProbeRun.DirectorOptions</c>, the overload that takes the shot);
    /// a capture keeps <see cref="FileVisionChannel"/>.
    ///
    /// THE WIRE (the design doc's "As built — E.3", the kit wire): <c>POST
    /// /studio/capture-jobs/:runId/vision</c>, multipart with exactly three parts — <c>frame</c> (one
    /// image of the screen, under <see cref="MaxFrameBytes"/>), <c>step</c> (the 0-based index of
    /// the vision step in the SENT shot's <c>steps</c>) and <c>requestId</c>. NO prompt: the site
    /// reads it out of the shot it sent (invariant 100).
    ///
    /// ONE QUESTION = ONE REQUEST ID. Each <see cref="Request"/> mints a new id
    /// (<c>Guid.ToString("N")</c>, 32 hex characters); every retry of that question sends the SAME
    /// id, so the site bills it once. A domain reload re-runs the whole shot and takes a NEW frame —
    /// a new question, with a new id; the site refuses a new id for a step it was already asked on this
    /// try, by name ("step N was already asked on this try — a re-run does not ask it again"). Reusing
    /// the old id there would have handed back a verdict about a screen that no longer exists.
    ///
    /// ANSWERS. 200 <c>{ requestId, step, match }</c> — the echoed id and step must be the ones this
    /// question SENT and <c>match</c> must be a JSON boolean, or it is not a verdict. A 4xx is a
    /// REFUSAL (<see cref="JobGuard.IsDeterministicAnswer"/>, the kit's one rule): the step fails with
    /// the site's own <c>message</c> and is never read as "did not match". A 5xx, a 408/429 or no
    /// response is retried with the same id (<see cref="MaxPosts"/> posts, backing off 1, 2 and 4 s),
    /// then fails by name. The director's own step timeout still applies over all of it.
    ///
    /// NEVER BLOCKS THE EDITOR: the POST is the kit's own <see cref="IStudioHttp.PostMultipart"/> — a
    /// <c>UnityWebRequest</c> sent asynchronously — and everything else happens in
    /// <see cref="Poll"/>, which the director calls once per editor tick: wait for the frame, send,
    /// check <c>IsDone</c>, retry. No thread: <c>ScreenCapture</c> and <c>UnityWebRequest</c> are
    /// main-thread APIs. The one piece of CPU work that is not free — shrinking a frame at or over the cap
    /// (<see cref="VisionFrame.FitUnder"/>) — runs once, on that tick.
    /// </summary>
    public sealed class HttpVisionChannel : IVisionChannel, IVisionOutcome, IDisposable
    {
        /// <summary>The site's cap on the frame part: <c>SELF_TEST_RESULT_MAX_BYTES</c>, 8 MiB. A frame
        /// goes up only when it is UNDER it: the site's upload limit refuses a file that reaches it (the
        /// first audit of E.3, K-N1).</summary>
        public const long MaxFrameBytes = 8L * 1024 * 1024;

        /// <summary>How many times ONE question is posted, all with the same request id.</summary>
        public const int MaxPosts = 4;

        /// <summary>The wait before retry N (1-based) — short: the step's own timeout (60 s by
        /// default) is the real bound.</summary>
        internal static readonly double[] BackoffSec = { 1, 2, 4 };

        /// <summary>How long the frame may take to appear — the try's own frame rule
        /// (<c>NovaCaptureAgent.ScreenshotWaitSec</c>).</summary>
        public const double FrameWaitSec = 10;

        /// <summary>A frame is taken once its size has held for this many polls — the rule the try's
        /// own frame waits on (<c>NovaCaptureAgent.WaitForFrame</c>): a half-written PNG is a
        /// broken picture.</summary>
        public const int StableFramePolls = 3;

        /// <summary>The multipart part names of the wire.</summary>
        public const string FrameField = "frame";
        public const string StepField = "step";
        public const string RequestIdField = "requestId";

        /// <summary>Between the run and the id in a screen check's frame name. Every such frame
        /// starts with <see cref="ProbeRun.FramePrefix"/>, so the sweep that removes a try's stray
        /// frame removes a screen check's too (<see cref="ProbeRun.SweepStrayFrames"/>).</summary>
        internal const string VisionFrameInfix = "-vision-";

        private enum State { Capturing, Posting, Done }

        private sealed class Ask
        {
            public Ask(string id, int step, string framePath)
            {
                Id = id;
                Step = step;
                FramePath = framePath;
                SendPath = framePath;
            }

            public readonly string Id;
            public readonly int Step;
            public readonly string FramePath;
            public string SendPath;
            public string ContentType = "image/png";
            public State State = State.Capturing;
            public double FrameDeadline;
            public long LastSize = -1;
            public int StablePolls;
            public int Posts;
            public double NextPostAt;
            public IStudioResponse? InFlight;
            public VisionPoll Outcome = VisionPoll.Pending;
        }

        private readonly ProbeVisionWire _wire;
        private readonly Dictionary<string, Ask> _asks = new();
        private readonly List<string> _order = new();
        private bool _disposed;

        public HttpVisionChannel(ProbeVisionWire wire)
        {
            _wire = wire;
        }

        /// <summary>Whom this channel asks, and as whom.</summary>
        public ProbeVisionWire Wire => _wire;

        /// <summary>Every request id this channel minted, in order (tests).</summary>
        internal IReadOnlyList<string> RequestIds => _order;

        /// <summary>Where the frame of question <paramref name="requestId"/> is written.</summary>
        public static string FramePath(string frameDir, string runId, string requestId) =>
            Path.Combine(frameDir,
                ProbeRun.FramePrefix + CapturePaths.SafeSegment(runId) + VisionFrameInfix + requestId + ".png");

        /// <summary>"the screen check at step N" — 1-based, the way the site and the page say it.</summary>
        private static string Label(int step) => $"the screen check at step {step + 1}";

        public string? Request(string prompt, int stepIndex)
        {
            var id = Guid.NewGuid().ToString("N");
            var ask = new Ask(id, stepIndex, FramePath(_wire.FrameDir, _wire.RunId, id));
            _asks[id] = ask;
            _order.Add(id);
            if (_disposed)
            {
                End(ask, VisionPoll.Failed($"the try ended before {Label(stepIndex)} could be asked"));
                return id;
            }
            if (stepIndex < 0)
            {
                End(ask, VisionPoll.Failed("the screen check was asked without its step's index — nothing was asked"));
                return id;
            }
            string? refused;
            try
            {
                Directory.CreateDirectory(_wire.FrameDir);
                if (File.Exists(ask.FramePath)) File.Delete(ask.FramePath);
                refused = _wire.CaptureFrame(ask.FramePath);
            }
            catch (Exception e)
            {
                refused = "the frame could not be taken: " + e.Message;
            }
            if (refused != null)
            {
                End(ask, VisionPoll.Failed($"{Label(stepIndex)}: {refused} — nothing was asked"));
                return id;
            }
            ask.FrameDeadline = _wire.Now() + FrameWaitSec;
            return id;
        }

        /// <summary>The file channel's question, answered through <see cref="Poll"/>: true only once
        /// the site ANSWERED. A refusal is not an answer — read it through <see cref="Poll"/>.</summary>
        public bool TryGetResult(string id, out bool match)
        {
            var poll = Poll(id);
            match = poll.Match == true;
            return poll.Match != null;
        }

        public VisionPoll Poll(string id)
        {
            if (!_asks.TryGetValue(id, out var ask))
                return VisionPoll.Failed($"no screen check '{id}' was asked on this channel");
            if (ask.State == State.Capturing) PollFrame(ask);
            if (ask.State == State.Posting) PollPost(ask);
            return ask.Outcome;
        }

        /// <summary>Close every question still open — the run is over (<c>AdDirector.Finish</c>):
        /// the request in flight is disposed and each frame is deleted.</summary>
        public void Dispose()
        {
            _disposed = true;
            foreach (var ask in _asks.Values)
            {
                if (ask.State == State.Done) continue;
                End(ask, VisionPoll.Failed($"the try ended before the site answered {Label(ask.Step)}"));
            }
        }

        // ---- the frame ----

        private void PollFrame(Ask ask)
        {
            var size = SizeOf(ask.FramePath);
            if (size > 0 && size == ask.LastSize)
                ask.StablePolls++;
            else
                ask.StablePolls = 0;
            ask.LastSize = size;
            if (ask.StablePolls >= StableFramePolls)
            {
                ReadyToSend(ask, size);
                return;
            }
            if (_wire.Now() >= ask.FrameDeadline)
                End(ask, VisionPoll.Failed(
                    $"{Label(ask.Step)}: no frame of the screen was written within {FrameWaitSec:0} s — nothing was asked"));
        }

        /// <summary>Only a frame under <see cref="MaxFrameBytes"/> goes up. A PNG at or over it is
        /// re-encoded as a JPEG, halving its size until it fits (<see cref="VisionFrame.FitUnder"/>);
        /// one that still does not fit is refused by name, and nothing is asked.</summary>
        private void ReadyToSend(Ask ask, long size)
        {
            if (size >= MaxFrameBytes)
            {
                VisionFrameFit fit;
                try { fit = _wire.FitFrame(ask.FramePath, MaxFrameBytes); }
                catch (Exception e) { fit = VisionFrameFit.Refused("it could not be shrunk: " + e.Message); }
                var fitPath = fit.Path;
                if (fit.Error != null || fitPath == null)
                {
                    End(ask, VisionPoll.Failed($"{Label(ask.Step)}: the frame is {size} bytes, not under the " +
                                               $"{MaxFrameBytes} bytes (8 MiB) the site accepts — " +
                                               $"{fit.Error ?? "it could not be shrunk"} — nothing was asked"));
                    return;
                }
                var fitted = SizeOf(fitPath);
                if (fitted <= 0 || fitted >= MaxFrameBytes)
                {
                    End(ask, VisionPoll.Failed($"{Label(ask.Step)}: the frame is {size} bytes and still " +
                                               $"{fitted} bytes after shrinking — not under the {MaxFrameBytes} bytes " +
                                               "(8 MiB) the site accepts; nothing was asked"));
                    return;
                }
                ask.SendPath = fitPath;
                ask.ContentType = fit.ContentType;
            }
            ask.State = State.Posting;
            ask.NextPostAt = _wire.Now();
        }

        // ---- the request ----

        private void PollPost(Ask ask)
        {
            if (ask.InFlight == null)
            {
                if (_wire.Now() < ask.NextPostAt) return;
                ask.Posts++;
                var fields = new Dictionary<string, string>
                {
                    [StepField] = ask.Step.ToString(CultureInfo.InvariantCulture),
                    [RequestIdField] = ask.Id,
                };
                try
                {
                    ask.InFlight = _wire.Http.PostMultipart(StudioEndpoints.Vision(_wire.BaseUrl, _wire.RunId),
                        _wire.StudioKey, fields, ask.SendPath, FrameField, ask.ContentType);
                }
                catch (Exception e)
                {
                    RetryOrFail(ask, "the request could not be sent: " + e.Message);
                    return;
                }
            }
            var post = ask.InFlight!;
            if (!post.IsDone) return;
            var status = post.Status;
            var body = post.Body ?? "";
            var transport = post.TransportError;
            ask.InFlight = null;
            try { post.Dispose(); }
            catch (Exception) { /* the answer is already read */ }

            if (transport == null && status >= 200 && status < 300)
            {
                End(ask, ReadAnswer(ask, body));
                return;
            }
            if (transport == null && JobGuard.IsDeterministicAnswer(status))
            {
                // A REFUSAL — the site deciding "no" (the cap, the money, the lease, a bad request, a
                // check that could not be answered). Not retried, and never a verdict.
                End(ask, VisionPoll.Failed($"the site refused {Label(ask.Step)} ({status}): {MessageOf(body)}"));
                return;
            }
            RetryOrFail(ask, transport != null
                ? $"no response ({transport})"
                : $"HTTP {status}: {MessageOf(body)}");
        }

        private void RetryOrFail(Ask ask, string problem)
        {
            if (ask.Posts >= MaxPosts)
            {
                End(ask, VisionPoll.Failed($"the site did not answer {Label(ask.Step)} after {ask.Posts} " +
                                           $"attempts with the same request id (last: {problem})"));
                return;
            }
            ask.NextPostAt = _wire.Now() + BackoffSec[Math.Min(ask.Posts - 1, BackoffSec.Length - 1)];
        }

        /// <summary>A 200 is a verdict only when it answers THIS question: the id and step it echoes
        /// are the ones this question SENT (invariant 100 — identity from the side that sent), and
        /// <c>match</c> is a JSON boolean. Anything else fails the step by name.</summary>
        private static VisionPoll ReadAnswer(Ask ask, string body)
        {
            JObject o;
            try { o = NovaJson.ParseObject(body); }
            catch (Exception e)
            {
                return VisionPoll.Failed($"the site's answer to {Label(ask.Step)} could not be read " +
                                         $"({e.Message}) — it is not a verdict");
            }
            var id = o["requestId"];
            if (id == null || id.Type != JTokenType.String || id.Value<string>() != ask.Id)
                return VisionPoll.Failed($"the site's answer to {Label(ask.Step)} names request " +
                                         $"'{id}', not '{ask.Id}' — it is not a verdict on this question");
            var step = o["step"];
            if (step == null || step.Type != JTokenType.Integer || step.Value<long>() != ask.Step)
                return VisionPoll.Failed($"the site's answer to {Label(ask.Step)} is about step " +
                                         $"'{step}' — it is not a verdict on this question");
            var match = o["match"];
            if (match == null || match.Type != JTokenType.Boolean)
                return VisionPoll.Failed($"the site's answer to {Label(ask.Step)} carries no true/false " +
                                         "`match` — it is not a verdict");
            return VisionPoll.Answered(match.Value<bool>());
        }

        /// <summary>The site's own sentence: <c>message</c> of a Nest error body (a string, or a list
        /// joined), else the body itself, cut to 300 characters.</summary>
        internal static string MessageOf(string body)
        {
            var text = (body ?? "").Trim();
            try
            {
                var o = NovaJson.ParseObject(text);
                var m = o["message"];
                if (m != null && m.Type == JTokenType.String && !string.IsNullOrWhiteSpace(m.Value<string>()))
                    return m.Value<string>()!;
                if (m is JArray list && list.Count > 0)
                    return string.Join("; ", list.Select(x => x.ToString()));
            }
            catch (Exception)
            {
                // not JSON (a proxy's HTML page): the text itself, cut below
            }
            if (text.Length == 0) return "(no message)";
            return text.Length <= 300 ? text : text.Substring(0, 300);
        }

        private void End(Ask ask, VisionPoll outcome)
        {
            if (ask.InFlight != null)
            {
                try { ask.InFlight.Dispose(); }
                catch (Exception) { /* closing, best-effort */ }
                ask.InFlight = null;
            }
            ask.Outcome = outcome;
            ask.State = State.Done;
            DeleteQuietly(ask.FramePath);
            if (ask.SendPath != ask.FramePath) DeleteQuietly(ask.SendPath);
        }

        private static long SizeOf(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch (IOException) { return 0; }
        }

        /// <summary>Best-effort: a frame that cannot be deleted now is a <c>probe-frame-*.png</c>,
        /// which the next try or claim sweeps (<see cref="ProbeRun.SweepStrayFrames"/>).</summary>
        private static void DeleteQuietly(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { /* swept later */ }
        }

        /// <summary>Production <see cref="ProbeVisionWire.CaptureFrame"/>: the try's own way of taking
        /// its frame (<c>NovaCaptureAgent.GatherProbeEvidence</c>) — <c>ScreenCapture</c> to a PNG.</summary>
        internal static string? ScreenCaptureTo(string path)
        {
            try
            {
                ScreenCapture.CaptureScreenshot(path);
                return null;
            }
            catch (Exception e)
            {
                return "ScreenCapture refused the frame: " + e.Message;
            }
        }
    }

    /// <summary>What <see cref="VisionFrame.FitUnder"/> made of a frame: the file to send and its
    /// type, or why none can be sent.</summary>
    public readonly struct VisionFrameFit
    {
        public string? Path { get; }
        public string ContentType { get; }
        public string? Error { get; }

        private VisionFrameFit(string? path, string contentType, string? error)
        {
            Path = path;
            ContentType = contentType;
            Error = error;
        }

        public static VisionFrameFit Sent(string path, string contentType) => new(path, contentType, null);
        public static VisionFrameFit Refused(string error) => new(null, "", error);
    }

    /// <summary>
    /// Slice E.3 — A SCREEN CHECK'S FRAME, UNDER THE SITE'S CAP. ScreenCapture writes a PNG; a PNG
    /// of a large or noisy Game view can reach 8 MiB. Such a frame is decoded and re-encoded as a
    /// JPEG at quality <see cref="JpegQuality"/>; while that is still not under the cap its width and
    /// height are halved (each output pixel the average of a 2x2 block), at most
    /// <see cref="MaxHalvings"/> times. The model reads at most 2576 px on the long edge, so the first
    /// halving of a 4K frame costs it nothing. A frame that still does not fit is refused by name.
    /// Runs on the main thread (Texture2D), once per oversized frame.
    /// </summary>
    public static class VisionFrame
    {
        public const int JpegQuality = 85;
        public const int MaxHalvings = 6;

        public static VisionFrameFit FitUnder(string pngPath, long maxBytes)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(pngPath); }
            catch (Exception e) { return VisionFrameFit.Refused("the frame could not be read: " + e.Message); }
            if (bytes.LongLength < maxBytes) return VisionFrameFit.Sent(pngPath, "image/png");

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(bytes))
                    return VisionFrameFit.Refused("the frame is not an image the kit can decode to shrink it");
                long last = bytes.LongLength;
                var halvings = 0;
                for (; ; halvings++)
                {
                    var jpg = tex.EncodeToJPG(JpegQuality);
                    last = jpg.LongLength;
                    if (last < maxBytes)
                    {
                        var jpgPath = System.IO.Path.ChangeExtension(pngPath, ".jpg");
                        File.WriteAllBytes(jpgPath, jpg);
                        return VisionFrameFit.Sent(jpgPath, "image/jpeg");
                    }
                    if (halvings >= MaxHalvings || tex.width < 2 || tex.height < 2) break;
                    var smaller = Halve(tex);
                    UnityEngine.Object.DestroyImmediate(tex);
                    tex = smaller;
                }
                return VisionFrameFit.Refused(
                    $"it is still {last} bytes as a JPEG at {tex.width}x{tex.height} after {halvings} halvings");
            }
            catch (Exception e)
            {
                return VisionFrameFit.Refused("it could not be shrunk: " + e.Message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>Half the width and height (at least 1 each), each pixel the average of the 2x2
        /// block it covers (clamped at an odd edge). CPU only — no GPU blit.</summary>
        internal static Texture2D Halve(Texture2D src)
        {
            int sw = src.width, sh = src.height;
            int w = Math.Max(1, sw / 2), h = Math.Max(1, sh / 2);
            var from = src.GetPixels32();
            var to = new Color32[w * h];
            for (var y = 0; y < h; y++)
            {
                int y0 = Math.Min(2 * y, sh - 1), y1 = Math.Min(2 * y + 1, sh - 1);
                for (var x = 0; x < w; x++)
                {
                    int x0 = Math.Min(2 * x, sw - 1), x1 = Math.Min(2 * x + 1, sw - 1);
                    Color32 a = from[y0 * sw + x0], b = from[y0 * sw + x1], c = from[y1 * sw + x0], d = from[y1 * sw + x1];
                    to[y * w + x] = new Color32(
                        (byte)((a.r + b.r + c.r + d.r + 2) / 4),
                        (byte)((a.g + b.g + c.g + d.g + 2) / 4),
                        (byte)((a.b + b.b + c.b + d.b + 2) / 4),
                        (byte)((a.a + b.a + c.a + d.a + 2) / 4));
                }
            }
            var dst = new Texture2D(w, h, TextureFormat.RGBA32, false);
            dst.SetPixels32(to);
            dst.Apply(false);
            return dst;
        }
    }
}
