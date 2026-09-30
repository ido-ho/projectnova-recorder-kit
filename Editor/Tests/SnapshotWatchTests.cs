using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace NovaFixtures.WatchData
{
    [Serializable] public class FxCoin { public int Amount; }
    public class FxSave
    {
        public static FxSave? Data;
        public Dictionary<string, FxCoin> Currencies = new();
        public Dictionary<string, int> Noise = new();
        /// <summary>keyed by what look like player ids — never sent in clear</summary>
        public Dictionary<string, int> Friends = new();
        public Dictionary<long, int> ByNumericId = new();

        public static void Reset()
        {
            var d = new FxSave();
            d.Currencies["topaz"] = new FxCoin { Amount = 50 };
            for (var i = 0; i < 80; i++) d.Noise["n" + i] = i;
            d.Friends["Xy3kP9qLmN2vB7tR5wZ1aC4dE6fG"] = 7;      // a base64-ish uid
            d.Friends["0123456789abcdef0123456789abcdef"] = 8;   // a hex id
            d.Friends["has space"] = 9;                          // not writable in the grammar
            d.ByNumericId[1234567890123] = 5;                     // a long numeric id
            Data = d;
        }
    }
    public static class FxWatchCheats
    {
        /// <summary>Topaz +10,000, and 80 other values each moved by a million — topaz is NOT in the kit's top 50.</summary>
        public static void GiveTopazInNoise()
        {
            FxSave.Data!.Currencies["topaz"].Amount += 10000;
            foreach (var k in FxSave.Data.Noise.Keys.ToList()) FxSave.Data.Noise[k] += 1_000_000;
        }
    }
}

namespace ProjectNova.RecorderKit.Tests
{
    using NovaFixtures.WatchData;

    /// <summary>Kit 0.13.2 — the fresh audit's M6 (identifier-like dictionary keys never leave the machine in clear: a salted
    /// short hash stands in, and a `get` still resolves it) and the friction fix (a snapshot check that WATCHES one path,
    /// named before the run: graded from that run's own reads, under the already-ticked `snapshot &lt;root&gt;` lever,
    /// reported apart from the 50-value cap).</summary>
    public class SnapshotWatchTests
    {
        private const string Root = "NovaFixtures.WatchData.FxSave.Data";
        private const string Topaz = Root + ".Currencies[topaz].Amount";
        private const string GiveCmd = "call NovaFixtures.WatchData.FxWatchCheats.GiveTopazInNoise";
        private string _root = "";
        private double _clock;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "snapwatch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
            FxSave.Reset();
            SnapshotRead.BudgetMsForTests = null;
            _clock = 0;
        }

        [TearDown]
        public void TearDown()
        {
            SnapshotRead.BudgetMsForTests = null;
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        // ---- M6: identifier-like keys ------------------------------------------------------------------------------

        [Test]
        public void IdentifierLikeKeysAreSentAsASaltedShortHashAndAGetStillReadsThem()
        {
            var r = SnapshotRead.ReadFull(Root, out var error)!;
            Assert.IsNull(error);
            var friends = r.Leaves.Keys.Where(k => k.StartsWith(Root + ".Friends[")).ToList();
            Assert.AreEqual(3, friends.Count, string.Join(", ", friends));
            foreach (var k in friends.Concat(r.Leaves.Keys.Where(k => k.StartsWith(Root + ".ByNumericId["))))
            {
                StringAssert.IsMatch(@"\[#[0-9a-f]{12}\]$", k);
                Assert.IsTrue(DataPath.IsPath(k), k);
                Assert.IsTrue(GameReflection.TryGetPath(k, out var v, out var err), $"{k}: {err}");
                Assert.AreEqual(r.Leaves[k], SnapshotRead.Number(v!));
            }
            foreach (var clear in new[] { "Xy3kP9qLmN2vB7tR5wZ1aC4dE6fG", "0123456789abcdef", "has space", "1234567890123" })
                Assert.IsFalse(r.Leaves.Keys.Any(k => k.Contains(clear)), $"'{clear}' left the machine in a path");
            Assert.IsTrue(r.Leaves.ContainsKey(Topaz), "an ordinary key (a currency id) stays readable");
            // stable: the same key hashes the same way on the next read
            CollectionAssert.AreEqual(friends, SnapshotRead.ReadFull(Root, out _)!.Leaves.Keys.Where(k => k.StartsWith(Root + ".Friends[")).ToList());
            Assert.IsFalse(GameReflection.TryGetPath(Root + ".Friends[#000000000000]", out _, out var none));
            StringAssert.Contains("no key", none);
        }

        // ---- the watched snapshot ----------------------------------------------------------------------------------

        private static JObject Watch(string watch, string expect = "increase") =>
            new() { ["snapshot"] = Root, ["watch"] = watch, ["expect"] = expect };

        [Test]
        public void AWatchedSnapshotCheckIsReadGradedAndItsLeverIsTheSnapshots()
        {
            var c = CheatCheck.FromJson(Watch(Topaz), out var why);
            Assert.IsNotNull(c, why);
            Assert.AreEqual(CheatCheck.SnapshotLever(Root), c!.Lever, "no `get` tick: the snapshot's own lever");
            Assert.AreEqual(Watch(Topaz).ToString(), c.ToJson().ToString(), "echoed in the sender's own key order");
            Assert.IsNull(CheatCheck.FromJson(Watch("NovaFixtures.WatchData.Other.Coins"), out why));
            StringAssert.Contains("under the snapshot's root", why);
            Assert.IsNull(CheatCheck.FromJson(new JObject { ["snapshot"] = Root, ["watch"] = Topaz }, out why));
            Assert.IsNull(CheatCheck.FromJson(new JObject { ["snapshot"] = Root, ["expect"] = "increase" }, out why));
            Assert.IsNull(CheatCheck.FromJson(new JObject { ["get"] = Topaz, ["expect"] = "increase", ["watch"] = Topaz }, out why));
        }

        private CheatProofRun Pump(JObject check)
        {
            var request = CheatProofRequest.FromJson(new JObject { ["command"] = GiveCmd, ["check"] = check, ["settleSec"] = 1 })!;
            var run = new CheatProofRun(request);
            var frame = 0;
            foreach (var _ in CheatProofRun.Run(run, _root, new GenericCheatBridge(_root, new UguiDriver()), () => _clock))
            {
                _clock += 0.25;
                Assert.Less(frame++, 1000);
            }
            return run;
        }

        [Test]
        public void TheWatchedValueIsReportedApartFromTheCapUnderTheTickedSnapshotLeverOnly()
        {
            Assert.IsNull(Levers.SetApproved(_root, GiveCmd, true));
            Assert.IsNull(Levers.SetApproved(_root, CheatCheck.SnapshotLever(Root), true));
            Assert.IsNull(Levers.SetNonProduction(_root, true));
            var run = Pump(Watch(Topaz));
            Assert.IsTrue(run.Ran && run.RunOk, run.LeverRefused + " | " + string.Join(" | ", run.Log));
            var facts = JObject.Parse(run.ToFactsJson("0.13.2"));
            var changed = ((JArray)facts["changed"]!).Select(x => x["path"]!.Value<string>()).ToList();
            Assert.AreEqual(SnapshotRead.MaxChanged, changed.Count);
            CollectionAssert.DoesNotContain(changed, Topaz, "topaz fell past the cap…");
            Assert.AreEqual(Topaz, facts["watched"]!["path"]!.Value<string>(), "…and is reported on its own");
            Assert.AreEqual(50, facts["watched"]!["before"]!.Value<double>());
            Assert.AreEqual(10050, facts["watched"]!["after"]!.Value<double>());
            Assert.AreEqual(Watch(Topaz).ToString(), facts["check"]!.ToString());
        }

        [Test]
        public void TheWatchedValueIsReadEvenWhenTheWalkIsCutAndAMissingOneStopsTheProofBeforeTheCheat()
        {
            Assert.IsNull(Levers.SetApproved(_root, GiveCmd, true));
            Assert.IsNull(Levers.SetApproved(_root, CheatCheck.SnapshotLever(Root), true));
            Assert.IsNull(Levers.SetNonProduction(_root, true));
            SnapshotRead.BudgetMsForTests = -1; // the walk stops at once — the watched value is still read, by fields
            var facts = JObject.Parse(Pump(Watch(Topaz)).ToFactsJson("0.13.2"));
            Assert.AreEqual(10050, facts["watched"]!["after"]!.Value<double>());
            SnapshotRead.BudgetMsForTests = null;
            // kit 0.13.4: an ENTRY the data does not hold is a state (absent) — the proof runs and grades it; a path that
            // is broken (no such field) is not, and stops the proof before the cheat
            var before = FxSave.Data!.Currencies["topaz"].Amount;
            var absent = Pump(Watch(Root + ".Currencies[ruby].Amount"));
            Assert.IsTrue(absent.Ran, absent.Before?.Error);
            Assert.IsTrue(absent.Before!.WatchedAbsent);
            FxSave.Reset();
            before = FxSave.Data!.Currencies["topaz"].Amount;
            var missing = Pump(Watch(Root + ".NoSuchField.Amount"));
            Assert.IsFalse(missing.Ran, "nothing to grade: the cheat is not run");
            StringAssert.Contains("NoSuchField", missing.Before!.Error);
            Assert.AreEqual(before, FxSave.Data.Currencies["topaz"].Amount);
        }
    }
}
