using System.Collections.Generic;
using System.IO;
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
    }
}
