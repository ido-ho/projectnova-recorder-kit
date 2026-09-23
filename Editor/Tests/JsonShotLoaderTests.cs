using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    public class JsonShotLoaderConditionTests
    {
        private static WaitCondition Parse(string json)
        {
            var errors = new List<string>();
            Assert.IsTrue(JsonShotLoader.TryParseCondition(JToken.Parse(json), "test", out var c, errors),
                "expected a successful parse, got: " + string.Join("; ", errors));
            CollectionAssert.IsEmpty(errors);
            return c;
        }

        private static List<string> ParseErrors(string json)
        {
            var errors = new List<string>();
            Assert.IsFalse(JsonShotLoader.TryParseCondition(JToken.Parse(json), "test", out _, errors));
            CollectionAssert.IsNotEmpty(errors);
            return errors;
        }

        [Test]
        public void Present_Absent_Interactable_CarryTheirName()
        {
            var present = Parse(@"{ ""kind"": ""present"", ""name"": ""RollBTN"" }");
            Assert.AreEqual(WaitKind.Present, present.Kind);
            Assert.AreEqual("RollBTN", present.ElementName);

            Assert.AreEqual(WaitKind.Absent, Parse(@"{ ""kind"": ""absent"", ""name"": ""X"" }").Kind);
            Assert.AreEqual(WaitKind.Interactable,
                Parse(@"{ ""kind"": ""interactable"", ""name"": ""X"" }").Kind);
        }

        [Test]
        public void TextContains_CarriesNameAndSubstring()
        {
            var c = Parse(@"{ ""kind"": ""textContains"", ""name"": ""TextMultiply"", ""substring"": ""1,000K"" }");
            Assert.AreEqual(WaitKind.TextContains, c.Kind);
            Assert.AreEqual("TextMultiply", c.ElementName);
            Assert.AreEqual("1,000K", c.Substring);
        }

        [Test]
        public void State_CarriesTheStateName()
        {
            var c = Parse(@"{ ""kind"": ""state"", ""name"": ""GameplayScene/Settled"" }");
            Assert.AreEqual(WaitKind.State, c.Kind);
            Assert.AreEqual("GameplayScene/Settled", c.ElementName);
        }

        /// <summary>The real settle conditions this format has to carry are conjunctions, and one of
        /// them (roguelegend's) nests another All inside itself.</summary>
        [Test]
        public void All_NestsArbitrarilyDeep()
        {
            var c = Parse(@"{
                ""kind"": ""all"",
                ""parts"": [
                    { ""kind"": ""present"", ""name"": ""RollBTN"" },
                    { ""kind"": ""all"", ""parts"": [
                        { ""kind"": ""absent"", ""name"": ""CollectBtn"" },
                        { ""kind"": ""absent"", ""name"": ""TapToText"" }
                    ] }
                ]
            }");
            Assert.AreEqual(WaitKind.All, c.Kind);
            Assert.AreEqual(2, c.Parts!.Length);
            Assert.AreEqual(WaitKind.All, c.Parts[1].Kind);
            Assert.AreEqual(2, c.Parts[1].Parts!.Length);
        }

        [Test]
        public void UnknownKind_IsAnErrorNamingTheKindAndLocation()
        {
            var errors = ParseErrors(@"{ ""kind"": ""presnet"", ""name"": ""X"" }");
            Assert.That(errors[0], Does.Contain("presnet"));
            Assert.That(errors[0], Does.Contain("test"));
        }

        [Test]
        public void MissingKind_IsAnError()
        {
            CollectionAssert.IsNotEmpty(ParseErrors(@"{ ""name"": ""X"" }"));
        }

        [Test]
        public void MissingName_IsAnError()
        {
            CollectionAssert.IsNotEmpty(ParseErrors(@"{ ""kind"": ""present"" }"));
        }

        /// <summary>An empty All is vacuously true, which as a settle condition means "captured, always".
        /// ShotGrammar documents that semantic deliberately; a hand-authored empty parts array is far
        /// more likely a mistake, so the loader rejects it rather than silently passing every shot.</summary>
        [Test]
        public void AllWithNoParts_IsAnError()
        {
            CollectionAssert.IsNotEmpty(ParseErrors(@"{ ""kind"": ""all"", ""parts"": [] }"));
        }

        /// <summary>Adversarial JSON must produce an error, never an exception — Newtonsoft's
        /// Value&lt;string&gt;() throws InvalidCastException when the token is an object/array
        /// rather than a string.</summary>
        [Test]
        public void KindAsWrongJsonType_IsAnErrorNotAnException()
        {
            Assert.DoesNotThrow(() =>
            {
                var errors = new List<string>();
                JsonShotLoader.TryParseCondition(JToken.Parse(@"{ ""kind"": {} }"), "test", out _, errors);
                CollectionAssert.IsNotEmpty(errors);
            });
        }
    }

    public class JsonShotLoaderStepTests
    {
        private static AdStep Parse(string json)
        {
            var errors = new List<string>();
            Assert.IsTrue(JsonShotLoader.TryParseStep(JToken.Parse(json), "test", out var s, errors),
                "expected a successful parse, got: " + string.Join("; ", errors));
            CollectionAssert.IsEmpty(errors);
            return s;
        }

        /// <summary>
        /// THE defaults test. Newtonsoft deserializes a missing double to 0, and 0 is actively harmful
        /// here: retryEvery 0 on a click-until re-clicks every pump for the whole window (real click
        /// spam — SNL has 9 such steps), and timeout 0 on a cheatUntil never fires the cheat at all.
        /// Every default below must equal the AdStep factory default it replaces.
        /// </summary>
        [Test]
        public void OmittedNumerics_TakeTheCSharpFactoryDefaults()
        {
            var click = Parse(@"{ ""kind"": ""click"", ""name"": ""RollBTN"" }");
            Assert.AreEqual(0, click.Index);
            Assert.AreEqual(6, click.TimeoutSec);
            Assert.AreEqual(0.6, click.RetryEverySec);
            Assert.IsFalse(click.Until.HasValue);

            var waitFor = Parse(@"{ ""kind"": ""waitFor"", ""condition"": { ""kind"": ""present"", ""name"": ""X"" } }");
            Assert.AreEqual(8, waitFor.TimeoutSec);

            var cheatUntil = Parse(@"{ ""kind"": ""cheatUntil"", ""command"": ""auto-resolve"",
                ""until"": { ""kind"": ""present"", ""name"": ""X"" } }");
            Assert.AreEqual(30, cheatUntil.TimeoutSec);
            Assert.AreEqual(1.5, cheatUntil.RetryEverySec);

            var vision = Parse(@"{ ""kind"": ""vision"", ""prompt"": ""a board"" }");
            Assert.AreEqual(60, vision.TimeoutSec);

            var hold = Parse(@"{ ""kind"": ""hold"", ""name"": ""RollBTN"", ""seconds"": 3.4 }");
            Assert.AreEqual(0, hold.Index);
        }

        [Test]
        public void ExplicitNumerics_OverrideTheDefaults()
        {
            var click = Parse(@"{ ""kind"": ""click"", ""name"": ""Menu"", ""index"": 2,
                ""timeout"": 12, ""retryEvery"": 0.25,
                ""until"": { ""kind"": ""present"", ""name"": ""Rank"" } }");
            Assert.AreEqual(2, click.Index);
            Assert.AreEqual(12, click.TimeoutSec);
            Assert.AreEqual(0.25, click.RetryEverySec);
            Assert.IsTrue(click.Until.HasValue);
            Assert.AreEqual("Rank", click.Until!.Value.ElementName);
        }

        [Test]
        public void EveryKind_RoundTrips()
        {
            Assert.AreEqual(AdStepKind.Click, Parse(@"{ ""kind"": ""click"", ""name"": ""X"" }").Kind);

            var hold = Parse(@"{ ""kind"": ""hold"", ""name"": ""X"", ""seconds"": 3.4 }");
            Assert.AreEqual(AdStepKind.Hold, hold.Kind);
            Assert.AreEqual(3.4, hold.Number);

            var wait = Parse(@"{ ""kind"": ""wait"", ""seconds"": 9 }");
            Assert.AreEqual(AdStepKind.Wait, wait.Kind);
            Assert.AreEqual(9, wait.Number);

            Assert.AreEqual(AdStepKind.WaitFor, Parse(
                @"{ ""kind"": ""waitFor"", ""condition"": { ""kind"": ""present"", ""name"": ""X"" } }").Kind);

            var cheat = Parse(@"{ ""kind"": ""cheat"", ""command"": ""StopAutoRoll"" }");
            Assert.AreEqual(AdStepKind.Cheat, cheat.Kind);
            Assert.AreEqual("StopAutoRoll", cheat.Text);

            Assert.AreEqual(AdStepKind.CheatUntil, Parse(
                @"{ ""kind"": ""cheatUntil"", ""command"": ""c"", ""until"": { ""kind"": ""present"", ""name"": ""X"" } }").Kind);

            var ts = Parse(@"{ ""kind"": ""timeScale"", ""factor"": 2 }");
            Assert.AreEqual(AdStepKind.TimeScale, ts.Kind);
            Assert.AreEqual(2, ts.Number);

            var vision = Parse(@"{ ""kind"": ""vision"", ""prompt"": ""a colorful board"" }");
            Assert.AreEqual(AdStepKind.Vision, vision.Kind);
            Assert.AreEqual("a colorful board", vision.Text);
        }

        /// <summary>A note is rationale, not behavior — it must parse and be ignored.</summary>
        [Test]
        public void Note_IsAcceptedAndDoesNotAffectTheStep()
        {
            var withNote = Parse(@"{ ""kind"": ""wait"", ""seconds"": 9, ""note"": ""clears the 6.2s handler"" }");
            Assert.AreEqual(AdStepKind.Wait, withNote.Kind);
            Assert.AreEqual(9, withNote.Number);
        }

        [Test]
        public void UnknownStepKind_IsAnErrorNamingIt()
        {
            var errors = new List<string>();
            Assert.IsFalse(JsonShotLoader.TryParseStep(
                JToken.Parse(@"{ ""kind"": ""clcik"", ""name"": ""X"" }"), "test", out _, errors));
            Assert.That(errors[0], Does.Contain("clcik"));
        }

        [Test]
        public void MissingRequiredFieldForAKind_IsAnError()
        {
            var errors = new List<string>();
            // click with no name
            Assert.IsFalse(JsonShotLoader.TryParseStep(
                JToken.Parse(@"{ ""kind"": ""click"" }"), "test", out _, errors));
            // cheat with no command
            Assert.IsFalse(JsonShotLoader.TryParseStep(
                JToken.Parse(@"{ ""kind"": ""cheat"" }"), "test", out _, errors));
            // cheatUntil with no until condition
            Assert.IsFalse(JsonShotLoader.TryParseStep(
                JToken.Parse(@"{ ""kind"": ""cheatUntil"", ""command"": ""c"" }"), "test", out _, errors));
            // waitFor with no condition
            Assert.IsFalse(JsonShotLoader.TryParseStep(
                JToken.Parse(@"{ ""kind"": ""waitFor"" }"), "test", out _, errors));
            // wait with no seconds
            Assert.IsFalse(JsonShotLoader.TryParseStep(
                JToken.Parse(@"{ ""kind"": ""wait"" }"), "test", out _, errors));
            Assert.AreEqual(5, errors.Count);
        }

        /// <summary>Adversarial JSON must produce an error, never an exception — the step-level
        /// twin of JsonShotLoaderConditionTests.KindAsWrongJsonType_IsAnErrorNotAnException.</summary>
        [Test]
        public void KindAsWrongJsonType_IsAnErrorNotAnException()
        {
            Assert.DoesNotThrow(() =>
            {
                var errors = new List<string>();
                JsonShotLoader.TryParseStep(JToken.Parse(@"{ ""kind"": {} }"), "test", out _, errors);
                CollectionAssert.IsNotEmpty(errors);
            });
        }

        /// <summary>Adversarial JSON must produce an error, never an exception — Newtonsoft's
        /// Value&lt;double&gt;() throws FormatException for a non-numeric string. "wait" requires
        /// seconds; a non-numeric value should read as absent and fail the required-field check,
        /// not crash.</summary>
        [Test]
        public void NonNumericFieldValue_IsHandledWithoutThrowing()
        {
            Assert.DoesNotThrow(() =>
            {
                var errors = new List<string>();
                JsonShotLoader.TryParseStep(
                    JToken.Parse(@"{ ""kind"": ""wait"", ""seconds"": ""abc"" }"), "test", out var step, errors);
                CollectionAssert.IsNotEmpty(errors);
            });
        }
    }

    public class JsonShotLoaderShotTests
    {
        private const string OneGoodShot = @"{
            ""$schemaVersion"": 1,
            ""shots"": [
                {
                    ""name"": ""board_bigwin"",
                    ""note"": ""best-effort; the cheat can fall through to a Social tile"",
                    ""setup"": [ ""RollTargetType LargeCoin"" ],
                    ""steps"": [
                        { ""kind"": ""click"", ""name"": ""RollBTN"" },
                        { ""kind"": ""wait"", ""seconds"": 9, ""note"": ""clears the 6.2s handler"" }
                    ],
                    ""settle"": { ""kind"": ""all"", ""parts"": [
                        { ""kind"": ""present"", ""name"": ""RollBTN"" },
                        { ""kind"": ""absent"", ""name"": ""CollectBtn"" }
                    ] },
                    ""expectState"": ""BoardState"",
                    ""settleTimeoutSec"": 15
                }
            ]
        }";

        [Test]
        public void LoadFrom_ParsesAWholeShot()
        {
            var result = JsonShotLoader.LoadFrom(OneGoodShot);
            CollectionAssert.IsEmpty(result.Errors);
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(1, result.Shots.Count);

            var shot = result.Shots[0];
            Assert.AreEqual("board_bigwin", shot.Name);
            CollectionAssert.AreEqual(new[] { "RollTargetType LargeCoin" }, shot.Setup);
            Assert.AreEqual(2, shot.Steps.Count);
            Assert.AreEqual(AdStepKind.Click, shot.Steps[0].Kind);
            Assert.AreEqual(9, shot.Steps[1].Number);
            Assert.AreEqual(WaitKind.All, shot.Settle.Kind);
            Assert.AreEqual("BoardState", shot.ExpectState);
            Assert.AreEqual(15, shot.SettleTimeoutSec);
        }

        [Test]
        public void OmittedShotFields_TakeTheCSharpDefaults()
        {
            var result = JsonShotLoader.LoadFrom(@"{
                ""$schemaVersion"": 1,
                ""shots"": [ {
                    ""name"": ""minimal"",
                    ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                    ""settle"": { ""kind"": ""present"", ""name"": ""X"" }
                } ]
            }");
            CollectionAssert.IsEmpty(result.Errors);
            var shot = result.Shots[0];
            CollectionAssert.IsEmpty(shot.Setup);
            Assert.IsNull(shot.ExpectState);
            Assert.AreEqual(8, shot.SettleTimeoutSec);
            Assert.IsFalse(shot.ArmCondition.HasValue);
            Assert.AreEqual(15, shot.ArmTimeoutSec);
            CollectionAssert.IsEmpty(shot.Parameters);
            // An omitted baseline is the board — a bare shot starts on the primary play surface.
            Assert.AreEqual(AdShot.BaselineBoard, shot.Baseline);
            Assert.IsNull(shot.Resist);
        }

        [Test]
        public void Resist_RoundTrips()
        {
            var result = JsonShotLoader.LoadFrom(@"{
                ""$schemaVersion"": 1,
                ""shots"": [ {
                    ""name"": ""die"",
                    ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                    ""settle"": { ""kind"": ""present"", ""name"": ""X"" },
                    ""resist"": false
                } ]
            }");
            CollectionAssert.IsEmpty(result.Errors);
            Assert.AreEqual(false, result.Shots[0].Resist);
        }

        [Test]
        public void MalformedResist_IsAnErrorNotAnException()
        {
            Assert.DoesNotThrow(() =>
            {
                var result = JsonShotLoader.LoadFrom(@"{
                    ""$schemaVersion"": 1,
                    ""shots"": [ {
                        ""name"": ""die"",
                        ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                        ""settle"": { ""kind"": ""present"", ""name"": ""X"" },
                        ""resist"": ""no""
                    } ]
                }");
                CollectionAssert.IsEmpty(result.Shots);
                Assert.That(result.Errors[0], Does.Contain("resist"));
            });
        }

        [Test]
        public void Baseline_RoundTrips()
        {
            var result = JsonShotLoader.LoadFrom(@"{
                ""$schemaVersion"": 1,
                ""shots"": [ {
                    ""name"": ""menu"",
                    ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                    ""settle"": { ""kind"": ""present"", ""name"": ""X"" },
                    ""baseline"": ""lobby""
                } ]
            }");
            CollectionAssert.IsEmpty(result.Errors);
            Assert.AreEqual(AdShot.BaselineLobby, result.Shots[0].Baseline);
        }

        /// <summary>The whole point of the field: a typo must be caught loudly, not routed onto the
        /// wrong baseline in silence the way a stale name-list once did.</summary>
        [Test]
        public void UnknownBaseline_IsAnErrorNamingItAndTheKnownValues()
        {
            var result = JsonShotLoader.LoadFrom(@"{
                ""$schemaVersion"": 1,
                ""shots"": [ {
                    ""name"": ""menu"",
                    ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                    ""settle"": { ""kind"": ""present"", ""name"": ""X"" },
                    ""baseline"": ""loby""
                } ]
            }");
            CollectionAssert.IsEmpty(result.Shots);
            Assert.That(result.Errors[0], Does.Contain("loby"));
            Assert.That(result.Errors[0], Does.Contain("lobby"));
            Assert.That(result.Errors[0], Does.Contain("board"));
        }

        [Test]
        public void MalformedBaseline_IsAnErrorNotAnException()
        {
            Assert.DoesNotThrow(() =>
            {
                var result = JsonShotLoader.LoadFrom(@"{
                    ""$schemaVersion"": 1,
                    ""shots"": [ {
                        ""name"": ""menu"",
                        ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                        ""settle"": { ""kind"": ""present"", ""name"": ""X"" },
                        ""baseline"": {}
                    } ]
                }");
                CollectionAssert.IsEmpty(result.Shots);
                CollectionAssert.IsNotEmpty(result.Errors);
            });
        }

        [Test]
        public void ArmAndParameters_RoundTrip()
        {
            var result = JsonShotLoader.LoadFrom(@"{
                ""$schemaVersion"": 1,
                ""shots"": [ {
                    ""name"": ""staged"",
                    ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                    ""settle"": { ""kind"": ""present"", ""name"": ""X"" },
                    ""arm"": { ""kind"": ""present"", ""name"": ""Lobby"" },
                    ""armTimeoutSec"": 20,
                    ""parameters"": [ ""hero"", ""level"" ]
                } ]
            }");
            CollectionAssert.IsEmpty(result.Errors);
            var shot = result.Shots[0];
            Assert.IsTrue(shot.ArmCondition.HasValue);
            Assert.AreEqual("Lobby", shot.ArmCondition!.Value.ElementName);
            Assert.AreEqual(20, shot.ArmTimeoutSec);
            CollectionAssert.AreEqual(new[] { "hero", "level" }, shot.Parameters);
        }

        /// <summary>
        /// settle is REQUIRED and must never default. WaitCondition is a non-nullable struct, so an
        /// omitted settle would become default(WaitCondition) == Present(""), which probe.Exists("")
        /// never satisfies — every shot would fail its capture check with no error anywhere.
        /// </summary>
        [Test]
        public void MissingSettle_IsAnError_NotADefault()
        {
            var result = JsonShotLoader.LoadFrom(@"{
                ""$schemaVersion"": 1,
                ""shots"": [ { ""name"": ""x"", ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ] } ]
            }");
            Assert.IsFalse(result.Ok);
            Assert.That(result.Errors[0], Does.Contain("settle"));
            Assert.That(result.Errors[0], Does.Contain("x"));
        }

        [Test]
        public void MissingNameOrSteps_IsAnError()
        {
            var noName = JsonShotLoader.LoadFrom(@"{ ""$schemaVersion"": 1, ""shots"": [ {
                ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                ""settle"": { ""kind"": ""present"", ""name"": ""X"" } } ] }");
            Assert.IsFalse(noName.Ok);

            var noSteps = JsonShotLoader.LoadFrom(@"{ ""$schemaVersion"": 1, ""shots"": [ {
                ""name"": ""x"", ""settle"": { ""kind"": ""present"", ""name"": ""X"" } } ] }");
            Assert.IsFalse(noSteps.Ok);

            var emptySteps = JsonShotLoader.LoadFrom(@"{ ""$schemaVersion"": 1, ""shots"": [ {
                ""name"": ""x"", ""steps"": [],
                ""settle"": { ""kind"": ""present"", ""name"": ""X"" } } ] }");
            Assert.IsFalse(emptySteps.Ok);
        }

        [Test]
        public void DuplicateShotNames_AreAnError()
        {
            var result = JsonShotLoader.LoadFrom(@"{ ""$schemaVersion"": 1, ""shots"": [
                { ""name"": ""dup"", ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                  ""settle"": { ""kind"": ""present"", ""name"": ""X"" } },
                { ""name"": ""dup"", ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                  ""settle"": { ""kind"": ""present"", ""name"": ""X"" } } ] }");
            Assert.IsFalse(result.Ok);
            Assert.That(result.Errors[0], Does.Contain("dup"));
        }

        [Test]
        public void UnsupportedSchemaVersion_IsAnError()
        {
            var result = JsonShotLoader.LoadFrom(@"{ ""$schemaVersion"": 2, ""shots"": [] }");
            Assert.IsFalse(result.Ok);
            Assert.That(result.Errors[0], Does.Contain("2"));
        }

        /// <summary>
        /// The loader NEVER throws: a caught exception would surface as an opaque type name
        /// ("InvalidCastException") instead of a specific, actionable message, and throwing mid-parse
        /// would abort the whole shots array — discarding every shot that DID parse correctly along
        /// with the one that didn't.
        /// </summary>
        [Test]
        public void MalformedJson_YieldsErrorsNotAnException()
        {
            var result = JsonShotLoader.LoadFrom("{ this is not json");
            Assert.IsFalse(result.Ok);
            CollectionAssert.IsNotEmpty(result.Errors);
            CollectionAssert.IsEmpty(result.Shots);
        }

        [Test]
        public void MissingFile_YieldsErrorsNotAnException()
        {
            var result = JsonShotLoader.Load("Tools/AdRecorder/definitely-not-here.json");
            Assert.IsFalse(result.Ok);
            CollectionAssert.IsNotEmpty(result.Errors);
            CollectionAssert.IsEmpty(result.Shots);
        }

        /// <summary>One bad shot must not silently discard the good ones — the operator needs to see
        /// both what loaded and what didn't.</summary>
        [Test]
        public void OneBadShot_DoesNotDiscardTheGoodOnes()
        {
            var result = JsonShotLoader.LoadFrom(@"{ ""$schemaVersion"": 1, ""shots"": [
                { ""name"": ""good"", ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                  ""settle"": { ""kind"": ""present"", ""name"": ""X"" } },
                { ""name"": ""bad"", ""steps"": [ { ""kind"": ""nonsense"" } ],
                  ""settle"": { ""kind"": ""present"", ""name"": ""X"" } } ] }");
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, result.Shots.Count);
            Assert.AreEqual("good", result.Shots[0].Name);
        }

        /// <summary>The companion to OneBadShot_DoesNotDiscardTheGoodOnes above, which only exercises an
        /// unknown step kind — a path that was already safe. This exercises a genuinely malformed field
        /// (name as an array) that used to throw InvalidCastException mid-loop, aborting the whole array
        /// and discarding every shot parsed before it, including good ones already collected.</summary>
        [Test]
        public void OneBadShot_WithAMalformedField_StillDoesNotDiscardTheGoodOnes()
        {
            var result = JsonShotLoader.LoadFrom(@"{ ""$schemaVersion"": 1, ""shots"": [
                { ""name"": ""good"", ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                  ""settle"": { ""kind"": ""present"", ""name"": ""X"" } },
                { ""name"": [ ""oops"" ], ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                  ""settle"": { ""kind"": ""present"", ""name"": ""X"" } } ] }");
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, result.Shots.Count);
            Assert.AreEqual("good", result.Shots[0].Name);
        }

        /// <summary>Adversarial JSON must produce an error, never an exception — Newtonsoft's
        /// Value&lt;int&gt;() throws InvalidCastException when $schemaVersion is an object/array rather
        /// than an integer.</summary>
        [Test]
        public void MalformedSchemaVersion_IsAnErrorNotAnException()
        {
            Assert.DoesNotThrow(() =>
            {
                var result = JsonShotLoader.LoadFrom(@"{ ""$schemaVersion"": {}, ""shots"": [] }");
                Assert.IsFalse(result.Ok);
            });
        }

        /// <summary>Adversarial JSON must produce an error, never an exception — the shot-level twin of
        /// KindAsWrongJsonType_IsAnErrorNotAnException, covering expectState (and, via StringArray,
        /// setup/parameters elements) rather than a condition/step kind.</summary>
        [Test]
        public void MalformedExpectStateOrArrayElement_IsAnErrorNotAnException()
        {
            Assert.DoesNotThrow(() =>
            {
                var result = JsonShotLoader.LoadFrom(@"{ ""$schemaVersion"": 1, ""shots"": [ {
                    ""name"": ""x"", ""expectState"": {},
                    ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
                    ""settle"": { ""kind"": ""present"", ""name"": ""X"" } } ] }");
                Assert.IsFalse(result.Ok);
            });
        }

        [Test]
        public void LastErrors_ReflectsTheMostRecentLoad()
        {
            JsonShotLoader.LoadFrom("{ not json");
            CollectionAssert.IsNotEmpty(JsonShotLoader.LastErrors);
            JsonShotLoader.LoadFrom(OneGoodShot);
            CollectionAssert.IsEmpty(JsonShotLoader.LastErrors);
        }

        // ---- the twelfth audit (fresh context, 2026-09-22) ----------------------------------------------------------

        private static string OneShotWith(string step, string shotFields = "") =>
            "{\"$schemaVersion\":1,\"shots\":[{\"name\":\"s\",\"steps\":[" + step + "]," +
            "\"settle\":{\"kind\":\"present\",\"name\":\"X\"}" + shotFields + "}]}";

        /// <summary>Every field the loader reads as SECONDS, as the one-shot document that carries it at a given value.</summary>
        private static readonly (string Label, string Where, string Field, Func<string, string> Doc)[] SecondsFields =
        {
            ("click.timeout", "shots[0] ('s').steps[0]", "timeout", v => OneShotWith("{\"kind\":\"click\",\"name\":\"A\",\"timeout\":" + v + "}")),
            ("click.retryEvery", "shots[0] ('s').steps[0]", "retryEvery", v => OneShotWith("{\"kind\":\"click\",\"name\":\"A\",\"until\":{\"kind\":\"present\",\"name\":\"B\"},\"retryEvery\":" + v + "}")),
            ("hold.seconds", "shots[0] ('s').steps[0]", "seconds", v => OneShotWith("{\"kind\":\"hold\",\"name\":\"A\",\"seconds\":" + v + "}")),
            ("wait.seconds", "shots[0] ('s').steps[0]", "seconds", v => OneShotWith("{\"kind\":\"wait\",\"seconds\":" + v + "}")),
            ("waitFor.timeout", "shots[0] ('s').steps[0]", "timeout", v => OneShotWith("{\"kind\":\"waitFor\",\"condition\":{\"kind\":\"present\",\"name\":\"B\"},\"timeout\":" + v + "}")),
            ("cheatUntil.timeout", "shots[0] ('s').steps[0]", "timeout", v => OneShotWith("{\"kind\":\"cheatUntil\",\"command\":\"set Player.coins 5\",\"until\":{\"kind\":\"present\",\"name\":\"B\"},\"timeout\":" + v + "}")),
            ("cheatUntil.retryEvery", "shots[0] ('s').steps[0]", "retryEvery", v => OneShotWith("{\"kind\":\"cheatUntil\",\"command\":\"set Player.coins 5\",\"until\":{\"kind\":\"present\",\"name\":\"B\"},\"retryEvery\":" + v + "}")),
            ("vision.timeout", "shots[0] ('s').steps[0]", "timeout", v => OneShotWith("{\"kind\":\"vision\",\"prompt\":\"is it open\",\"timeout\":" + v + "}")),
            ("shot.settleTimeoutSec", "shots[0] ('s')", "settleTimeoutSec", v => OneShotWith("{\"kind\":\"wait\",\"seconds\":1}", ",\"settleTimeoutSec\":" + v)),
            ("shot.armTimeoutSec", "shots[0] ('s')", "armTimeoutSec", v => OneShotWith("{\"kind\":\"wait\",\"seconds\":1}", ",\"arm\":{\"kind\":\"present\",\"name\":\"B\"},\"armTimeoutSec\":" + v)),
        };

        /// <summary>
        /// The twelfth audit, S1 — A NUMBER OF SECONDS IS FINITE AND AT LEAST 0.1, OR THE SHOT IS REFUSED, BY NAME. A
        /// delivered cheatUntil with retryEvery 0 (or below, or NaN — a literal this kit's reader takes) froze the editor
        /// for its whole timeout; a timeout of Infinity never ends. Every field the loader reads as seconds is held to the
        /// same rule, in one sentence, and the delivery check — which is this loader — refuses the file before writing it.
        /// </summary>
        [Test]
        public void EverySecondsFieldBelowTheFloorOrNotFiniteIsRefusedByName()
        {
            var failures = new List<string>();
            foreach (var (label, where, field, doc) in SecondsFields)
            {
                var want = $"{where}: '{field}' must be a finite number of seconds, at least 0.1";
                foreach (var bad in new[] { "0", "-1", "0.09", "-0.0", "NaN", "Infinity", "-Infinity" })
                {
                    var r = JsonShotLoader.LoadFrom(doc(bad));
                    if (r.Shots.Count != 0 || r.Errors.Count != 1 || r.Errors[0] != want)
                        failures.Add($"{label} = {bad}: [{string.Join(" | ", r.Errors)}], {r.Shots.Count} shot(s)");
                }
                // CONTROL: the floor itself loads, and so does a field left out (its default is well above the floor). The
                // exponent form is 6e2, the ceiling, since the thirteenth audit's M2 (it was 1e3, which is over it now).
                foreach (var good in new[] { "0.1", "1", "6e2" })
                {
                    var r = JsonShotLoader.LoadFrom(doc(good));
                    if (r.Shots.Count != 1 || r.Errors.Count != 0)
                        failures.Add($"{label} = {good} (control): [{string.Join(" | ", r.Errors)}]");
                }
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
            Assert.AreEqual(0.1, JsonShotLoader.MinSeconds);
        }

        /// <summary>
        /// The twelfth audit, M1 — A SHOT NAME LONGER THAN THE SITE'S CAP IS REFUSED BY ITS LENGTH, WITHOUT THE NAME. The
        /// loader built "shots[i] ('name').steps[j]" for every step, so a 263,799-character name over 9,300 steps cost 1.9 s
        /// to deliver and 0.85 s on every load. The site refuses a name over 80 characters; the kit now refuses the same, at
        /// the name, before any step is read, and its sentence does not repeat the name.
        /// </summary>
        [Test]
        public void AShotNameLongerThanTheCapIsRefusedByItsLengthAloneBeforeAnyStep()
        {
            Assert.AreEqual(80, JsonShotLoader.MaxShotName);
            string Named(string name) => "{\"$schemaVersion\":1,\"shots\":[{\"name\":\"" + name + "\",\"steps\":[{\"kind\":\"wait\",\"seconds\":1}]," +
                                         "\"settle\":{\"kind\":\"present\",\"name\":\"X\"}}]}";
            var over = JsonShotLoader.LoadFrom(Named(new string('a', 81)));
            CollectionAssert.IsEmpty(over.Shots);
            CollectionAssert.AreEqual(new[] { "shots[0]: the shot name is 81 characters long, and this kit takes names of at most 80" },
                over.Errors.ToArray());
            // CONTROL: at the cap it loads
            var at = JsonShotLoader.LoadFrom(Named(new string('a', 80)));
            CollectionAssert.IsEmpty(at.Errors);
            Assert.AreEqual(new string('a', 80), at.Shots.Single().Name);
            // the refusal comes before the steps: a name at the cap with a broken step is refused for the step, one over it
            // for the name alone
            var brokenStep = "{\"$schemaVersion\":1,\"shots\":[{\"name\":\"NAME\",\"steps\":[{\"kind\":\"clcik\"}],\"settle\":{\"kind\":\"present\",\"name\":\"X\"}}]}";
            StringAssert.EndsWith("unknown step kind 'clcik'", JsonShotLoader.LoadFrom(brokenStep.Replace("NAME", new string('b', 80))).Errors.Single());
            Assert.AreEqual("shots[0]: the shot name is 81 characters long, and this kit takes names of at most 80",
                JsonShotLoader.LoadFrom(brokenStep.Replace("NAME", new string('b', 81))).Errors.Single());
        }

        /// <summary>
        /// The thirteenth audit, M2 — A NUMBER OF SECONDS IS AT MOST 600, OR THE SHOT IS REFUSED, BY NAME. A delivered wait
        /// of 1e300 s never ended, and the capture agent waited on the director for ever. Every field read as seconds, in one
        /// sentence, checked after the floor: a number that is not finite keeps the floor's sentence.
        /// </summary>
        [Test]
        public void EverySecondsFieldAboveTheCeilingIsRefusedByName()
        {
            Assert.AreEqual(600, JsonShotLoader.MaxSeconds);
            var failures = new List<string>();
            foreach (var (label, where, field, doc) in SecondsFields)
            {
                var want = $"{where}: '{field}' must be at most 600 seconds";
                foreach (var bad in new[] { "600.001", "601", "1e300" })
                {
                    var r = JsonShotLoader.LoadFrom(doc(bad));
                    if (r.Shots.Count != 0 || r.Errors.Count != 1 || r.Errors[0] != want)
                        failures.Add($"{label} = {bad}: [{string.Join(" | ", r.Errors)}], {r.Shots.Count} shot(s)");
                }
                // CONTROL: the ceiling itself loads, written either way
                foreach (var good in new[] { "600", "6e2", "599.99" })
                {
                    var r = JsonShotLoader.LoadFrom(doc(good));
                    if (r.Shots.Count != 1 || r.Errors.Count != 0)
                        failures.Add($"{label} = {good} (control): [{string.Join(" | ", r.Errors)}]");
                }
                // Infinity is refused by the floor's sentence, not this one: it is not a finite number at all
                var inf = JsonShotLoader.LoadFrom(doc("Infinity"));
                if (inf.Errors.Count != 1 || inf.Errors[0] != $"{where}: '{field}' must be a finite number of seconds, at least 0.1")
                    failures.Add($"{label} = Infinity: [{string.Join(" | ", inf.Errors)}]");
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        private static string ShotWithLists(int setup, int steps, string firstStep = "{\"kind\":\"wait\",\"seconds\":1}") =>
            "{\"$schemaVersion\":1,\"shots\":[{\"name\":\"s\",\"setup\":[" +
            string.Join(",", Enumerable.Repeat("\"set Player.coins 5\"", setup)) + "],\"steps\":[" +
            string.Join(",", new[] { firstStep }.Concat(Enumerable.Repeat("{\"kind\":\"wait\",\"seconds\":1}", steps - 1))) +
            "],\"settle\":{\"kind\":\"present\",\"name\":\"X\"}}]}";

        /// <summary>
        /// The thirteenth audit, S1 — A SHOT'S SETUP AND STEPS ARE CAPPED BY COUNT, AT THE SHOT, BEFORE ANY STEP. The lever
        /// cap counts DISTINCT commands, so 16,355 copies of one command were one lever: a 212,897-byte file whose setup froze
        /// the editor for 21.9 s in one tick. The director yields after each command and each step now; these caps bound how
        /// many there are. Both sentences are the site's too (the shared grammar fixture), and steps come first.
        /// </summary>
        [Test]
        public void ASetupOrAStepListOverItsCapIsRefusedByCountBeforeAnyStep()
        {
            Assert.AreEqual(500, JsonShotLoader.MaxSetup);
            Assert.AreEqual(500, JsonShotLoader.MaxSteps);
            CollectionAssert.AreEqual(new[] { "shots[0] ('s'): the shot has 501 steps, and this kit takes at most 500" },
                JsonShotLoader.LoadFrom(ShotWithLists(0, 501)).Errors.ToArray());
            CollectionAssert.AreEqual(new[] { "shots[0] ('s'): the shot has 501 setup commands, and this kit takes at most 500" },
                JsonShotLoader.LoadFrom(ShotWithLists(501, 1)).Errors.ToArray());
            // both over: the steps are counted first
            CollectionAssert.AreEqual(new[] { "shots[0] ('s'): the shot has 501 steps, and this kit takes at most 500" },
                JsonShotLoader.LoadFrom(ShotWithLists(501, 501)).Errors.ToArray());
            // before any step: over the cap, a broken first step is not even read
            Assert.AreEqual("shots[0] ('s'): the shot has 501 steps, and this kit takes at most 500",
                JsonShotLoader.LoadFrom(ShotWithLists(0, 501, "{\"kind\":\"clcik\"}")).Errors.Single());
            StringAssert.EndsWith("unknown step kind 'clcik'",
                JsonShotLoader.LoadFrom(ShotWithLists(0, 500, "{\"kind\":\"clcik\"}")).Errors.Single());
            // CONTROL: at both caps the shot loads, with every command and step it lists
            var at = JsonShotLoader.LoadFrom(ShotWithLists(500, 500));
            CollectionAssert.IsEmpty(at.Errors);
            Assert.AreEqual(500, at.Shots.Single().Setup.Count);
            Assert.AreEqual(500, at.Shots.Single().Steps.Count);
        }

        private static string Parts(int n, string name = "N") =>
            string.Join(",", Enumerable.Range(0, n).Select(i => "{\"kind\":\"absent\",\"name\":\"" + name + i + "\"}"));

        private static string AllOf(string parts) => "{\"kind\":\"all\",\"parts\":[" + parts + "]}";

        private static string WithSettle(string settle, string step = "{\"kind\":\"wait\",\"seconds\":1}") =>
            "{\"$schemaVersion\":1,\"shots\":[{\"name\":\"s\",\"steps\":[" + step + "],\"settle\":" + settle + "}]}";

        /// <summary>
        /// The thirteenth audit, S2 — ONE CONDITION HOLDS AT MOST 64 PARTS, COUNTED OVER ITS WHOLE TREE. One evaluation of a
        /// condition runs inside one editor tick and asks the UI about every part (a 2,255-part <c>all</c>: 1,984 ms on a
        /// 3,000-node canvas), so a yield cannot split it; the count can. Every <c>all</c>'s parts count, nested ones too:
        /// counted per array, nesting would let eight levels of 64 through. Refused at the condition's own address.
        /// </summary>
        [Test]
        public void AConditionOverThePartsCapIsRefusedCountedOverItsWholeTree()
        {
            Assert.AreEqual(64, JsonShotLoader.MaxConditionParts);
            const string over = "the condition holds more than 64 parts, counting the parts of every 'all' inside it";
            CollectionAssert.AreEqual(new[] { "shots[0] ('s').settle: " + over },
                JsonShotLoader.LoadFrom(WithSettle(AllOf(Parts(65)))).Errors.ToArray());
            // NESTED: no single array holds more than 32, and the tree holds 2 + 32 + 32 = 66
            CollectionAssert.AreEqual(new[] { "shots[0] ('s').settle: " + over },
                JsonShotLoader.LoadFrom(WithSettle(AllOf(AllOf(Parts(32, "a")) + "," + AllOf(Parts(32, "b"))))).Errors.ToArray());
            // every place a condition is read: waitFor, click's and cheatUntil's until, the shot's arm
            CollectionAssert.AreEqual(new[] { "shots[0] ('s').steps[0].condition: " + over },
                JsonShotLoader.LoadFrom(WithSettle("{\"kind\":\"present\",\"name\":\"X\"}",
                    "{\"kind\":\"waitFor\",\"condition\":" + AllOf(Parts(65)) + "}")).Errors.ToArray());
            CollectionAssert.AreEqual(new[] { "shots[0] ('s').steps[0].until: " + over },
                JsonShotLoader.LoadFrom(WithSettle("{\"kind\":\"present\",\"name\":\"X\"}",
                    "{\"kind\":\"click\",\"name\":\"A\",\"until\":" + AllOf(Parts(65)) + "}")).Errors.ToArray());
            CollectionAssert.AreEqual(new[] { "shots[0] ('s').steps[0].until: " + over },
                JsonShotLoader.LoadFrom(WithSettle("{\"kind\":\"present\",\"name\":\"X\"}",
                    "{\"kind\":\"cheatUntil\",\"command\":\"set Player.coins 5\",\"until\":" + AllOf(Parts(65)) + "}")).Errors.ToArray());
            var arm = "{\"$schemaVersion\":1,\"shots\":[{\"name\":\"s\",\"steps\":[{\"kind\":\"wait\",\"seconds\":1}]," +
                      "\"settle\":{\"kind\":\"present\",\"name\":\"X\"},\"arm\":" + AllOf(Parts(65)) + "}]}";
            CollectionAssert.AreEqual(new[] { "shots[0] ('s').arm: " + over }, JsonShotLoader.LoadFrom(arm).Errors.ToArray());
            // CONTROL: 64 loads, flat or nested (2 + 31 + 31), and each condition has its own count
            CollectionAssert.IsEmpty(JsonShotLoader.LoadFrom(WithSettle(AllOf(Parts(64)))).Errors);
            CollectionAssert.IsEmpty(JsonShotLoader.LoadFrom(WithSettle(AllOf(AllOf(Parts(31, "a")) + "," + AllOf(Parts(31, "b"))))).Errors);
            CollectionAssert.IsEmpty(JsonShotLoader.LoadFrom(WithSettle(AllOf(Parts(64)),
                "{\"kind\":\"waitFor\",\"condition\":" + AllOf(Parts(64)) + "}")).Errors);
        }
    }

    public class JsonShotLoaderPathTests
    {
        [Test]
        public void LoadFromPath_MissingFile_IsAnEmptyCatalogNotAnError()
        {
            var missing = Path.Combine(Path.GetTempPath(), "nova-no-such-shots-" + Path.GetRandomFileName() + ".json");
            var result = JsonShotLoader.LoadFromPath(missing);
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(0, result.Shots.Count);
            CollectionAssert.IsEmpty(result.Errors);
        }

        [Test]
        public void LoadFromPath_ReadsARealFile()
        {
            var path = Path.Combine(Path.GetTempPath(), "nova-shots-" + Path.GetRandomFileName() + ".json");
            File.WriteAllText(path, @"{ ""$schemaVersion"": 1, ""shots"": [] }");
            try
            {
                var result = JsonShotLoader.LoadFromPath(path);
                Assert.IsTrue(result.Ok);
                Assert.AreEqual(0, result.Shots.Count);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ---- K5: what the SITE accepts, this loader must not refuse --------------------------------

        private const string BigIntShot =
            "{ \"$schemaVersion\": 1, \"shots\": [ " +
            "{ \"name\": \"good\", \"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], " +
            "\"settle\": { \"kind\": \"present\", \"name\": \"X\" } }, " +
            "{ \"name\": \"big\", \"steps\": [ { \"kind\": \"wait\", \"seconds\": 100000000000000000000 } ], " +
            "\"settle\": { \"kind\": \"present\", \"name\": \"X\" } } ] }";

        /// <summary>
        /// An integer past long.MaxValue is a BigInteger to Newtonsoft, and Value&lt;double&gt;()
        /// throws on one. Thrown INSIDE the loader that "never throws", it took the whole file with
        /// it — the good shot above included — and reported it as "could not read the file".
        /// </summary>
        /// <remarks>Since the thirteenth audit (M2) a number of seconds is at most 600, so the huge one is refused by that
        /// sentence — which is how this shows it was read as a number (read as absent, it would load with its default) — and
        /// the good shot still loads beside it. A huge integer that loads is the shared grammar fixture's timeScale case.</remarks>
        private const string BigIntCeiling = "shots[1] ('big').steps[0]: 'seconds' must be at most 600 seconds";

        [Test]
        public void AHugeIntegerIsANumberNotACrash()
        {
            var result = JsonShotLoader.LoadFrom(BigIntShot);
            CollectionAssert.AreEqual(new[] { "good" }, result.Shots.Select(s => s.Name).ToArray());
            CollectionAssert.AreEqual(new[] { BigIntCeiling }, result.Errors.ToArray());
        }

        [Test]
        public void AHugeIntegerDoesNotTakeTheWholeFileWithItThroughLoadFromPath()
        {
            var path = Path.Combine(Path.GetTempPath(), "nova-bigint-" + Path.GetRandomFileName() + ".json");
            File.WriteAllText(path, BigIntShot);
            try
            {
                var result = JsonShotLoader.LoadFromPath(path);
                CollectionAssert.AreEqual(new[] { "good" }, result.Shots.Select(s => s.Name).ToArray());
                CollectionAssert.AreEqual(new[] { BigIntCeiling }, result.Errors.ToArray());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void ASchemaVersionTooBigForAnIntIsRefusedNotThrown()
        {
            foreach (var version in new[] { "100000000000000000000", "5000000000" })
            {
                var result = JsonShotLoader.LoadFrom(
                    "{ \"$schemaVersion\": " + version + ", \"shots\": [] }");
                Assert.AreEqual(1, result.Errors.Count, version);
                StringAssert.StartsWith("unsupported $schemaVersion '" + version + "'", result.Errors[0]);
            }
        }

        /// <summary>
        /// Newtonsoft date-parses any string that LOOKS like a date, turning it into a Date token —
        /// which every reader here then reads as "absent, or the wrong type". A shot name, a cheat
        /// command or a textContains substring is text, never a date.
        /// </summary>
        [Test]
        public void StringsThatLookLikeDatesAreStillStrings()
        {
            var result = JsonShotLoader.LoadFrom(
                "{ \"$schemaVersion\": 1, \"shots\": [ { \"name\": \"2026-09-21T10:00:00Z\", " +
                "\"expectState\": \"2026-09-21T10:00:00+02:00\", " +
                "\"setup\": [ \"/Date(1)/\" ], " +
                "\"steps\": [ { \"kind\": \"cheat\", \"command\": \"2026-09-21T10:00:00Z\" }, " +
                "{ \"kind\": \"click\", \"name\": \"2026-09-21T10:00:00Z\", " +
                "\"until\": { \"kind\": \"present\", \"name\": \"X\" } } ], " +
                "\"settle\": { \"kind\": \"textContains\", \"name\": \"Clock\", " +
                "\"substring\": \"2026-09-21T10:00:00\" } } ] }");

            CollectionAssert.IsEmpty(result.Errors);
            Assert.AreEqual(1, result.Shots.Count);
            var shot = result.Shots[0];
            Assert.AreEqual("2026-09-21T10:00:00Z", shot.Name);
            Assert.AreEqual("2026-09-21T10:00:00+02:00", shot.ExpectState);
            CollectionAssert.AreEqual(new[] { "/Date(1)/" }, shot.Setup.ToArray());
            Assert.AreEqual("2026-09-21T10:00:00Z", shot.Steps[0].Text);
        }

        /// <summary>A settle of `all` → `all` → … n deep, ending in a `present`.</summary>
        private static string NestedShot(string name, int alls)
        {
            var open = "";
            var close = "";
            for (var i = 0; i < alls; i++)
            {
                open += "{ \"kind\": \"all\", \"parts\": [ ";
                close += " ] }";
            }
            return "{ \"name\": \"" + name + "\", \"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], " +
                   "\"settle\": " + open + "{ \"kind\": \"present\", \"name\": \"X\" }" + close + " }";
        }

        [Test]
        public void ConditionsEightDeepLoadAndNineDeepAreRefused()
        {
            var result = JsonShotLoader.LoadFrom("{ \"$schemaVersion\": 1, \"shots\": [ " +
                                                 NestedShot("deep8", 7) + ", " + NestedShot("deep9", 8) + " ] }");
            CollectionAssert.AreEqual(new[] { "deep8" }, result.Shots.Select(s => s.Name).ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    "shots[1] ('deep9').settle.parts[0].parts[0].parts[0].parts[0].parts[0].parts[0]" +
                    ".parts[0].parts[0]: conditions are nested more than 8 deep",
                },
                result.Errors.ToArray());
        }

        [Test]
        public void ADeepNestingIsOneErrorForTheBranchNotOnePerPart()
        {
            // Two parts at depth 9 under the same `all`: the first stops the branch, as any other
            // condition error does.
            var nine = "{ \"kind\": \"all\", \"parts\": [ " +
                       "{ \"kind\": \"present\", \"name\": \"A\" }, { \"kind\": \"present\", \"name\": \"B\" } ] }";
            var open = "";
            var close = "";
            for (var i = 0; i < 7; i++) { open += "{ \"kind\": \"all\", \"parts\": [ "; close += " ] }"; }
            var result = JsonShotLoader.LoadFrom(
                "{ \"$schemaVersion\": 1, \"shots\": [ { \"name\": \"n\", " +
                "\"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], \"settle\": " +
                open + nine + close + " } ] }");
            Assert.AreEqual(1, result.Errors.Count, string.Join(" | ", result.Errors));
            StringAssert.EndsWith(": conditions are nested more than 8 deep", result.Errors[0]);
        }

        [Test]
        public void NestingFarPastNewtonsoftsOwnLimitIsTheKitsErrorNotTheParsers()
        {
            var result = JsonShotLoader.LoadFrom(
                "{ \"$schemaVersion\": 1, \"shots\": [ " + NestedShot("deep", 70) + " ] }");
            Assert.AreEqual(1, result.Errors.Count, string.Join(" | ", result.Errors));
            StringAssert.EndsWith(": conditions are nested more than 8 deep", result.Errors[0]);
        }

        /// <summary>
        /// The eleventh audit, S1 — THE AUDITOR'S FILE, FROM DISK. <c>{"$schemaVersion": [[[…]]], "shots": []}</c>, 20,000
        /// deep: the loader rendered the refused version with an INDENTED <c>ToString()</c>, whose length grows with the
        /// square of the depth — 1.7 s and an 800,000,056-character error on every load. <see cref="NovaJson.ParseObject"/>
        /// now refuses a document nested past <see cref="NovaJson.MaxNesting"/> by name, before anything is built from it.
        /// Timed warm, from disk, through the path the adapter takes on every relay command.
        /// </summary>
        [Test]
        public void ADeepSchemaVersionLoadedFromDiskIsRefusedInUnder50msWithAShortMessage()
        {
            const int depth = 20_000;
            var text = "{ \"$schemaVersion\": " + new string('[', depth) + "1" + new string(']', depth) + ", \"shots\": [] }";
            var path = Path.Combine(Path.GetTempPath(), "deep-version-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, text);
            try
            {
                JsonShotLoader.LoadFrom("{ \"$schemaVersion\": [[1]], \"shots\": [] }"); // JIT, off the clock
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = JsonShotLoader.LoadFromPath(path);
                watch.Stop();
                TestContext.WriteLine($"LoadFromPath, $schemaVersion {depth} deep: {watch.ElapsedMilliseconds} ms, " +
                                      $"error {result.Errors.Sum(e => (long)e.Length)} characters");
                Assert.AreEqual(1, result.Errors.Count);
                Assert.Less(result.Errors[0].Length, 100, result.Errors[0].Substring(0, Math.Min(200, result.Errors[0].Length)));
                Assert.AreEqual("shots file is not valid JSON: it nests arrays and objects more than 256 deep", result.Errors[0]);
                Assert.Less(watch.ElapsedMilliseconds, 50, $"a {depth}-deep $schemaVersion took {watch.ElapsedMilliseconds} ms to refuse");
                CollectionAssert.IsEmpty(result.Shots);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// The eleventh audit, ruling 2 — THE REFUSED VERSION IS SHOWN COMPACT AND CUT TO 64 CHARACTERS
        /// (<c>JsonShotLoader.MaxShownVersion</c>). Under the nesting cap a value can still render long: an indented 40-deep
        /// array is 3,280 characters, a string as long as the file. A scalar keeps its plain rendering (the shared grammar's
        /// "'2'" and "'1'" cases).
        /// </summary>
        [Test]
        public void ARefusedSchemaVersionIsShownCompactAndCutTo64Characters()
        {
            string Error(string version) => JsonShotLoader.LoadFrom("{ \"$schemaVersion\": " + version + ", \"shots\": [] }").Errors.Single();
            Assert.AreEqual("unsupported $schemaVersion '" + new string('[', 40) + new string(']', 24) + "' — this kit reads version 1",
                Error(new string('[', 40) + new string(']', 40)));
            Assert.AreEqual("unsupported $schemaVersion '" + new string('v', 64) + "' — this kit reads version 1",
                Error("\"" + new string('v', 100_000) + "\""));
            Assert.AreEqual("unsupported $schemaVersion '{\"a\":[1,2]}' — this kit reads version 1", Error("{ \"a\": [1, 2] }"));
            // CONTROLS: a scalar is shown as it always was
            Assert.AreEqual("unsupported $schemaVersion '2' — this kit reads version 1", Error("2"));
            Assert.AreEqual("unsupported $schemaVersion '1' — this kit reads version 1", Error("\"1\""));
            Assert.AreEqual("unsupported $schemaVersion 'True' — this kit reads version 1", Error("true"));
        }

        /// <summary>
        /// The eleventh audit, ruling 2 — <see cref="NovaJson.ParseObject"/> REFUSES NESTING PAST <see cref="NovaJson.MaxNesting"/>
        /// (256), by name: an array or object inside 256 others is refused, one inside 255 is read. It counts containers only,
        /// as the site's <c>jsonDepthOver</c> does, and the shared delivery fixture holds the two to the same boundary. 256 is
        /// far above a valid shot (about 20) and above the 70-deep conditions whose OWN sentence the kit keeps
        /// (<see cref="NestingFarPastNewtonsoftsOwnLimitIsTheKitsErrorNotTheParsers"/>, about 145).
        /// </summary>
        [Test]
        public void NovaJsonRefusesNestingPastItsCapByName()
        {
            Assert.AreEqual(256, NovaJson.MaxNesting);
            string Note(int arrays) => "{ \"note\": " + new string('[', arrays) + new string(']', arrays) + " }";
            Assert.DoesNotThrow(() => NovaJson.ParseObject(Note(256)), "an array inside 256 containers, counting the root, is read");
            var e = Assert.Throws<Newtonsoft.Json.JsonReaderException>(() => NovaJson.ParseObject(Note(257)));
            Assert.AreEqual("it nests arrays and objects more than 256 deep", e!.Message);
            // an object counts the same as an array, and a scalar inside the deepest container is not a level
            Assert.Throws<Newtonsoft.Json.JsonReaderException>(() => NovaJson.ParseObject(
                "{ \"note\": " + new string('[', 256) + "{}" + new string(']', 256) + " }"));
            Assert.DoesNotThrow(() => NovaJson.ParseObject("{ \"note\": " + new string('[', 256) + "1" + new string(']', 256) + " }"));
        }
    }

    /// <summary>
    /// Slice A2′ — THE SHARED GRAMMAR SEAM (invariant 101).
    ///
    /// <c>Editor/Tests/Fixtures/shots-grammar.cases.json</c> holds one verdict per case — the shot
    /// names that load and the errors that come out — and BOTH sides of the wire are held to it:
    /// this test runs every case through the real <see cref="JsonShotLoader"/>, and the hosted
    /// lint's spec (<c>apps/renderer/src/game-ads/shots/shots-lint.spec.ts</c>) runs the same file
    /// through the TypeScript mirror. A grammar change on either side turns the other red, which is
    /// the only thing that stops the website promising a studio a shot their kit cannot load.
    ///
    /// The file is DATA, not a copy of either implementation: if the kit's real behaviour ever
    /// disagrees with a case, that is a finding to take to both sides, never a number to edit here.
    /// </summary>
    public class ShotsGrammarSharedCasesTests
    {
        private const string FixtureRelative = "Editor/Tests/Fixtures/shots-grammar.cases.json";
        private const int MinimumCases = 36;

        /// <summary>
        /// The package's own folder — through the package manager when the kit is installed as a
        /// package (the test host's case), with a walk up from the working directory as the fallback
        /// for a project that embedded the sources.
        /// </summary>
        private static string? FixturePath()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(JsonShotLoader).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
            {
                var resolved = Path.Combine(info.resolvedPath, FixtureRelative);
                if (File.Exists(resolved)) return resolved;
            }
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                var guess = Path.Combine(dir.FullName, "com.projectnova.recorder-kit", FixtureRelative);
                if (File.Exists(guess)) return guess;
            }
            return null;
        }

        [Test]
        public void EveryCaseInTheSharedFixtureLoadsExactlyAsItSays()
        {
            var path = FixturePath();
            // FAIL, never skip: a missing shared fixture means the two sides are no longer held to
            // the same grammar, and a skipped test reads as a pass on every dashboard there is.
            Assert.IsNotNull(path,
                "the shared grammar fixture was not found (" + FixtureRelative + ") — the kit and " +
                "the hosted lint are no longer tested against the same cases");

            // Read the fixture the way the kit reads a shots file: a case whose `text` or `doc`
            // contains something date-shaped must reach the loader as the bytes it says, not as a
            // DateTime Newtonsoft round-tripped on the way in.
            var root = NovaJson.ParseObject(File.ReadAllText(path!));
            var cases = root["cases"] as JArray;
            Assert.IsNotNull(cases, "the fixture has no 'cases' array");
            Assert.GreaterOrEqual(cases!.Count, MinimumCases,
                "the shared fixture has shrunk below the grammar it is supposed to pin");

            var failures = new List<string>();
            foreach (var t in cases)
            {
                var c = (JObject)t;
                var name = c["name"]?.Value<string>() ?? "(unnamed)";
                var text = c["text"]?.Type == JTokenType.String
                    ? c["text"]!.Value<string>()!
                    : c["doc"]!.ToString(Newtonsoft.Json.Formatting.None);

                var result = JsonShotLoader.LoadFrom(text);

                if (c["levers"] is not JArray)
                    failures.Add($"{name}: the case has no 'levers' array — the two sides' lever " +
                                 "lists are no longer compared");

                var expectedShots = (c["shots"] as JArray ?? new JArray())
                    .Select(x => x.Value<string>() ?? "").ToArray();
                var gotShots = result.Shots.Select(s => s.Name).ToArray();
                if (!expectedShots.SequenceEqual(gotShots))
                    failures.Add($"{name}: shots — expected [{string.Join(", ", expectedShots)}], " +
                                 $"got [{string.Join(", ", gotShots)}]");

                var expectedErrors = (c["kitErrors"] as JArray ?? new JArray())
                    .Select(x => x.Value<string>() ?? "").ToArray();
                if (expectedErrors.Length != result.Errors.Count)
                {
                    failures.Add($"{name}: {expectedErrors.Length} error(s) expected, " +
                                 $"{result.Errors.Count} produced — [{string.Join(" | ", result.Errors)}]");
                    continue;
                }
                // THE LEVER LIST is part of the same shared verdict: it is a security control, and
                // nothing else compares the two sides' idea of which commands a delivered file asks
                // to run. Computed from the same text, by the same definition, on both sides.
                var expectedLevers = (c["levers"] as JArray ?? new JArray())
                    .Select(x => x.Value<string>() ?? "").ToArray();
                var gotLevers = Levers.NeededFromShotsJson(text).ToArray();
                if (!expectedLevers.SequenceEqual(gotLevers))
                    failures.Add($"{name}: levers — expected [{string.Join(", ", expectedLevers)}], " +
                                 $"got [{string.Join(", ", gotLevers)}]");

                for (var i = 0; i < expectedErrors.Length; i++)
                {
                    var expected = expectedErrors[i];
                    var got = result.Errors[i];
                    // An expectation ending in ": " is a PREFIX: the parser's own wording follows,
                    // and Newtonsoft's sentence is not the hosted side's to mirror.
                    var ok = expected.EndsWith(": ", StringComparison.Ordinal)
                        ? got.StartsWith(expected, StringComparison.Ordinal)
                        : string.Equals(expected, got, StringComparison.Ordinal);
                    if (!ok)
                        failures.Add($"{name}: error[{i}] — expected \"{expected}\", got \"{got}\"");
                }
            }

            Assert.IsEmpty(failures,
                $"{failures.Count} of the {cases.Count} shared grammar cases disagree with this " +
                "loader:\n" + string.Join("\n", failures));
        }
    }
}
