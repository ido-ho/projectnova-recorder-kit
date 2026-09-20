using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Footage hygiene (invariant 16) for data-driven games: mute the bed, force timescale 1,
    /// apply resist per upcoming shot. A leftover timescale-0 or an unmuted bed fails ready.
    /// HUD-off is not this gate — that waits on a game fork.
    /// </summary>
    public class HygieneReadyGateTests
    {
        private sealed class FakeUi : IUiDriver
        {
            public bool Exists(string name) => false;
            public bool IsInteractable(string name) => false;
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) => false;
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        private sealed class FakeCheats : ICheatBridge
        {
            public readonly List<string> Run_ = new();
            public readonly HashSet<string> Fail = new();
            public bool Run(string command)
            {
                Run_.Add(command);
                foreach (var prefix in Fail)
                    if (command.StartsWith(prefix))
                        return false;
                return true;
            }
        }

        private static HygieneSpec SentaurSpec() => new()
        {
            Mute = "set BattleSceneManager._backgroundMusic.mute true",
            MuteGet = "BattleSceneManager._backgroundMusic.mute",
            ResistOn = "call Player.ApplyDamageResist 1 0",
            ResistOff = "set Player._damageReductionAmount 0",
        };

        private static (HygieneReadyGate gate, DirectorContext ctx, FakeCheats cheats, float[] scale, List<string> log)
            Make(HygieneSpec? spec = null, float scale = 1f, string lastGet = "BattleSceneManager._backgroundMusic.mute = True")
        {
            var held = new[] { scale };
            var cheats = new FakeCheats();
            var log = new List<string>();
            var ctx = new DirectorContext(new FakeUi(), NullStateProbe.Instance, cheats, () => 0, log.Add);
            var gate = new HygieneReadyGate(
                () => spec ?? SentaurSpec(),
                () => held[0],
                v => held[0] = v,
                () => lastGet);
            return (gate, ctx, cheats, held, log);
        }

        private static void Drain(IEnumerable routine)
        {
            foreach (var _ in routine) { }
        }

        private static UpcomingShot Board(string name, bool? resist = null) =>
            new(name, AdShot.BaselineBoard, resist);

        private static UpcomingShot Lobby(string name) =>
            new(name, AdShot.BaselineLobby);

        [Test]
        public void BoardShot_MutesBed_ForcesTimescale1_AppliesDefaultResistOn()
        {
            var (gate, ctx, cheats, scale, log) = Make(scale: 1f);
            ctx.UpcomingShots = new[] { Board("raven_early") };

            Drain(gate.WaitUntilReady(ctx));

            Assert.IsTrue(ctx.LastOpSucceeded);
            Assert.AreEqual(1f, scale[0]);
            CollectionAssert.Contains(cheats.Run_, "set BattleSceneManager._backgroundMusic.mute true");
            CollectionAssert.Contains(cheats.Run_, "get BattleSceneManager._backgroundMusic.mute");
            CollectionAssert.Contains(cheats.Run_, "call Player.ApplyDamageResist 1 0");
            CollectionAssert.DoesNotContain(cheats.Run_, "set Player._damageReductionAmount 0");
            Assert.That(log, Has.Some.Contain("set BattleSceneManager._backgroundMusic.mute true"));
            Assert.That(log, Has.None.Contain("SFX only"));
            Assert.That(log, Has.Some.Contain("timeScale"));
        }

        [Test]
        public void ResistFalse_UsesResistOff_NotResistOn()
        {
            var (gate, ctx, cheats, _, _) = Make();
            ctx.UpcomingShots = new[] { Board("game_over", resist: false) };

            Drain(gate.WaitUntilReady(ctx));

            Assert.IsTrue(ctx.LastOpSucceeded);
            CollectionAssert.Contains(cheats.Run_, "set Player._damageReductionAmount 0");
            CollectionAssert.DoesNotContain(cheats.Run_, "call Player.ApplyDamageResist 1 0");
        }

        [Test]
        public void LobbyOnly_SkipsMuteAndResist_StillRequiresTimescale1()
        {
            var (gate, ctx, cheats, scale, _) = Make(scale: 1f);
            ctx.UpcomingShots = new[] { Lobby("title_hold") };

            Drain(gate.WaitUntilReady(ctx));

            Assert.IsTrue(ctx.LastOpSucceeded);
            Assert.AreEqual(1f, scale[0]);
            CollectionAssert.IsEmpty(cheats.Run_);
        }

        [Test]
        public void BareReady_IsBoardHygiene()
        {
            var (gate, ctx, cheats, _, _) = Make();
            ctx.UpcomingShots = System.Array.Empty<UpcomingShot>();

            Drain(gate.WaitUntilReady(ctx));

            Assert.IsTrue(ctx.LastOpSucceeded);
            CollectionAssert.Contains(cheats.Run_, "set BattleSceneManager._backgroundMusic.mute true");
            CollectionAssert.Contains(cheats.Run_, "call Player.ApplyDamageResist 1 0");
        }

        [Test]
        public void LeftoverTimescale0_FailsReady()
        {
            // Cards / pause / death assign 0 once. Sample on entry — do not write 1 first.
            var (gate, ctx, cheats, _, log) = Make(scale: 0f);
            ctx.UpcomingShots = new[] { Board("raven_early") };

            Drain(gate.WaitUntilReady(ctx));

            Assert.IsFalse(ctx.LastOpSucceeded);
            Assert.That(string.Join("\n", log), Does.Contain("timeScale").And.Contain("0"));
            CollectionAssert.IsEmpty(cheats.Run_);
        }

        [Test]
        public void UnmutedBed_FailsReady()
        {
            var (gate, ctx, _, _, log) = Make(lastGet: "BattleSceneManager._backgroundMusic.mute = False");
            ctx.UpcomingShots = new[] { Board("raven_early") };

            Drain(gate.WaitUntilReady(ctx));

            Assert.IsFalse(ctx.LastOpSucceeded);
            Assert.That(log, Has.Some.Contain("unmuted"));
        }

        [Test]
        public void MuteWriteFailure_FailsReady()
        {
            var (gate, ctx, cheats, _, log) = Make();
            cheats.Fail.Add("set BattleSceneManager._backgroundMusic.mute");
            ctx.UpcomingShots = new[] { Board("raven_early") };

            Drain(gate.WaitUntilReady(ctx));

            Assert.IsFalse(ctx.LastOpSucceeded);
            Assert.That(log, Has.Some.Contain("mute"));
        }

        [Test]
        public void DeclaredEmptyReadyBlock_FailsReady()
        {
            var spec = HygieneSpec.Parse(@"{ ""gameId"": ""sentaur"", ""ready"": { ""mutee"": ""typo"" } }");
            Assert.IsTrue(spec.Declared);
            Assert.IsFalse(spec.HasAny);
            var (gate, ctx, _, _, log) = Make(spec: spec);
            Drain(gate.WaitUntilReady(ctx));
            Assert.IsFalse(ctx.LastOpSucceeded);
            Assert.That(string.Join("\n", log), Does.Contain("unreadable").Or.Contain("empty"));
        }

        [Test]
        public void EmptySpec_IsAlwaysReady()
        {
            var (gate, ctx, cheats, _, _) = Make(spec: new HygieneSpec());
            ctx.UpcomingShots = new[] { Board("raven_early") };

            Drain(gate.WaitUntilReady(ctx));

            Assert.IsTrue(ctx.LastOpSucceeded);
            CollectionAssert.IsEmpty(cheats.Run_);
        }

        [Test]
        public void Load_ReadsReadyBlock_AndNeverThrowsOnJunk()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nova-hygiene-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "Library", "Nova"));
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir, "Library", "Nova", "adapter.json"),
                    @"{ ""gameId"": ""sentaur"", ""ready"": {
                        ""mute"": ""set BattleSceneManager._backgroundMusic.mute true"",
                        ""muteGet"": ""BattleSceneManager._backgroundMusic.mute"",
                        ""resistOn"": ""call Player.ApplyDamageResist 1 0"",
                        ""resistOff"": ""set Player._damageReductionAmount 0""
                    } }");
                var spec = HygieneSpec.Load(dir);
                Assert.IsTrue(spec.HasAny);
                Assert.AreEqual("set BattleSceneManager._backgroundMusic.mute true", spec.Mute);
                Assert.AreEqual("BattleSceneManager._backgroundMusic.mute", spec.MuteGet);
                Assert.AreEqual("call Player.ApplyDamageResist 1 0", spec.ResistOn);
                Assert.AreEqual("set Player._damageReductionAmount 0", spec.ResistOff);

                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir, "Library", "Nova", "adapter.json"),
                    "not-json");
                Assert.DoesNotThrow(() => HygieneSpec.Load(dir));
                Assert.IsFalse(HygieneSpec.Load(dir).HasAny);
            }
            finally
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void LooksTrue_ReadsGetProbeLine()
        {
            Assert.IsTrue(HygieneReadyGate.LooksTrue("BattleSceneManager._backgroundMusic.mute = True"));
            Assert.IsFalse(HygieneReadyGate.LooksTrue("BattleSceneManager._backgroundMusic.mute = False"));
            Assert.IsFalse(HygieneReadyGate.LooksTrue(""));
        }
    }
}
