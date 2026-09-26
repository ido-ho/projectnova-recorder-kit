using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Binding failures must abort BEFORE recording starts. A shot that records first and fails
    /// later leaves a junk file the operator has to recognise and delete.
    /// </summary>
    public class DirectorBindingTests
    {
        private sealed class RecordingCheatBridge : ICheatBridge
        {
            public readonly List<string> Commands = new();
            public bool Run(string command) { Commands.Add(command); return true; }
        }

        private sealed class CountingRecorder : IRecorderDriver
        {
            public string OutputDir { get; }
            public bool IsRecording { get; private set; }
            public int StartCount { get; private set; }
            // 0x0 keeps CaptureAspect quiet in tests, same as DirectorTests' FakeDriver.
            public int OutputWidth => 0;
            public int OutputHeight => 0;

            public CountingRecorder(string? dir = null)
            {
                OutputDir = dir ?? Path.Combine(Path.GetTempPath(),
                    "director-binding-recorder-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(OutputDir);
            }

            public void Start(string clipName)
            {
                IsRecording = true;
                StartCount++;
                File.WriteAllText(Path.Combine(OutputDir, clipName + "_001.mp4"), "clip");
            }
            public void Stop() => IsRecording = false;
        }

        /// <summary>
        /// Resolves every request immediately with "no match", never touching a file. Used only to
        /// keep the mixed cheat+vision test off the real, file-backed IVisionChannel — swapping in
        /// Options.SkipVisionShots instead would skip the WHOLE shot (it is a per-shot gate), which
        /// would hide the very thing that test needs to observe: the shot's non-vision steps still
        /// run and get bound.
        /// </summary>
        private sealed class InertVision : IVisionChannel
        {
            public string? LastPrompt { get; private set; }
            public string? Request(string prompt, int stepIndex) { LastPrompt = prompt; return "inert"; }
            public bool TryGetResult(string id, out bool match) { match = false; return true; }
        }

        private string _outDir = "";
        private double _clock;

        [SetUp]
        public void SetUp()
        {
            _outDir = Path.Combine(Path.GetTempPath(), "director-binding-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_outDir);
            _clock = 0;
        }

        [TearDown]
        public void TearDown()
        {
            AdDirector.Active?.Finish("test teardown");
            Directory.Delete(_outDir, recursive: true);
        }

        private AdDirector RunWith(AdShot shot, RecordingCheatBridge cheats,
            IReadOnlyDictionary<string, string> bindings, CountingRecorder? recorder = null,
            IVisionChannel? vision = null, Action? releaseCameraHold = null)
        {
            var adapter = new GameAdapter
            {
                GameId = "testgame",
                CheatBridge = cheats,
                Shots = () => new[] { shot },
            };
            var d = AdDirector.Run(adapter, new[] { shot }, new AdDirector.Options
            {
                AutoPump = false,
                Bindings = bindings,
                Recorder = recorder ?? new CountingRecorder(_outDir),
                Vision = vision,
                Now = () => _clock,
                ReleaseCameraHold = releaseCameraHold ?? (() => { }),
            });
            Assert.IsNotNull(d);
            PumpToEnd(d!);
            return d!;
        }

        private void PumpToEnd(AdDirector d, int maxTicks = 100_000)
        {
            var ticks = 0;
            while (!d.IsFinished && ticks++ < maxTicks)
            {
                d.PumpOnce();
                _clock += 0.1;
            }
            Assert.IsTrue(d.IsFinished, "director never finished");
        }

        [Test]
        public void BoundParameter_ReachesTheCheatBridge()
        {
            var cheats = new RecordingCheatBridge();
            var shot = new AdShot("s", new[] { "stage-hero {hero}" },
                new[] { AdStep.Wait(0.1) }, WaitCondition.Present("X"),
                parameters: new[] { "hero" });

            var d = RunWith(shot, cheats, new Dictionary<string, string> { ["hero"] = "hero.draco" });

            CollectionAssert.Contains(cheats.Commands, "stage-hero hero.draco");
        }

        [Test]
        public void UnboundParameter_AbortsBeforeRecordingStarts()
        {
            var cheats = new RecordingCheatBridge();
            var recorder = new CountingRecorder();
            var shot = new AdShot("s", new[] { "stage-hero {hero}" },
                new[] { AdStep.Wait(0.1) }, WaitCondition.Present("X"),
                parameters: new[] { "hero" });

            var d = RunWith(shot, cheats, new Dictionary<string, string>(), recorder);

            Assert.AreEqual(0, recorder.StartCount, "no recording may begin on a bad binding");
            StringAssert.Contains("hero", d.Summary);
            Assert.IsFalse(d.AllCaptured);
        }

        [Test]
        public void SetupBindingFailed_AfterAPoseCheat_ReleasesWithoutRecording()
        {
            var cheats = new RecordingCheatBridge();
            var recorder = new CountingRecorder();
            var released = 0;
            var shot = new AdShot("s",
                new[] { "camera-pose 1 2 3 40.84 135 0 20", "stage-hero {hero}" },
                new[] { AdStep.Wait(0.1) }, WaitCondition.Present("X"),
                parameters: new[] { "hero" });

            var d = RunWith(shot, cheats, new Dictionary<string, string>(), recorder,
                releaseCameraHold: () => released++);

            CollectionAssert.Contains(cheats.Commands, "camera-pose 1 2 3 40.84 135 0 20");
            Assert.AreEqual(0, recorder.StartCount, "no recording may begin on a bad binding");
            Assert.GreaterOrEqual(released, 1,
                "setup may already have posed; bindingFailed must release without waiting for Stop");
            Assert.IsFalse(d.AllCaptured);
        }

        [Test]
        public void StepBindingFailed_AfterSetup_ReleasesWithoutRecording()
        {
            var cheats = new RecordingCheatBridge();
            var recorder = new CountingRecorder();
            var released = 0;
            var shot = new AdShot("s",
                new[] { "camera-pose 1 2 3 40.84 135 0 20" },
                new[] { AdStep.Cheat("assert-hero-shown {hero}") }, WaitCondition.Present("X"),
                parameters: new[] { "hero" });

            var d = RunWith(shot, cheats, new Dictionary<string, string>(), recorder,
                releaseCameraHold: () => released++);

            CollectionAssert.Contains(cheats.Commands, "camera-pose 1 2 3 40.84 135 0 20");
            Assert.AreEqual(0, recorder.StartCount);
            Assert.GreaterOrEqual(released, 1,
                "setup posed, then a later step failed to bind — release without recording");
            Assert.IsFalse(d.AllCaptured);
        }

        /// <summary>The ninth audit, M2: the eighth fold's brace guard refused every bound text, so a click name the site
        /// sends (<c>Slot{0}_{i}</c> — it checks braces in LEVERS only) aborted the shot on the studio's machine. A click or
        /// hold name is looked up among the game's objects and never reaches the gate: it binds. A lever does not.</summary>
        [Test]
        public void AClickOrHoldNameMayHoldABraceOutsideAPlaceholderALeverMayNot()
        {
            foreach (var step in new[] { AdStep.Click("Slot{0}_{i}"), AdStep.Hold("Slot{0}_{i}", 0.1) })
            {
                var recorder = new CountingRecorder();
                var shot = new AdShot("c", new string[0], new[] { step }, WaitCondition.Present("X"), parameters: new[] { "i" });
                var d = RunWith(shot, new RecordingCheatBridge(), new Dictionary<string, string> { ["i"] = "3" }, recorder);
                StringAssert.DoesNotContain("ABORT", d.Summary, step.Kind.ToString());
                // the press finds nothing in this fake game, so the director tries again: one take per attempt
                Assert.GreaterOrEqual(recorder.StartCount, 1, $"the {step.Kind} name did not bind, so nothing was recorded");
            }

            foreach (var lever in new[]
                     {
                         new AdShot("l", new[] { "SelectHero {{h}}" }, new[] { AdStep.Wait(0.1) }, WaitCondition.Present("X"),
                             parameters: new[] { "h" }),
                         new AdShot("l", new string[0], new[] { AdStep.Cheat("SelectHero {{h}}") }, WaitCondition.Present("X"),
                             parameters: new[] { "h" }),
                     })
            {
                var cheats = new RecordingCheatBridge();
                var recorder = new CountingRecorder();
                var d = RunWith(lever, cheats, new Dictionary<string, string> { ["h"] = "knight" }, recorder);
                StringAssert.Contains("ABORT l: the lever 'SelectHero {{h}}' holds a '{' or '}' that is not part of a placeholder, " +
                                      "so a bound value would make a command that reads as a placeholder itself", d.Summary);
                CollectionAssert.DoesNotContain(cheats.Commands, "SelectHero {knight}");
                Assert.AreEqual(0, recorder.StartCount, "no recording may begin on a bad binding");
            }
        }

        [Test]
        public void ZeroParameterShot_SendsItsCommandsVerbatim()
        {
            var cheats = new RecordingCheatBridge();
            var shot = new AdShot("s", new[] { "sweep-pointers" },
                new[] { AdStep.Cheat("auto-resolve-any") }, WaitCondition.Present("X"));

            RunWith(shot, cheats, new Dictionary<string, string>());

            CollectionAssert.Contains(cheats.Commands, "sweep-pointers");
            CollectionAssert.Contains(cheats.Commands, "auto-resolve-any");
        }

        [Test]
        public void StepTextIsSubstituted_ButVisionPromptsAreNot()
        {
            var cheats = new RecordingCheatBridge();
            var vision = new InertVision();
            var shot = new AdShot("s", new string[0],
                new[] { AdStep.Cheat("assert-hero-shown {hero}"),
                        AdStep.Vision("a hero named {hero} on screen") },
                WaitCondition.Present("X"),
                parameters: new[] { "hero" });

            RunWith(shot, cheats, new Dictionary<string, string> { ["hero"] = "hero.draco" },
                vision: vision);

            CollectionAssert.Contains(cheats.Commands, "assert-hero-shown hero.draco");
            // The Vision prompt keeps its braces: prose may legitimately contain them.
            StringAssert.Contains("{hero}", vision.LastPrompt);
        }
    }
}
