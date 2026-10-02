using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Plan v3.1 phase 6.4 — REPEAT UNTIL A VALUE over several steps: a <c>waitFor</c> with <c>repeatFrom</c> goes back to an
    /// earlier step and runs again until its condition holds, at most <c>times</c> times. Driven through the real director
    /// (one pump per tick, a fake clock), over a fake game whose "level" rises by one each time its cheat runs.
    /// </summary>
    public class DirectorRepeatTests
    {
        private sealed class LevelGame : ICheatBridge, IStateProbe
        {
            public int Level;
            public int Runs;
            // only the shot's own cheat moves the level — the director also sends its own verbs (e.g. show-ui) at start
            public bool Run(string command)
            {
                if (command != "level-up") return true;
                Runs++;
                Level++;
                return true;
            }
            public string? CurrentStateName => "L" + Level;
        }

        private sealed class Recorder : IRecorderDriver
        {
            public string OutputDir { get; }
            public bool IsRecording { get; private set; }
            public int OutputWidth => 0;
            public int OutputHeight => 0;
            public Recorder(string dir) { OutputDir = dir; Directory.CreateDirectory(dir); }
            public void Start(string clipName)
            {
                IsRecording = true;
                File.WriteAllText(Path.Combine(OutputDir, clipName + "_001.mp4"), "clip");
            }
            public void Stop() => IsRecording = false;
        }

        private string _dir = "";
        private double _clock;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "director-repeat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _clock = 0;
        }

        [TearDown]
        public void TearDown()
        {
            AdDirector.Active?.Finish("test teardown");
            Directory.Delete(_dir, recursive: true);
        }

        private AdDirector Run(LevelGame game, int times)
        {
            var shot = new AdShot("roll-to-3", Array.Empty<string>(),
                new[]
                {
                    AdStep.Cheat("level-up"),
                    AdStep.WaitFor(WaitCondition.State("L3"), timeout: 0.5, repeatFrom: 0, times: times),
                },
                WaitCondition.State("L3"));
            var adapter = new GameAdapter
            {
                GameId = "testgame",
                CheatBridge = game,
                StateProbe = game,
                Shots = () => new[] { shot },
            };
            var d = AdDirector.Run(adapter, new[] { shot }, new AdDirector.Options
            {
                AutoPump = false,
                // ONE attempt: the director's whole-shot retry is its own feature — this measures the repeat alone
                MaxAttempts = 1,
                Recorder = new Recorder(_dir),
                Now = () => _clock,
                ReleaseCameraHold = () => { },
            });
            Assert.IsNotNull(d);
            for (var ticks = 0; !d!.IsFinished && ticks < 100_000; ticks++)
            {
                d.PumpOnce();
                _clock += 0.1;
            }
            Assert.IsTrue(d!.IsFinished, "director never finished");
            return d;
        }

        [Test]
        public void RepeatsFromTheEarlierStepUntilTheValueHolds()
        {
            var game = new LevelGame();
            var d = Run(game, times: 5);
            Assert.AreEqual(3, game.Runs, "level-up ran until the level read 3 — and not once more");
            Assert.IsTrue(d.AllCaptured, string.Join("\n", d.LogLines));
            // phase 6 audit: each step keeps ONE mark, at its last run — the take's moment names do not shift with repeats
            var marks = (Newtonsoft.Json.Linq.JArray)Newtonsoft.Json.Linq.JObject.Parse(d.StepMarksJson!)["marks"]!;
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, marks.Select(m => (int)m["i"]!).ToArray(), d.StepMarksJson);
            Assert.Greater((double)marks[0]["atSec"]!, 0.5, "the mark of step 0 is its LAST run, not the first");
        }

        [Test]
        public void StopsWhenTheRepeatsRunOut()
        {
            var game = new LevelGame();
            var d = Run(game, times: 1);
            Assert.AreEqual(2, game.Runs, "the first pass and ONE repeat — then the step fails as any waitFor\n" + string.Join("\n", d.LogLines));
            Assert.IsFalse(d.AllCaptured);
        }
    }
}
