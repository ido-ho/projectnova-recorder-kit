using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Slice E.3 — a try's SCREEN CHECK, asked of the site. The real probe director
    /// (<c>ProbeRun.DirectorOptions</c>, the overload that takes the shot, and
    /// <see cref="ProbeRun.Start"/>), the real <see cref="HttpVisionChannel"/>, and a FAKE
    /// <see cref="IStudioHttp"/> — the transport seam the agent's other POSTs go through — so nothing
    /// here touches a network. The clock is the test's own, shared by the director and the channel.
    /// </summary>
    public class ProbeVisionTests
    {
        private const string PlainAdapter = @"{ ""gameId"": ""g"" }";
        private const string BaseUrl = "https://site.example/api/";
        private const string Key = "studio-key-1";
        private const string RunId = "run_42";
        private const string VisionUrl = "https://site.example/api/studio/capture-jobs/run_42/vision";
        private const string Prompt = "the chest is open";
        private static readonly Regex IdShape = new("^[A-Za-z0-9_-]{8,64}$");

        /// <summary>A wait, then the screen check at index 1 — never index 0, so an index the
        /// director did not pass cannot pass by luck.</summary>
        private static readonly string LookShot = @"{ ""name"": ""look"", ""steps"": [
  { ""kind"": ""wait"", ""seconds"": 0.2 },
  { ""kind"": ""vision"", ""prompt"": """ + Prompt + @""", ""timeout"": 20 } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";

        private string _root = "";
        private double _clock;
        private byte[] _png = Array.Empty<byte>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "probe-vision-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            File.WriteAllBytes(SyncNova.AdapterFile(_root), new UTF8Encoding(false).GetBytes(PlainAdapter));
            _clock = 0;
            _png = Png(8, 8, noise: false);
        }

        [TearDown]
        public void TearDown()
        {
            AdDirector.Active?.Finish("test teardown");
            LeverGateBridge.ForgetRefused();
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        // ---- fakes -------------------------------------------------------------------------------

        private sealed class FakeResponse : IStudioResponse
        {
            public bool IsDone { get; set; } = true;
            public long Status { get; set; } = 200;
            public string Body { get; set; } = "";
            public string? TransportError { get; set; }
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        private sealed class Post
        {
            public string Url = "";
            public string Key = "";
            public Dictionary<string, string> Fields = new();
            public string? FilePath;
            public string FileField = "";
            public string ContentType = "";
            public byte[]? Bytes;
            public double At;
            public string RequestId => Fields.TryGetValue("requestId", out var v) ? v : "";
            public int Step => int.Parse(Fields["step"]);
        }

        /// <summary>The kit's transport, faked: records every screen-check POST (and the frame bytes
        /// AS SENT) and answers with <see cref="Answer"/>. Any other call is a test failure.</summary>
        private sealed class FakeHttp : IStudioHttp
        {
            private readonly Func<double> _now;
            public FakeHttp(Func<double> now) => _now = now;
            public readonly List<Post> Posts = new();
            public readonly List<FakeResponse> Responses = new();
            public Func<Post, FakeResponse> Answer = p => Verdict(p, true);

            public IStudioResponse PostMultipart(string url, string studioKey, IReadOnlyDictionary<string, string> fields,
                string? filePath, string fileField, string contentType)
            {
                var p = new Post
                {
                    Url = url,
                    Key = studioKey,
                    Fields = fields.ToDictionary(kv => kv.Key, kv => kv.Value),
                    FilePath = filePath,
                    FileField = fileField,
                    ContentType = contentType,
                    Bytes = filePath != null && File.Exists(filePath) ? File.ReadAllBytes(filePath) : null,
                    At = _now(),
                };
                Posts.Add(p);
                var r = Answer(p);
                Responses.Add(r);
                return r;
            }

            public IStudioResponse Get(string url, string studioKey) => throw new AssertionException("unexpected GET " + url);
            public IStudioResponse PostJson(string url, string studioKey, string json) =>
                throw new AssertionException("unexpected POST " + url);
            public IStudioResponse PostFile(string url, string studioKey, string filePath, string fileField,
                IReadOnlyDictionary<string, string> fields) => throw new AssertionException("unexpected upload " + url);
            public IStudioResponse PutFile(string url, string filePath, IReadOnlyDictionary<string, string> headers) =>
                throw new AssertionException("unexpected PUT " + url);
        }

        /// <summary>The site's 200, echoing what was sent.</summary>
        private static FakeResponse Verdict(Post p, bool match) => new()
        {
            Status = 200,
            Body = new JObject { ["requestId"] = p.RequestId, ["step"] = p.Step, ["match"] = match }.ToString(),
        };

        private static FakeResponse Error(long status, string message) => new()
        {
            Status = status,
            Body = new JObject { ["message"] = message, ["error"] = "x", ["statusCode"] = status }.ToString(),
        };

        private sealed class FakeUi : IUiDriver
        {
            public readonly HashSet<string> Names = new() { "Board", "Go" };
            public bool Exists(string name) => Names.Contains(name);
            public bool IsInteractable(string name) => Names.Contains(name);
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) => Names.Contains(name);
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        private sealed class RecordingBridge : ICheatBridge
        {
            public readonly List<string> Run_ = new();
            public bool Run(string command) { Run_.Add(command); return true; }
        }

        // ---- helpers -----------------------------------------------------------------------------

        private static byte[] Png(int w, int h, bool noise, int seed = 7)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                var px = new Color32[w * h];
                var raw = new byte[w * h * 4];
                if (noise) new System.Random(seed).NextBytes(raw);
                for (var i = 0; i < px.Length; i++)
                    px[i] = noise
                        ? new Color32(raw[4 * i], raw[4 * i + 1], raw[4 * i + 2], raw[4 * i + 3])
                        : new Color32(40, 80, 120, 255);
                t.SetPixels32(px);
                t.Apply(false);
                return t.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(t);
            }
        }

        private string FrameDir => RelayPaths.Root(_root);

        private ProbeVisionWire Wire(FakeHttp http, Func<string, string?>? capture = null) =>
            new(http, BaseUrl, Key, RunId, FrameDir)
            {
                Now = () => _clock,
                CaptureFrame = capture ?? (path =>
                {
                    File.WriteAllBytes(path, _png);
                    return null;
                }),
            };

        private ProbePlan Plan(string shot)
        {
            var plan = ProbePlan.Prepare(_root, new ProbeRequest(shot, PlainAdapter,
                SyncNova.Sha256OfText(shot), SyncNova.Sha256OfText(PlainAdapter)));
            Assert.IsNull(plan.Refusal, plan.Refusal);
            Assert.IsTrue(plan.MayRun);
            return plan;
        }

        /// <summary>Run a try of <paramref name="shot"/> the way the agent builds one, pumped by hand.</summary>
        private AdDirector RunTry(string shot, ProbeVisionWire wire, Action<AdDirector>? eachTick = null)
        {
            var plan = Plan(shot);
            var adapter = new GameAdapter
            {
                GameId = "g",
                Ui = new FakeUi(),
                CheatBridge = new RecordingBridge(),
                ReadyGate = NullReadyGate.Instance,
                Recovery = NullRecoveryPolicy.Instance,
            };
            var options = ProbeRun.DirectorOptions(_root, null, plan.Shot!, wire);
            options.AutoPump = false;
            options.Now = () => _clock;
            options.ReleaseCameraHold = () => { };
            var d = ProbeRun.Start(adapter, plan, options);
            Assert.IsNotNull(d);
            var ticks = 0;
            while (!d!.IsFinished && ticks++ < 100_000)
            {
                d.PumpOnce();
                _clock += 0.1;
                eachTick?.Invoke(d);
            }
            Assert.IsTrue(d.IsFinished, "director never finished");
            return d;
        }

        private string[] VisionFramesLeft() =>
            Directory.Exists(FrameDir)
                ? Directory.GetFiles(FrameDir).Where(f => Path.GetFileName(f).Contains(HttpVisionChannel.VisionFrameInfix)).ToArray()
                : Array.Empty<string>();

        private const string Lead = "look attempt 1: Vision '" + Prompt + "' did not resolve — ";

        // ---- the request -------------------------------------------------------------------------

        [Test]
        public void AMatchIsAMatch_AskedAsTheClaimedRun_WithTheWiresThreePartsOnly()
        {
            var http = new FakeHttp(() => _clock);
            var d = RunTry(LookShot, Wire(http));

            Assert.IsTrue(d.AllCaptured, d.Summary);
            Assert.IsNull(d.FailedReason);
            Assert.AreEqual(1, http.Posts.Count, "one question, asked once");
            var p = http.Posts[0];
            Assert.AreEqual(VisionUrl, p.Url, "the CLAIMED run's vision route");
            Assert.AreEqual(Key, p.Key, "the studio key that claimed the run");
            CollectionAssert.AreEquivalent(new[] { "step", "requestId" }, p.Fields.Keys,
                "the text parts are exactly step + requestId — NO prompt (the site reads it from what it sent)");
            Assert.AreEqual("1", p.Fields["step"], "the 0-based index of the vision step in the SENT shot");
            StringAssert.IsMatch(IdShape.ToString(), p.RequestId);
            Assert.AreEqual("frame", p.FileField);
            Assert.AreEqual("image/png", p.ContentType);
            CollectionAssert.AreEqual(_png, p.Bytes, "the frame the channel captured, as captured");
            StringAssert.StartsWith(Path.Combine(FrameDir, ProbeRun.FramePrefix), p.FilePath);
            CollectionAssert.IsEmpty(VisionFramesLeft(), "the frame is deleted once the question is answered");
            Assert.IsTrue(http.Responses[0].Disposed);
        }

        [Test]
        public void ANoFailsTheStep_AndSaysDidNotMatch()
        {
            var http = new FakeHttp(() => _clock) { Answer = p => Verdict(p, false) };
            var d = RunTry(LookShot, Wire(http));

            Assert.IsFalse(d.AllCaptured);
            Assert.AreEqual(1, d.FailedStepIndex);
            Assert.AreEqual("vision", d.FailedStepKindName);
            Assert.AreEqual(Lead + AdDirector.VisionNoMatch, d.FailedReason);
            StringAssert.Contains("did not match", d.FailedReason, "the positive control for the refusal tests");
        }

        [Test]
        public void ARefusalFailsTheStepInTheSitesOwnWords_AndNeverReadsAsDidNotMatch()
        {
            const string said = "this try has used its 1 screen check — the shot has 1 `vision` step, and each is asked once";
            var http = new FakeHttp(() => _clock) { Answer = _ => Error(409, said) };
            var d = RunTry(LookShot, Wire(http));

            Assert.IsFalse(d.AllCaptured);
            Assert.AreEqual(1, d.FailedStepIndex);
            Assert.AreEqual("vision", d.FailedStepKindName);
            Assert.AreEqual(Lead + "the site refused the screen check at step 2 (409): " + said, d.FailedReason);
            StringAssert.DoesNotContain("did not match", d.FailedReason, "a refusal is not a verdict");
            Assert.AreEqual(1, http.Posts.Count, "a refusal is the site deciding — it is not asked again");
            CollectionAssert.IsEmpty(VisionFramesLeft());
        }

        [Test]
        public void AnUnanswerableCheck_422_IsARefusalToo()
        {
            const string said = "the screen check at step 2 could not be answered: the model refused — it is not asked again on this try";
            var http = new FakeHttp(() => _clock) { Answer = _ => Error(422, said) };
            var d = RunTry(LookShot, Wire(http));
            Assert.AreEqual(Lead + "the site refused the screen check at step 2 (422): " + said, d.FailedReason);
            Assert.AreEqual(1, http.Posts.Count);
        }

        [Test]
        public void A5xxIsAskedAgainWithTheSameId_AfterABackoff_ThenFailsByName()
        {
            var http = new FakeHttp(() => _clock)
            {
                Answer = _ => Error(503, "this screen check is still being answered — ask again with the same request id"),
            };
            var d = RunTry(LookShot, Wire(http));

            Assert.AreEqual(HttpVisionChannel.MaxPosts, http.Posts.Count);
            Assert.AreEqual(1, http.Posts.Select(p => p.RequestId).Distinct().Count(),
                "every retry of the question sends the SAME id, so the site bills it once");
            for (var i = 1; i < http.Posts.Count; i++)
                Assert.GreaterOrEqual(http.Posts[i].At - http.Posts[i - 1].At, HttpVisionChannel.BackoffSec[i - 1] - 1e-9,
                    $"retry {i} came before its backoff");
            Assert.AreEqual(Lead + $"the site did not answer the screen check at step 2 after {HttpVisionChannel.MaxPosts} "
                            + "attempts with the same request id (last: HTTP 503: this screen check is still being "
                            + "answered — ask again with the same request id)", d.FailedReason);
            StringAssert.DoesNotContain("did not match", d.FailedReason);
            Assert.IsTrue(http.Responses.All(r => r.Disposed));
        }

        [Test]
        public void NoResponse_ThenAnAnswer_IsTheAnswer_ToTheSameQuestion()
        {
            var n = 0;
            var http = new FakeHttp(() => _clock)
            {
                Answer = p => ++n switch
                {
                    1 => new FakeResponse { Status = 0, TransportError = "Cannot resolve destination host" },
                    2 => Error(500, "<html>bad gateway</html>"),
                    _ => Verdict(p, true),
                },
            };
            var d = RunTry(LookShot, Wire(http));
            Assert.IsTrue(d.AllCaptured, d.Summary);
            Assert.AreEqual(3, http.Posts.Count);
            Assert.AreEqual(1, http.Posts.Select(p => p.RequestId).Distinct().Count());
        }

        [TestCase(429)]
        [TestCase(408)]
        public void ANotNow_408or429_IsAskedAgain_NotARefusal(int status)
        {
            // The kit's one 4xx rule (JobGuard.IsDeterministicAnswer): 408 and 429 are "not now", not
            // "no" — a proxy in front of the site can send them. Same id, and the answer counts.
            var n = 0;
            var http = new FakeHttp(() => _clock)
            {
                Answer = p => ++n == 1 ? Error(status, "slow down") : Verdict(p, true),
            };
            var d = RunTry(LookShot, Wire(http));
            Assert.IsTrue(d.AllCaptured, d.Summary);
            Assert.AreEqual(2, http.Posts.Count);
            Assert.AreEqual(1, http.Posts.Select(p => p.RequestId).Distinct().Count());
        }

        [Test]
        public void EachQuestionHasItsOwnId_AndANewChannelNeverReusesOne()
        {
            var twoLooks = @"{ ""name"": ""two"", ""steps"": [
  { ""kind"": ""vision"", ""prompt"": ""a"", ""timeout"": 20 },
  { ""kind"": ""wait"", ""seconds"": 0.2 },
  { ""kind"": ""vision"", ""prompt"": ""b"", ""timeout"": 20 } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";
            var http = new FakeHttp(() => _clock);
            var d = RunTry(twoLooks, Wire(http));
            Assert.IsTrue(d.AllCaptured, d.Summary);
            Assert.AreEqual(2, http.Posts.Count);
            CollectionAssert.AreEqual(new[] { 0, 2 }, http.Posts.Select(p => p.Step));
            Assert.AreNotEqual(http.Posts[0].RequestId, http.Posts[1].RequestId, "a new question needs a new id");
            Assert.IsTrue(http.Posts.All(p => IdShape.IsMatch(p.RequestId)));

            // The re-run after a domain reload builds a NEW channel for the same run and step: a new
            // frame, so a new question — never the old id (that would return a verdict about a screen
            // that no longer exists).
            var again = new HttpVisionChannel(Wire(http));
            var id = again.Request(Prompt, 0);
            CollectionAssert.DoesNotContain(http.Posts.Select(p => p.RequestId), id);
            again.Dispose();
        }

        [Test]
        public void NoAnswerBeforeTheStepsTimeout_IsANamedReason_AndTheRequestIsClosedWithTheRun()
        {
            var http = new FakeHttp(() => _clock) { Answer = _ => new FakeResponse { IsDone = false } };
            var d = RunTry(LookShot.Replace(@"""timeout"": 20", @"""timeout"": 3"), Wire(http));

            Assert.AreEqual(Lead + AdDirector.VisionTimedOut(3), d.FailedReason);
            StringAssert.DoesNotContain("did not match", d.FailedReason);
            Assert.AreEqual(1, http.Posts.Count);
            Assert.IsTrue(http.Responses[0].Disposed, "a request in flight must not outlive its run");
            CollectionAssert.IsEmpty(VisionFramesLeft());
        }

        [Test]
        public void ALostLease_FinishesTheRun_AndClosesTheQuestionInFlight()
        {
            var http = new FakeHttp(() => _clock) { Answer = _ => new FakeResponse { IsDone = false } };
            var finished = false;
            RunTry(LookShot, Wire(http), d =>
            {
                if (finished || http.Posts.Count == 0) return;
                finished = true;
                d.Finish("the try's lease was lost"); // what RunProbe does when the lease goes
            });
            Assert.IsTrue(finished);
            Assert.IsTrue(http.Responses[0].Disposed);
            CollectionAssert.IsEmpty(VisionFramesLeft());
        }

        [TestCase("id")]
        [TestCase("step")]
        [TestCase("match-string")]
        [TestCase("no-match")]
        [TestCase("not-json")]
        public void AnAnswerThatIsNotAVerdictOnThisQuestion_FailsByName(string how)
        {
            var http = new FakeHttp(() => _clock)
            {
                Answer = p => new FakeResponse
                {
                    Status = 200,
                    Body = how switch
                    {
                        "id" => new JObject { ["requestId"] = "someone-elses-id", ["step"] = p.Step, ["match"] = true }.ToString(),
                        "step" => new JObject { ["requestId"] = p.RequestId, ["step"] = p.Step + 1, ["match"] = true }.ToString(),
                        "match-string" => new JObject { ["requestId"] = p.RequestId, ["step"] = p.Step, ["match"] = "true" }.ToString(),
                        "no-match" => new JObject { ["requestId"] = p.RequestId, ["step"] = p.Step }.ToString(),
                        _ => "<html>ok</html>",
                    },
                },
            };
            var d = RunTry(LookShot, Wire(http));
            Assert.IsFalse(d.AllCaptured, how);
            StringAssert.StartsWith(Lead + "the site's answer to the screen check at step 2", d.FailedReason);
            StringAssert.Contains("not a verdict", d.FailedReason);
            StringAssert.DoesNotContain("did not match", d.FailedReason);
        }

        // ---- the frame ---------------------------------------------------------------------------

        [Test]
        public void AFrameOverTheCap_IsShrunkBeforeItIsSent()
        {
            var http = new FakeHttp(() => _clock);
            var fitted = new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 };
            long capAsked = 0;
            var wire = Wire(http, path =>
            {
                File.WriteAllBytes(path, new byte[HttpVisionChannel.MaxFrameBytes + 1]);
                return null;
            });
            wire.FitFrame = (path, cap) =>
            {
                capAsked = cap;
                var jpg = Path.ChangeExtension(path, ".jpg");
                File.WriteAllBytes(jpg, fitted);
                return VisionFrameFit.Sent(jpg, "image/jpeg");
            };
            var d = RunTry(LookShot, wire);

            Assert.IsTrue(d.AllCaptured, d.Summary);
            Assert.AreEqual(8L * 1024 * 1024, capAsked, "the site's cap: 8 MiB");
            Assert.AreEqual(1, http.Posts.Count);
            Assert.AreEqual("image/jpeg", http.Posts[0].ContentType);
            CollectionAssert.AreEqual(fitted, http.Posts[0].Bytes, "the SHRUNK frame is what is sent");
            Assert.IsEmpty(Directory.GetFiles(FrameDir).Where(f => Path.GetFileName(f).StartsWith(ProbeRun.FramePrefix)),
                "both the frame and its shrunk copy are deleted");
        }

        [Test]
        public void AFrameAtTheCap_IsShrunk()
        {
            // The first audit of E.3, K-N1: the site's upload limit (multer) refuses a file that
            // REACHES 8 MiB, so a frame goes up only when it is strictly UNDER the cap.
            var http = new FakeHttp(() => _clock);
            var fitted = new byte[] { 0xFF, 0xD8, 0xFF, 9, 8, 7 };
            long capAsked = 0;
            var wire = Wire(http, path =>
            {
                File.WriteAllBytes(path, new byte[HttpVisionChannel.MaxFrameBytes]);
                return null;
            });
            wire.FitFrame = (path, cap) =>
            {
                capAsked = cap;
                var jpg = Path.ChangeExtension(path, ".jpg");
                File.WriteAllBytes(jpg, fitted);
                return VisionFrameFit.Sent(jpg, "image/jpeg");
            };
            var d = RunTry(LookShot, wire);
            Assert.IsTrue(d.AllCaptured, d.Summary);
            Assert.AreEqual(HttpVisionChannel.MaxFrameBytes, capAsked, "a frame AT the cap is shrunk");
            Assert.AreEqual(1, http.Posts.Count);
            Assert.AreEqual("image/jpeg", http.Posts[0].ContentType);
            CollectionAssert.AreEqual(fitted, http.Posts[0].Bytes, "the SHRUNK frame is what is sent");
        }

        [Test]
        public void AFrameShrunkOnlyToTheCap_IsRefusedByName_AndNothingIsAsked()
        {
            var http = new FakeHttp(() => _clock);
            var wire = Wire(http, path =>
            {
                File.WriteAllBytes(path, new byte[HttpVisionChannel.MaxFrameBytes + 1]);
                return null;
            });
            wire.FitFrame = (path, _) =>
            {
                var jpg = Path.ChangeExtension(path, ".jpg");
                File.WriteAllBytes(jpg, new byte[HttpVisionChannel.MaxFrameBytes]);
                return VisionFrameFit.Sent(jpg, "image/jpeg");
            };
            var d = RunTry(LookShot, wire);

            Assert.AreEqual(0, http.Posts.Count, "a frame of exactly 8 MiB is not sent");
            Assert.AreEqual(Lead + $"the screen check at step 2: the frame is {HttpVisionChannel.MaxFrameBytes + 1} bytes "
                            + $"and still {HttpVisionChannel.MaxFrameBytes} bytes after shrinking — not under the "
                            + $"{HttpVisionChannel.MaxFrameBytes} bytes (8 MiB) the site accepts; nothing was asked", d.FailedReason);
        }

        [Test]
        public void AFrameThatCannotBeShrunk_IsRefusedByName_AndNothingIsAsked()
        {
            var http = new FakeHttp(() => _clock);
            var wire = Wire(http, path =>
            {
                File.WriteAllBytes(path, new byte[HttpVisionChannel.MaxFrameBytes + 1]);
                return null;
            });
            wire.FitFrame = (_, _) => VisionFrameFit.Refused("it is still 9000000 bytes as a JPEG at 1x1 after 6 halvings");
            var d = RunTry(LookShot, wire);

            Assert.AreEqual(0, http.Posts.Count, "nothing over the cap is sent");
            Assert.AreEqual(Lead + $"the screen check at step 2: the frame is {HttpVisionChannel.MaxFrameBytes + 1} bytes, "
                            + $"not under the {HttpVisionChannel.MaxFrameBytes} bytes (8 MiB) the site accepts — it is still "
                            + "9000000 bytes as a JPEG at 1x1 after 6 halvings — nothing was asked", d.FailedReason);
            CollectionAssert.IsEmpty(VisionFramesLeft());
        }

        [Test]
        public void NoFrameWithinTheWait_IsNamed_AndNothingIsAsked()
        {
            var http = new FakeHttp(() => _clock);
            var d = RunTry(LookShot, Wire(http, _ => null)); // asked, but Unity never wrote it
            Assert.AreEqual(0, http.Posts.Count);
            Assert.AreEqual(Lead + "the screen check at step 2: no frame of the screen was written within "
                            + "10 s — nothing was asked", d.FailedReason);
        }

        [Test]
        public void AFrameTheScreenRefused_IsNamed_AndNothingIsAsked()
        {
            var http = new FakeHttp(() => _clock);
            var d = RunTry(LookShot, Wire(http, _ => "ScreenCapture refused the frame: no game view"));
            Assert.AreEqual(0, http.Posts.Count);
            Assert.AreEqual(Lead + "the screen check at step 2: ScreenCapture refused the frame: no game view "
                            + "— nothing was asked", d.FailedReason);
        }

        [Test]
        public void FitUnder_ReEncodesARealPngOverTheCap_AndLeavesOneUnderItAlone()
        {
            var png = Png(256, 256, noise: true);
            var path = Path.Combine(FrameDir, "probe-frame-x-vision-fit.png");
            Directory.CreateDirectory(FrameDir);
            File.WriteAllBytes(path, png);

            var asIs = VisionFrame.FitUnder(path, png.LongLength + 1);
            Assert.IsNull(asIs.Error);
            Assert.AreEqual(path, asIs.Path, "a frame under the cap is sent as it is");
            Assert.AreEqual("image/png", asIs.ContentType);
            var atCap = VisionFrame.FitUnder(path, png.LongLength);
            Assert.IsNull(atCap.Error, atCap.Error);
            Assert.AreEqual("image/jpeg", atCap.ContentType, "a frame AT the cap is re-encoded (K-N1)");

            // A cap one byte under this frame's own full-size JPEG: the re-encode alone cannot fit,
            // so exactly one halving must — 256x256 goes up as 128x128.
            long fullJpeg;
            var probe = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(probe.LoadImage(png));
                fullJpeg = probe.EncodeToJPG(VisionFrame.JpegQuality).LongLength;
            }
            finally { UnityEngine.Object.DestroyImmediate(probe); }
            var cap = fullJpeg - 1;
            Assert.Greater(png.LongLength, cap, "precondition: the PNG is over this cap");
            var fit = VisionFrame.FitUnder(path, cap);
            Assert.IsNull(fit.Error, fit.Error);
            Assert.AreEqual("image/jpeg", fit.ContentType);
            var bytes = File.ReadAllBytes(fit.Path!);
            Assert.Less(bytes.LongLength, cap);
            var t = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(t.LoadImage(bytes), "the shrunk frame is a real image");
                Assert.AreEqual(128, t.width, "halved once");
                Assert.AreEqual(128, t.height);
            }
            finally { UnityEngine.Object.DestroyImmediate(t); }
        }

        [Test]
        public void FitUnder_AJpegOfExactlyTheCap_IsHalvedAgain()
        {
            var png = Png(256, 256, noise: true);
            var path = Path.Combine(FrameDir, "probe-frame-x-vision-exact.png");
            Directory.CreateDirectory(FrameDir);
            File.WriteAllBytes(path, png);
            long fullJpeg;
            var probe = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(probe.LoadImage(png));
                fullJpeg = probe.EncodeToJPG(VisionFrame.JpegQuality).LongLength;
            }
            finally { UnityEngine.Object.DestroyImmediate(probe); }
            Assert.Greater(png.LongLength, fullJpeg, "precondition: the PNG is over this cap");
            var fit = VisionFrame.FitUnder(path, fullJpeg);
            Assert.IsNull(fit.Error, fit.Error);
            var t = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(t.LoadImage(File.ReadAllBytes(fit.Path!)));
                Assert.AreEqual(128, t.width, "the full-size JPEG is exactly the cap, not under it: halved once");
            }
            finally { UnityEngine.Object.DestroyImmediate(t); }
        }

        [Test]
        public void FitUnder_AtTheRealCap_AFrameOver8MiBGoesUpUnder8MiB()
        {
            var png = Png(2048, 2048, noise: true);
            Assert.Greater(png.LongLength, HttpVisionChannel.MaxFrameBytes, "precondition: a noise PNG over 8 MiB");
            Directory.CreateDirectory(FrameDir);
            var path = Path.Combine(FrameDir, "probe-frame-x-vision-big.png");
            File.WriteAllBytes(path, png);
            var fit = VisionFrame.FitUnder(path, HttpVisionChannel.MaxFrameBytes);
            Assert.IsNull(fit.Error, fit.Error);
            Assert.AreEqual("image/jpeg", fit.ContentType);
            Assert.LessOrEqual(new FileInfo(fit.Path!).Length, HttpVisionChannel.MaxFrameBytes);
        }

        [Test]
        public void FitUnder_RefusesByName_WhenNothingFits()
        {
            var png = Png(256, 256, noise: true);
            Directory.CreateDirectory(FrameDir);
            var path = Path.Combine(FrameDir, "probe-frame-x-vision-tiny.png");
            File.WriteAllBytes(path, png);
            var fit = VisionFrame.FitUnder(path, 10);
            Assert.IsNull(fit.Path);
            StringAssert.StartsWith("it is still ", fit.Error);
            // bounded: 256 -> 4 px after six halvings, then it stops and says so
            StringAssert.EndsWith($"as a JPEG at 4x4 after {VisionFrame.MaxHalvings} halvings", fit.Error);
        }

        [Test]
        public void TheSweepTakesAScreenChecksFrame_EvenOfTheRunInFlight()
        {
            Directory.CreateDirectory(FrameDir);
            var tryFrame = ProbeRun.FramePath(_root, RunId);
            var mine = HttpVisionChannel.FramePath(FrameDir, RunId, "abcdef0123456789");
            var old = HttpVisionChannel.FramePath(FrameDir, "run_old", "0123456789abcdef");
            foreach (var f in new[] { tryFrame, mine, old }) File.WriteAllBytes(f, _png);
            ProbeRun.SweepStrayFrames(_root, RunId);
            Assert.IsTrue(File.Exists(tryFrame), "the in-flight try's own frame is kept");
            Assert.IsFalse(File.Exists(mine));
            Assert.IsFalse(File.Exists(old));
        }

        // ---- the channel, asked directly ---------------------------------------------------------

        /// <summary>Poll one question until it ends (or the budget runs out), the director's way:
        /// once per tick, on the shared clock.</summary>
        private VisionPoll PollToEnd(HttpVisionChannel ch, string id, Action<int>? eachPoll = null)
        {
            var poll = VisionPoll.Pending;
            for (var i = 0; i < 1000 && poll.IsPending; i++)
            {
                poll = ch.Poll(id);
                eachPoll?.Invoke(i);
                _clock += 0.1;
            }
            return poll;
        }

        [Test]
        public void AQuestionWithNoStepIndex_AsksNothing_AndSaysWhy()
        {
            var http = new FakeHttp(() => _clock);
            var captured = 0;
            var ch = new HttpVisionChannel(Wire(http, path =>
            {
                captured++;
                File.WriteAllBytes(path, _png);
                return null;
            }));
            var id = ch.Request(Prompt, -1);
            var poll = PollToEnd(ch, id!);
            Assert.AreEqual("the screen check was asked without its step's index — nothing was asked", poll.Failure);
            Assert.IsNull(poll.Match);
            Assert.AreEqual(0, captured, "no frame is taken");
            Assert.AreEqual(0, http.Posts.Count, "nothing is asked");
            ch.Dispose();
        }

        [Test]
        public void TryGetResult_IsTrueOnlyForAnAnswer_NeverForARefusal()
        {
            var refused = new FakeHttp(() => _clock) { Answer = _ => Error(409, "no") };
            var ch = new HttpVisionChannel(Wire(refused));
            var id = ch.Request(Prompt, 1)!;
            Assert.IsNotNull(PollToEnd(ch, id).Failure, "precondition: the question ended in a refusal");
            Assert.IsFalse(ch.TryGetResult(id, out var m), "a refusal is not an answer");
            Assert.IsFalse(m);
            ch.Dispose();

            foreach (var said in new[] { true, false })
            {
                var answered = new FakeHttp(() => _clock) { Answer = p => Verdict(p, said) };
                var ch2 = new HttpVisionChannel(Wire(answered));
                var id2 = ch2.Request(Prompt, 1)!;
                Assert.AreEqual(said, PollToEnd(ch2, id2).Match);
                Assert.IsTrue(ch2.TryGetResult(id2, out var m2), "an answer is an answer");
                Assert.AreEqual(said, m2);
                ch2.Dispose();
            }
        }

        [Test]
        public void AHalfWrittenFrame_IsNotSent_UntilItsSizeHolds()
        {
            // The frame lands at the END of a frame (Unity's rule) and can be seen half-written; the
            // channel sends it only once its size has held for StableFramePolls polls.
            var full = Png(64, 64, noise: true);
            var half = full.Take(full.Length / 2).ToArray();
            var http = new FakeHttp(() => _clock);
            string? framePath = null;
            var ch = new HttpVisionChannel(Wire(http, path =>
            {
                framePath = path;
                File.WriteAllBytes(path, half);
                return null;
            }));
            var id = ch.Request(Prompt, 1)!;
            var poll = PollToEnd(ch, id, i =>
            {
                if (i == 0) File.WriteAllBytes(framePath!, full); // the rest of the frame lands
            });
            Assert.AreEqual(true, poll.Match, poll.Failure);
            Assert.AreEqual(1, http.Posts.Count);
            CollectionAssert.AreEqual(full, http.Posts[0].Bytes, "the WHOLE frame is sent, never the half first seen");
            ch.Dispose();
        }

        // ---- wiring and headers ------------------------------------------------------------------

        [Test]
        public void OnlyATryWithVisionSteps_AsksTheSite_AsTheClaimedJob()
        {
            var http = new FakeHttp(() => _clock);
            var wire = Wire(http);
            var withVision = ProbeRun.DirectorOptions(_root, null, Plan(LookShot).Shot!, wire);
            Assert.IsInstanceOf<HttpVisionChannel>(withVision.Vision);
            var channel = (HttpVisionChannel)withVision.Vision!;
            Assert.AreSame(wire, channel.Wire);
            Assert.AreEqual(RunId, channel.Wire.RunId);
            Assert.AreEqual(Key, channel.Wire.StudioKey);
            Assert.AreSame(http, channel.Wire.Http, "the kit's own transport — the headers every job route sends");

            var plain = @"{ ""name"": ""plain"", ""steps"": [ { ""kind"": ""wait"", ""seconds"": 0.2 } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";
            var without = ProbeRun.DirectorOptions(_root, null, Plan(plain).Shot!, wire);
            Assert.IsNull(without.Vision, "a try with no screen check asks no one (the director's file default, unused)");
            Assert.IsTrue(without.CloudContent);
            Assert.AreEqual(1, without.MaxAttempts);
        }

        [Test]
        public void EveryStudioCallCarriesTheKeyTheKitVersionAndTheBoundWorkspace()
        {
            using var req = new UnityWebRequest(VisionUrl, UnityWebRequest.kHttpVerbPOST);
            UnityStudioHttp.ApplyStudioHeaders(req, Key, "ws_7");
            Assert.AreEqual("Bearer " + Key, req.GetRequestHeader("Authorization"));
            Assert.AreEqual(KitVersion.Current, req.GetRequestHeader(KitVersion.Header));
            Assert.AreEqual("ws_7", req.GetRequestHeader(KitVersion.WorkspaceHeader));
            Assert.AreEqual("application/json", req.GetRequestHeader("Accept"));

            using var unbound = new UnityWebRequest(VisionUrl, UnityWebRequest.kHttpVerbPOST);
            UnityStudioHttp.ApplyStudioHeaders(unbound, Key, "");
            Assert.IsTrue(string.IsNullOrEmpty(unbound.GetRequestHeader(KitVersion.WorkspaceHeader)),
                "an unbound project sends no workspace header");
        }

        [Test]
        public void MessageOf_ReadsTheSitesSentence()
        {
            Assert.AreEqual("no", HttpVisionChannel.MessageOf(@"{ ""message"": ""no"", ""statusCode"": 409 }"));
            Assert.AreEqual("a; b", HttpVisionChannel.MessageOf(@"{ ""message"": [""a"", ""b""] }"));
            Assert.AreEqual("<html>x</html>", HttpVisionChannel.MessageOf("<html>x</html>"));
            Assert.AreEqual("(no message)", HttpVisionChannel.MessageOf(""));
        }
    }
}
