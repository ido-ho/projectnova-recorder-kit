using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Slice D — the pure text and disk passes of the export: the seven regexes, the Addressables
    /// YAML read, and the doc/config sweep. No editor, no assets.
    ///
    /// Every regex gets a POSITIVE and a NEAR-MISS. A pattern table tested only on things that match
    /// cannot tell "it fires" from "it fires on everything".
    /// </summary>
    public class ExportScanTests
    {
        // Line numbers matter: the whole value of a hit is `file:line`.
        private const string Fixture =
            "// fixture\n" +                            // 1
            "#if UNITY_EDITOR\n" +                      // 2  unity-editor-ifdef
            "#endif\n" +                                // 3
            "public class CheatMenu { }\n" +            // 4  debug-class
            "// spacer\n" +                             // 5
            "Random.InitState(42);\n" +                 // 6  random-seed
            "RemoteConfig.Fetch();\n" +                 // 7  remote-config
            "var tutorialStep = 1;\n" +                 // 8  tutorial-key
            "const string FeatureUnlock = \"x\";\n" +   // 9  feature-unlock
            "PlayerPrefs.SetInt(\"coins\", 1);\n" +     // 10 playerprefs-key
            "DevConsole.RegisterAction(\"Force Win\", () => Win());\n";   // 11 dev-command

        private const string NearMiss =
            "// near misses\n" +                        // 1
            "#if UNITY_ANDROID\n" +                     // 2  not one of the three symbols
            "#endif\n" +                                // 3
            "int debugClass = 0;\n" +                   // 4  no `class ` token
            "Random.Range(0, 10);\n" +                  // 5  not InitState
            "RemoteConfigs.Fetch();\n" +                // 6  no word boundary after
            "int tutorialStep;\n" +                     // 7  no = ( or \"
            "var featureLocked = true;\n" +             // 8  not feature_unlock
            "PlayerPrefs.SetInt(keyName, 1);\n" +       // 9  no quoted key
            "DevConsole.RegisterAction(name, cb);\n";    // 10 no literal name

        [Test]
        public void DevCommand_FiresOnEveryRegistrationShape_AndNotOnAPlainCall()
        {
            string[] fire =
            {
                "DevConsole.RegisterAction(\"Give me everything pls\", () => Give());",   // RL, verbatim shape
                "LunarConsole.RegisterAction(\"Skip level\", Skip);",
                "DebugLogConsole.AddCommand(\"coins\", \"add coins\", AddCoins);",
                "[ConsoleMethod(\"god\", \"god mode\")] static void God() { }",
                "[Command(\"spawn-boss\")] void SpawnBoss() { }",
                "public static readonly DevVar<string> PlayerUid = new DevVar<string>(\"dev.playerUid\", string.Empty);",
                "SRDebug.Instance.RegisterCommand($\"Level {i}\", Go);",
            };
            foreach (var line in fire)
                Assert.IsTrue(ExportScan.ScanText("A.cs", line).Exists(h => h.Pattern == "dev-command"), line);
            string[] quiet =
            {
                "RegisterAction(actionName, cb);",
                "events.AddListener(OnClick);",
                "var command = \"Force Win\";",
                "[Serializable] class CommandData { }",
            };
            foreach (var line in quiet)
                Assert.IsFalse(ExportScan.ScanText("A.cs", line).Exists(h => h.Pattern == "dev-command"), line);
        }

        [Test]
        public void EveryPattern_FiresOnItsPositive_AtTheRightLine()
        {
            var hits = ExportScan.ScanText("Fixture.cs", Fixture);
            var byId = new Dictionary<string, ExportPatternHit>();
            foreach (var h in hits)
            {
                Assert.AreEqual("Fixture.cs", h.File);
                Assert.IsFalse(byId.ContainsKey(h.Pattern), "one hit per id in this fixture: " + h.Pattern);
                byId[h.Pattern] = h;
            }

            Assert.AreEqual(8, hits.Count, "all eight and nothing else");
            Assert.AreEqual(2, byId["unity-editor-ifdef"].Line);
            Assert.AreEqual(4, byId["debug-class"].Line);
            Assert.AreEqual(6, byId["random-seed"].Line);
            Assert.AreEqual(7, byId["remote-config"].Line);
            Assert.AreEqual(8, byId["tutorial-key"].Line);
            Assert.AreEqual(9, byId["feature-unlock"].Line);
            Assert.AreEqual(10, byId["playerprefs-key"].Line);
            Assert.AreEqual(11, byId["dev-command"].Line);

            // `text` is the matched LINE, trimmed
            Assert.AreEqual("#if UNITY_EDITOR", byId["unity-editor-ifdef"].Text);
            Assert.AreEqual("PlayerPrefs.SetInt(\"coins\", 1);", byId["playerprefs-key"].Text);

            // and hits come out ordered by line
            for (var i = 1; i < hits.Count; i++)
                Assert.LessOrEqual(hits[i - 1].Line, hits[i].Line);
        }

        [Test]
        public void EveryPattern_StaysSilentOnItsNearMiss()
        {
            var hits = ExportScan.ScanText("Near.cs", NearMiss);
            var names = new List<string>();
            foreach (var h in hits) names.Add(h.Pattern + "@" + h.Line + " " + h.Text);
            CollectionAssert.IsEmpty(names, "no near-miss line may fire any of the eight");
        }

        [Test]
        public void UnityEditorIfdef_TakesAllThreeSymbols_ButOnlyAtTheStartOfALine()
        {
            Assert.AreEqual(1, ExportScan.ScanText("a.cs", "  #if DEVELOPMENT_BUILD\n").Count);
            Assert.AreEqual(1, ExportScan.ScanText("a.cs", "#if DEBUG\n").Count);
            Assert.AreEqual(1, ExportScan.ScanText("a.cs", "#if UNITY_EDITOR && !X\n").Count);
            CollectionAssert.IsEmpty(ExportScan.ScanText("a.cs", "// #if UNITY_EDITOR\n"));
        }

        [Test]
        public void PatternText_IsTrimmedAndCappedAt200Chars()
        {
            var long_ = "    Random.InitState(1); // " + new string('z', 400) + "   \n";
            var hits = ExportScan.ScanText("a.cs", long_);
            Assert.AreEqual(1, hits.Count);
            Assert.AreEqual(ExportFormat.PatternTextMax, hits[0].Text.Length);
            StringAssert.StartsWith("Random.InitState(1);", hits[0].Text);
        }

        [Test]
        public void ScanText_HandlesCrLfAndEmptyInput()
        {
            var hits = ExportScan.ScanText("a.cs", "line1\r\nRandom.InitState(2);\r\n");
            Assert.AreEqual(1, hits.Count);
            Assert.AreEqual(2, hits[0].Line);
            Assert.AreEqual("Random.InitState(2);", hits[0].Text);
            CollectionAssert.IsEmpty(ExportScan.ScanText("a.cs", ""));
            CollectionAssert.IsEmpty(ExportScan.ScanText("a.cs", null));
        }

        // ---- the line the hit is ON (fresh-context audit K5, 2026-09-21) ------------------
        //
        // `^\s*#if…` under RegexOptions.Multiline let `\s*` run BACKWARDS across newlines: the match
        // started on the first blank line above the directive, so the hit came back pointing at an
        // EMPTY line several lines early. `file:line` is the entire value of a pattern hit, and an
        // intake lane citing the wrong line cites nothing. Measured: `#if UNITY_EDITOR` on line 7
        // after two blank lines reported `line=5 text=''`.

        [Test]
        public void AHitAfterBlankLines_ReportsTheLineItIsActuallyOn()
        {
            //        1        2   3   4   5   6   7                   8
            var text = "// a\n" + "\n" + "\n" + "\n" + "\n" + "\n" + "#if UNITY_EDITOR\n" + "#endif\n";
            var hits = ExportScan.ScanText("a.cs", text);
            Assert.AreEqual(1, hits.Count);
            Assert.AreEqual(7, hits[0].Line, "the directive is on line 7");
            Assert.AreEqual("#if UNITY_EDITOR", hits[0].Text, "and `text` is that line, not the blank one above it");
        }

        [Test]
        public void AHitAfterABlankLine_InACrLfFile_ReportsTheLineItIsActuallyOn()
        {
            var hits = ExportScan.ScanText("a.cs", "// a\r\n\r\n#if DEBUG\r\n#endif\r\n");
            Assert.AreEqual(1, hits.Count);
            Assert.AreEqual(3, hits[0].Line);
            Assert.AreEqual("#if DEBUG", hits[0].Text, "no stray \\r survives into the wire");
        }

        [Test]
        public void AFileOfBlankLines_ScansInBoundedTime_NotQuadratically()
        {
            // The same defect made the scan quadratic: 32,000 blank lines (a 32 KB file, far under
            // patternFileMaxBytes) took 16.8 SECONDS of frozen editor. Line-by-line it is linear.
            var text = new string('\n', 32000) + "#if UNITY_EDITOR\n";
            var sw = Stopwatch.StartNew();
            var hits = ExportScan.ScanText("big.cs", text);
            sw.Stop();
            Assert.AreEqual(1, hits.Count);
            Assert.AreEqual(32001, hits[0].Line);
            Assert.Less(sw.ElapsedMilliseconds, 1000,
                "32 KB of blank lines took " + sw.ElapsedMilliseconds + " ms");
        }

        [Test]
        public void NoPattern_CanMatchAcrossALineBreak()
        {
            // `\s` in the table spanned newlines. `class` on one line and the name on the next is
            // not a `debug-class` hit on either of them.
            CollectionAssert.IsEmpty(ExportScan.ScanText("a.cs", "class\nCheatMenu { }\n"));
            CollectionAssert.IsEmpty(ExportScan.ScanText("a.cs", "PlayerPrefs.SetInt\n(\"k\", 1);\n"));
            // positive control: on ONE line both still fire
            Assert.AreEqual(1, ExportScan.ScanText("a.cs", "class CheatMenu { }\n").Count);
            Assert.AreEqual(1, ExportScan.ScanText("a.cs", "PlayerPrefs.SetInt(\"k\", 1);\n").Count);
        }

        // ---- the tree walk never leaves the project (audit K7) ---------------------------

        [Test]
        public void ConfigHunt_NeverFollowsADirectorySymlinkOutOfTheProject()
        {
            var root = Path.Combine(Path.GetTempPath(), "novakit-link-" + Path.GetRandomFileName());
            try
            {
                var project = Path.Combine(root, "project");
                var outside = Path.Combine(root, "outside");
                Write(project, "real/remote_config_real.json", "{}");
                Write(outside, "remote_config_secret.json", "{\"apiKey\":\"not in this project\"}");
                if (!TrySymlink(Path.Combine(project, "shared"), outside))
                    Assert.Ignore("this platform would not create a directory symlink");

                var found = ExportScan.FindConfigFiles(project);
                var listed = string.Join(", ", found.ToArray());
                CollectionAssert.Contains(found, "real/remote_config_real.json",
                    "positive control: a REAL directory under the project is still read (" + listed + ")");
                foreach (var f in found)
                    Assert.IsFalse(f.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0,
                        "the hunt walked a symlink OUT of the project and would have uploaded " + f);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void ATreeWalk_TerminatesOnASymlinkCycle()
        {
            // The .cs scan over an Assets/ holding one symlink cycle ran past 120 SECONDS on the
            // main thread before the auditor killed it (Directory.GetFiles(…, AllDirectories)
            // follows links). The shared walker skips reparse points, so a cycle is a non-event.
            var root = Path.Combine(Path.GetTempPath(), "novakit-cycle-" + Path.GetRandomFileName());
            try
            {
                var project = Path.Combine(root, "project");
                Write(project, "a/b/remote_config_here.json", "{}");
                if (!TrySymlink(Path.Combine(project, "a", "loop"), project))
                    Assert.Ignore("this platform would not create a directory symlink");

                var sw = Stopwatch.StartNew();
                var found = ExportScan.FindConfigFiles(project);
                sw.Stop();
                Assert.Less(sw.ElapsedMilliseconds, 5000, "the walk took " + sw.ElapsedMilliseconds + " ms");
                CollectionAssert.AreEqual(new[] { "a/b/remote_config_here.json" }, found,
                    "the real file once, and nothing reached through the cycle");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        /// <summary>`ln -s` through the shell: .NET Standard 2.1 has no CreateSymbolicLink, and the
        /// kit must compile under both API compatibility levels. False on any platform or volume
        /// that will not make one — the caller then skips rather than passing vacuously.</summary>
        private static bool TrySymlink(string linkPath, string targetPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
                var psi = new ProcessStartInfo("/bin/ln", "-s \"" + targetPath + "\" \"" + linkPath + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.WaitForExit(10000);
                    if (p.ExitCode != 0) return false;
                }
                return Directory.Exists(linkPath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- Addressables ----------------------------------------------------------------

        [Test]
        public void ReadAddressableGuids_TakesEveryMGuidLine_DedupedAndSorted()
        {
            const string yaml =
                "MonoBehaviour:\n" +
                "  m_Name: Default Local Group\n" +
                "  m_GUID: ffffffffffffffffffffffffffffffff\n" +
                "  m_SerializeEntries:\n" +
                "  - m_GUID: 0123456789abcdef0123456789abcdef\n" +
                "    m_Address: Boot\n" +
                "  - m_GUID: aaaaaaaabbbbbbbbccccccccdddddddd\n" +
                "  - m_GUID: 0123456789abcdef0123456789abcdef\n" +
                "  m_Notes: m_GUID: nothex\n";

            var guids = ExportScan.ReadAddressableGuids(yaml);
            CollectionAssert.AreEqual(new[]
            {
                "0123456789abcdef0123456789abcdef",
                "aaaaaaaabbbbbbbbccccccccdddddddd",
                "ffffffffffffffffffffffffffffffff",
            }, guids);

            CollectionAssert.IsEmpty(ExportScan.ReadAddressableGuids(null));
            CollectionAssert.IsEmpty(ExportScan.ReadAddressableGuids("nothing here"));
        }

        // ---- asset YAML, read INSTEAD of loading the asset (audit K6) --------------------

        private const string ColorAssetYaml =
            "%YAML 1.1\n" +
            "%TAG !u! tag:unity3d.com,2011:\n" +
            "--- !u!114 &11400000\n" +
            "MonoBehaviour:\n" +
            "  m_ObjectHideFlags: 0\n" +
            "  m_Name: colors\n" +
            "  primary: {r: 1, g: 0, b: 0, a: 1}\n" +
            "  secondary: {r: 0.1, g: 0.2, b: 0.3, a: 1}\n" +
            "  packed: {rgba: 4278190335}\n" +
            "  offset: {x: 3, y: 4}\n" +
            "  entries:\n" +
            "  - color: {r: 0, g: 1, b: 0, a: 0.5}\n" +
            "    label: first\n" +
            "  - color: {r: 0, g: 0, b: 1, a: 1}\n" +
            "  trailing: {r: 1, g: 1, b: 1, a: 1}\n";

        [Test]
        public void ReadYamlColors_ReadsBothColourShapes_WithTheirKeyPath()
        {
            var colors = ExportScan.ReadYamlColors(ColorAssetYaml, 256);
            var byName = new Dictionary<string, string>();
            // FIRST wins: a repeated key (every element of a list) is a separate row, not an
            // overwrite — `colorObjects[].colors` is a list on the wire for exactly that reason.
            foreach (var kv in colors) if (!byName.ContainsKey(kv.Key)) byName[kv.Key] = kv.Value;

            Assert.AreEqual("#FF0000FF", byName["primary"], "Color.red");
            // 0.1/0.2/0.3 x 255, rounded the way ColorUtility.ToHtmlStringRGBA rounds them
            Assert.AreEqual("#1A334DFF", byName["secondary"]);
            Assert.AreEqual("#FF0000FF", byName["packed"], "a Color32 is one packed little-endian uint");
            Assert.AreEqual("#00FF0080", byName["entries.color"], "nested under its parent key");
            Assert.AreEqual("#FFFFFFFF", byName["trailing"], "the parent is popped again on dedent");

            // the near miss: a Vector2/Vector4 is a flow mapping too, and is NOT a colour
            Assert.IsFalse(byName.ContainsKey("offset"));
            Assert.IsFalse(byName.ContainsKey("m_Name"));
            // both `entries.color` rows are there (a repeated key is not a duplicate row)
            var greens = 0;
            foreach (var kv in colors) if (kv.Key == "entries.color") greens++;
            Assert.AreEqual(2, greens);
        }

        [Test]
        public void ReadYamlColors_IsBounded_AndSafeOnRubbish()
        {
            Assert.AreEqual(2, ExportScan.ReadYamlColors(ColorAssetYaml, 2).Count, "the cap holds");
            CollectionAssert.IsEmpty(ExportScan.ReadYamlColors(null, 256));
            CollectionAssert.IsEmpty(ExportScan.ReadYamlColors("", 256));
            CollectionAssert.IsEmpty(ExportScan.ReadYamlColors("not yaml at all", 256));
            CollectionAssert.IsEmpty(ExportScan.ReadYamlColors(ColorAssetYaml, 0));
        }

        [Test]
        public void ReadYamlColors_SaysWhenTheColourCapCutTheReadShort()
        {
            // M7 (audit round 4). A cap that BITES is never silent — the same rule every other
            // listing in the export obeys. It only became reachable in practice when the reader
            // learned to read `Color[]` elements, which is how a 300-swatch palette asset now
            // meets a 256-colour cap.
            var notes = new List<string>();
            Assert.AreEqual(2, ExportScan.ReadYamlColors(ColorAssetYaml, 2, notes).Count);
            Assert.AreEqual(1, notes.Count,
                "the cap was reached and there is more file below it — one row, never silent:\n  " +
                string.Join("\n  ", notes.ToArray()));
            StringAssert.Contains("2", notes[0], notes[0]);

            // POSITIVE CONTROL: a cap that does NOT bite writes nothing…
            notes.Clear();
            ExportScan.ReadYamlColors(ColorAssetYaml, 256, notes);
            CollectionAssert.IsEmpty(notes, "an uncapped read has nothing to report");

            // …and neither does a read that ends with the cap exactly met by the LAST line, which
            // is not "colours were dropped".
            notes.Clear();
            ExportScan.ReadYamlColors("MonoBehaviour:\n  only: {r: 1, g: 0, b: 0, a: 1}\n", 1, notes);
            CollectionAssert.IsEmpty(notes, "the only colour in the file is not a cap that bit");
        }

        // ---- S3: the three shapes the colour reader was blind to (audit round 3) ---------

        [Test]
        public void ReadYamlColors_ReadsABareFlowMapListItem_NamedByItsParentAndIndex()
        {
            // `Color[]` and `List<Color>` — the shape a brand-palette asset is made of. Unity writes
            // the elements as BARE flow mappings under a list marker, with no key of their own. The
            // reader stripped the `- `, landed on `{`, took `{r` as the key and then refused the
            // remainder because it did not start with `{` — every swatch dropped, and no `errors`
            // row to say so (fresh-context audit S3, 2026-09-21).
            const string yaml =
                "%YAML 1.1\n" +
                "%TAG !u! tag:unity3d.com,2011:\n" +
                "--- !u!114 &11400000\n" +
                "MonoBehaviour:\n" +
                "  m_Name: palette\n" +
                "  swatches:\n" +
                "  - {r: 1, g: 0, b: 0, a: 1}\n" +
                "  - {r: 0, g: 0, b: 1, a: 1}\n" +
                "  rows:\n" +
                "  - color: {r: 0, g: 1, b: 0, a: 1}\n" +
                "  refs:\n" +
                "  - {fileID: 0}\n" +
                "  - {r: 1, g: 1, b: 0, a: 1}\n" +
                "  after: {r: 1, g: 1, b: 1, a: 1}\n";

            var colors = ExportScan.ReadYamlColors(yaml, 256);
            var byName = FirstWins(colors);
            var listed = Listed(colors);

            Assert.IsTrue(byName.ContainsKey("swatches[0]"), "a bare Color[] element, named by its parent and index: " + listed);
            Assert.AreEqual("#FF0000FF", byName["swatches[0]"]);
            Assert.AreEqual("#0000FFFF", byName["swatches[1]"], "the index advances with the list");

            // …and the MAPPING list item keeps the name the spec pins for it.
            Assert.AreEqual("#00FF00FF", byName["rows.color"], "spec §4: `entries.color` for a colour inside a list");

            // a non-colour list item still holds its POSITION, so index 1 is the second element
            Assert.IsFalse(byName.ContainsKey("refs[0]"), "{fileID: 0} is not a colour: " + listed);
            Assert.AreEqual("#FFFF00FF", byName["refs[1]"], "the index is the position in the LIST: " + listed);

            // the list is popped again afterwards
            Assert.AreEqual("#FFFFFFFF", byName["after"]);
        }

        [Test]
        public void ReadYamlColors_ReadsOnlyTheMonoBehaviourDocuments_NotEveryDocumentInTheFile()
        {
            // A TMP font asset is several documents in one file: the MonoBehaviour, then the
            // Material and the atlas Texture2D it owns. The reader treated the whole file as one
            // document, so every TMP font came back as a `colorObjects` row full of SHADER colours
            // (`m_SavedProperties.m_Colors._GlowColor`) — facts about a Unity shader, reported as
            // facts about the studio's brand (fresh-context audit S3, 2026-09-21).
            const string tmpShaped =
                "%YAML 1.1\n" +
                "%TAG !u! tag:unity3d.com,2011:\n" +
                "--- !u!114 &11400000\n" +
                "MonoBehaviour:\n" +
                "  m_Name: probe SDF\n" +
                "  m_FaceInfo:\n" +
                "    m_FamilyName: Cairo\n" +
                "  m_TintColor: {r: 0, g: 0, b: 0, a: 1}\n" +
                "--- !u!21 &2100000\n" +
                "Material:\n" +
                "  m_Name: probe SDF Material\n" +
                "  m_SavedProperties:\n" +
                "    m_Colors:\n" +
                "    - _GlowColor: {r: 1, g: 0, b: 0, a: 1}\n" +
                "    - _FaceColor: {r: 1, g: 1, b: 1, a: 1}\n" +
                "--- !u!28 &2800000\n" +
                "Texture2D:\n" +
                "  m_Name: probe Atlas\n" +
                "  m_ColorSpace: {r: 0.5, g: 0.5, b: 0.5, a: 1}\n";

            var colors = ExportScan.ReadYamlColors(tmpShaped, 256);
            var listed = Listed(colors);
            Assert.AreEqual(1, colors.Count, "only the MonoBehaviour document is the ScriptableObject: " + listed);
            Assert.AreEqual("m_TintColor", colors[0].Key, listed);
            foreach (var kv in colors)
                Assert.IsFalse(kv.Key.IndexOf("m_SavedProperties", StringComparison.Ordinal) >= 0,
                    "a Material's shader colours are not the asset's colours: " + listed);

            // POSITIVE CONTROL: a SECOND MonoBehaviour document (a sub-asset) IS read.
            var withSub = tmpShaped + "--- !u!114 &11400001\nMonoBehaviour:\n  subColor: {r: 0, g: 1, b: 0, a: 1}\n";
            var both = FirstWins(ExportScan.ReadYamlColors(withSub, 256));
            Assert.IsTrue(both.ContainsKey("subColor"), "a sub-asset MonoBehaviour is still read: " + Listed(ExportScan.ReadYamlColors(withSub, 256)));
            Assert.IsTrue(both.ContainsKey("m_TintColor"));
        }

        [Test]
        public void ReadYamlColors_JoinsAFlowMapUnityWrappedAcrossLines_AndSaysSoWhenItCannot()
        {
            // Unity wraps a long `{…}` at about 80 columns. The reader took the text up to the first
            // `}` ON THAT LINE, found none, and returned null — silently, so a colour behind a long
            // key name simply did not exist (fresh-context audit S3).
            const string wrapped =
                "%YAML 1.1\n" +
                "--- !u!114 &11400000\n" +
                "MonoBehaviour:\n" +
                "  m_Name: wrapped\n" +
                "  aVeryLongKeyNameThatPushesTheValuePastEightyColumns: {r: 0.12345678, g: 0.2345678,\n" +
                "    b: 0.3456789, a: 1}\n" +
                "  after: {r: 1, g: 1, b: 1, a: 1}\n";

            var colors = ExportScan.ReadYamlColors(wrapped, 256);
            var byName = FirstWins(colors);
            var listed = Listed(colors);
            Assert.IsTrue(byName.ContainsKey("aVeryLongKeyNameThatPushesTheValuePastEightyColumns"),
                "a wrapped flow mapping is joined before it is parsed: " + listed);
            Assert.AreEqual("#1F3C58FF", byName["aVeryLongKeyNameThatPushesTheValuePastEightyColumns"]);
            Assert.AreEqual("#FFFFFFFF", byName["after"], "and the continuation line did not derail the key path");

            // BOUNDED, AND NEVER SILENT: a `{` that never closes is given up on and counted.
            var runaway = "%YAML 1.1\n--- !u!114 &11400000\nMonoBehaviour:\n  broken: {r: 1,\n";
            for (var i = 0; i < 40; i++) runaway += "    g: 0,\n";
            var notes = new List<string>();
            var none = ExportScan.ReadYamlColors(runaway, 256, notes);
            CollectionAssert.IsEmpty(none, Listed(none));
            Assert.AreEqual(1, notes.Count, "an unclosed flow mapping is reported, not swallowed: " +
                                            string.Join(" | ", notes.ToArray()));
            StringAssert.Contains("flow mapping", notes[0]);

            // POSITIVE CONTROL: the same reader with nothing wrapped notes nothing.
            var quiet = new List<string>();
            ExportScan.ReadYamlColors(ColorAssetYaml, 256, quiet);
            CollectionAssert.IsEmpty(quiet, string.Join(" | ", quiet.ToArray()));
        }

        [Test]
        public void ReadYamlColors_KeepsExponentNegativeHdrAndColor32Working()
        {
            // Not new behaviour — the shapes the S3 rewrite must not break. A float field can hold
            // any of these and `ColorUtility.ToHtmlStringRGBA` clamps exactly this way.
            const string edge =
                "%YAML 1.1\n" +
                "--- !u!114 &11400000\n" +
                "MonoBehaviour:\n" +
                "  tiny: {r: 1e-05, g: 0, b: 0, a: 1}\n" +
                "  negative: {r: -0.5, g: 0, b: 0, a: 1}\n" +
                "  hdr: {r: 3.5, g: 1, b: 1, a: 1}\n" +
                "  packed: {rgba: 4278190335}\n" +
                "  notAColour: {x: 1, y: 2, z: 3, w: 4}\n";

            var byName = FirstWins(ExportScan.ReadYamlColors(edge, 256));
            Assert.AreEqual("#000000FF", byName["tiny"], "1e-05 x 255 rounds to 0, and the exponent parses");
            Assert.AreEqual("#000000FF", byName["negative"], "a negative channel clamps to 00, never to a negative byte");
            Assert.AreEqual("#FFFFFFFF", byName["hdr"], "an HDR channel over 1 clamps to FF");
            Assert.AreEqual("#FF0000FF", byName["packed"], "a Color32 is one packed little-endian uint");
            Assert.IsFalse(byName.ContainsKey("notAColour"), "a Vector4 is a flow mapping and is not a colour");
        }

        private static Dictionary<string, string> FirstWins(List<KeyValuePair<string, string>> colors)
        {
            var byName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in colors) if (!byName.ContainsKey(kv.Key)) byName[kv.Key] = kv.Value;
            return byName;
        }

        private static string Listed(List<KeyValuePair<string, string>> colors)
        {
            var parts = new List<string>();
            foreach (var kv in colors) parts.Add(kv.Key + "=" + kv.Value);
            return "[" + string.Join(", ", parts.ToArray()) + "]";
        }

        [Test]
        public void LooksLikeYaml_TellsTextSerialisationFromBinary()
        {
            Assert.IsTrue(ExportScan.LooksLikeYaml(ColorAssetYaml));
            Assert.IsFalse(ExportScan.LooksLikeYaml("\0\0\0\0binary"), "force-binary serialisation has no YAML to read");
            Assert.IsFalse(ExportScan.LooksLikeYaml(null));
        }

        [Test]
        public void ReadYamlNestedValue_FindsTheTmpFamilyName_AndOnlyUnderItsParent()
        {
            const string font =
                "%YAML 1.1\n" +
                "MonoBehaviour:\n" +
                "  m_Name: Cairo SDF\n" +
                "  m_SourceFontFile: {fileID: 0}\n" +
                "  m_FaceInfo:\n" +
                "    m_FaceIndex: 0\n" +
                "    m_FamilyName: Cairo\n" +
                "    m_StyleName: Regular\n" +
                "  m_fontInfo:\n" +
                "    m_FamilyName: WrongOne\n";
            Assert.AreEqual("Cairo", ExportScan.ReadYamlNestedValue(font, "m_FaceInfo", "m_FamilyName"));
            // the near miss: the SAME child key under a DIFFERENT parent is not it
            Assert.AreEqual("WrongOne", ExportScan.ReadYamlNestedValue(font, "m_fontInfo", "m_FamilyName"));
            Assert.IsNull(ExportScan.ReadYamlNestedValue(font, "m_FaceInfo", "m_Nope"));
            Assert.IsNull(ExportScan.ReadYamlNestedValue(font, "m_Nope", "m_FamilyName"));
            Assert.IsNull(ExportScan.ReadYamlNestedValue((string?)null, "m_FaceInfo", "m_FamilyName"));
            Assert.AreEqual("Quoted Name",
                ExportScan.ReadYamlNestedValue("a:\n  b:\n    c: \"Quoted Name\"\n", "b", "c"));
        }

        /// <summary>The MonoBehaviour document of a TMP font, in the order Unity writes it.</summary>
        private const string FaceInfoDoc =
            "--- !u!114 &11400000\n" +
            "MonoBehaviour:\n" +
            "  m_Name: Cairo SDF\n" +
            "  m_FaceInfo:\n" +
            "    m_FaceIndex: 0\n" +
            "    m_FamilyName: Cairo\n";

        /// <summary>
        /// THE ATLAS, AS UNITY REALLY WRITES IT: a `Texture2D` document whose `_typelessdata:` is
        /// ONE line of megabytes of hex. Unity orders a file's documents by ascending SIGNED
        /// fileID, and a font's atlas very often has a NEGATIVE one — so this comes BEFORE the
        /// `&amp;11400000` MonoBehaviour, and `m_FaceInfo` sits after all of it.
        /// </summary>
        private static string AtlasDoc(int hexChars, bool negativeId) =>
            "--- !u!28 &" + (negativeId ? "-5942297626794167620" : "8400000") + "\n" +
            "Texture2D:\n" +
            "  m_Name: Cairo SDF Atlas\n" +
            "  m_Width: 2048\n" +
            "  m_Height: 2048\n" +
            "  _typelessdata: " + new string('7', hexChars) + "\n";

        private const string YamlHead = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

        [Test]
        public void ReadYamlNestedValue_Streaming_ReadsPastTheATLAS_AndStopsAtTheFirstMatch()
        {
            // AUDIT ROUND 4, S1 — measured, not supposed. Of 61 `.asset` files holding an
            // `m_FaceInfo:` across the two real games on this machine, 48 carry a non-empty
            // `m_FamilyName`; the 256 KiB head budget read 26 of them and spent its budget on 22,
            // because the atlas comes FIRST. "m_FamilyName is on about line 25" was the premise,
            // and it was false. This is the production shape.
            using (var r = new StringReader(YamlHead + AtlasDoc(300000, negativeId: true) + FaceInfoDoc))
                Assert.AreEqual("Cairo", ExportScan.ReadYamlNestedValue(r, "m_FaceInfo", "m_FamilyName"),
                    "a 300,000-char atlas line BEFORE the MonoBehaviour must not hide the family name");

            // CRLF, which is what a Windows studio's asset files hold.
            var crlf = (YamlHead + AtlasDoc(300000, negativeId: true) + FaceInfoDoc).Replace("\n", "\r\n");
            using (var r = new StringReader(crlf))
                Assert.AreEqual("Cairo", ExportScan.ReadYamlNestedValue(r, "m_FaceInfo", "m_FamilyName"), "CRLF");

            // POSITIVE CONTROL: the atlas AFTER the face info still answers, and the read stops at
            // the match rather than walking the hex.
            using (var r = new StringReader(YamlHead + FaceInfoDoc + AtlasDoc(300000, negativeId: false)))
                Assert.AreEqual("Cairo", ExportScan.ReadYamlNestedValue(r, "m_FaceInfo", "m_FamilyName"));

            // NEAR MISS: a file with no face info at all is a null, not an answer — and a file
            // whose only match is under the wrong parent is still a null.
            using (var r = new StringReader(YamlHead + AtlasDoc(300000, negativeId: true)))
                Assert.IsNull(ExportScan.ReadYamlNestedValue(r, "m_FaceInfo", "m_FamilyName"));
            Assert.IsNull(ExportScan.ReadYamlNestedValue((TextReader?)null, "a", "b"));

            // A FILE WITH NO TRAILING NEWLINE still has a last line.
            using (var r = new StringReader("MonoBehaviour:\n  m_FaceInfo:\n    m_FamilyName: Cairo"))
                Assert.AreEqual("Cairo", ExportScan.ReadYamlNestedValue(r, "m_FaceInfo", "m_FamilyName"));
        }

        [Test]
        public void ReadYamlNestedValue_Streaming_LetsAREADFAILUREThrow_RatherThanAnsweringNull()
        {
            // M3 (audit round 4). A `catch { return null; }` around the read turned "the disk said
            // no" into "this font has no family name", with no `errors` row — a silent null, which
            // is the one thing this whole file is written not to do (invariant 50). The CALLER
            // catches and writes the row; the reader must let it through.
            var e = Assert.Catch<Exception>(() =>
                ExportScan.ReadYamlNestedValue(new ThrowingReader(), "m_FaceInfo", "m_FamilyName"));
            StringAssert.Contains("the disk said no", e!.Message);
        }

        /// <summary>A reader that fails the way a disk does: part way through.</summary>
        private sealed class ThrowingReader : TextReader
        {
            private int _left = 40;
            public override int Read(char[] buffer, int index, int count)
            {
                if (_left <= 0) throw new IOException("the disk said no");
                var text = "MonoBehaviour:\n  m_Other: 1\n";
                var n = Math.Min(count, text.Length);
                text.CopyTo(0, buffer, index, n);
                _left -= n;
                return n;
            }
            public override int Read() => throw new IOException("the disk said no");
            public override string? ReadLine() => throw new IOException("the disk said no");
        }

        // ---- N2: the sweeps must be interruptible, not just their matches ------------------

        [Test]
        public void TheDocAndConfigSweeps_YieldOncePerEntryWALKED_NotOncePerMatch()
        {
            // The collector can only take a slice on something the iterator YIELDS, and these two
            // yielded matches only — so the whole walk of a real project's tree ran uninterrupted
            // on the editor's main thread (measured by the auditor at 933 ms and 1,858 ms on two
            // real projects). A "still walking" yield is what lets the collector tick per entry.
            var root = Path.Combine(Path.GetTempPath(), "novakit-slice-" + Path.GetRandomFileName());
            try
            {
                for (var i = 0; i < 40; i++) Write(root, "tree/" + i + "/filler.txt", "x");
                Write(root, "remote_config.json", "{}");
                for (var i = 0; i < 20; i++) Write(root, "docs/img" + i + ".png", "x");
                Write(root, "docs/a.md", "a");

                var configYields = 0;
                foreach (var _ in ExportScan.ScanConfigFiles(root)) configYields++;
                Assert.GreaterOrEqual(configYields, 80,
                    "the config hunt walked ~83 entries and yielded " + configYields + " times");

                var docYields = 0;
                foreach (var _ in ExportScan.ScanDocFiles(root)) docYields++;
                Assert.GreaterOrEqual(docYields, 20,
                    "the doc sweep walked 22 entries and yielded " + docYields + " times");

                // POSITIVE CONTROL: the MATCHES are exactly what they were — a marker is not a file.
                CollectionAssert.AreEqual(new[] { "remote_config.json" }, ExportScan.FindConfigFiles(root));
                CollectionAssert.AreEqual(new[] { "docs/a.md" }, ExportScan.FindDocFiles(root));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // ---- the walker's own caps (audit K7) --------------------------------------------

        [Test]
        public void TheWalkerStopsAtItsCaps_AndSaysSo()
        {
            var root = Path.Combine(Path.GetTempPath(), "novakit-caps-" + Path.GetRandomFileName());
            try
            {
                for (var i = 0; i < 12; i++) Write(root, "f" + i + ".txt", "x");
                Write(root, "a/b/c/d/deep.txt", "x");

                var notes = new List<string>();
                var seen = 0;
                foreach (var item in ExportScan.Walk(root, new ExportWalkLimits { MaxEntries = 5 }, notes)) seen++;
                Assert.LessOrEqual(seen, 5);
                Assert.AreEqual(1, notes.Count, string.Join(" | ", notes.ToArray()));
                StringAssert.Contains("stopped at 5 entries", notes[0]);

                notes.Clear();
                var deepSeen = false;
                foreach (var item in ExportScan.Walk(root, new ExportWalkLimits { MaxDepth = 2 }, notes))
                    if (item.Relative.EndsWith("deep.txt", StringComparison.Ordinal)) deepSeen = true;
                Assert.IsFalse(deepSeen, "a file four folders down is past a depth cap of 2");
                Assert.AreEqual(1, notes.Count, string.Join(" | ", notes.ToArray()));
                StringAssert.Contains("deeper than 2 levels", notes[0]);

                // POSITIVE CONTROL: with no cap in the way, both are reached and nothing is noted.
                notes.Clear();
                var all = 0;
                deepSeen = false;
                foreach (var item in ExportScan.Walk(root, new ExportWalkLimits(), notes))
                {
                    if (!item.IsDirectory) all++;
                    if (item.Relative.EndsWith("deep.txt", StringComparison.Ordinal)) deepSeen = true;
                }
                Assert.AreEqual(13, all);
                Assert.IsTrue(deepSeen);
                CollectionAssert.IsEmpty(notes);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void DocFolders_AreMatchedAgainstTheFoldersThatExist_NotProbedByName()
        {
            // On a case-INSENSITIVE volume the old code probed `docs/`, `Docs/` and
            // `Documentation/` and found the same folder twice; the file-level case-insensitive
            // de-dup hid that — and on a case-SENSITIVE volume that same de-dup silently dropped
            // `Docs/a.md` because `docs/a.md` had been seen (fresh-context audit minor).
            var root = Path.Combine(Path.GetTempPath(), "novakit-docdir-" + Path.GetRandomFileName());
            try
            {
                Write(root, "Docs/a.md", "a");
                Write(root, "Docs/b.md", "b");
                var docs = ExportScan.FindDocFiles(root);
                CollectionAssert.AreEqual(new[] { "Docs/a.md", "Docs/b.md" }, docs,
                    "the folder's REAL name, each file exactly once");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // ---- docs and config -------------------------------------------------------------

        [Test]
        public void DocAndConfigSweep_TakesWhatTheSpecNamesAndSkipsTheRest()
        {
            var root = Path.Combine(Path.GetTempPath(), "novakit-docs-" + Path.GetRandomFileName());
            try
            {
                Write(root, "README.md", "#");
                Write(root, "README", "plain");
                Write(root, "notes.md", "not at a doc root");
                Write(root, "docs/a.md", "a");
                Write(root, "docs/sub/b.txt", "b");
                Write(root, "docs/skip.png", "not a doc");
                Write(root, "Documentation/c.md", "c");

                Write(root, "remote_config.json", "{}");
                Write(root, "Assets/Cfg/remote_config_prod.json", "{}");
                Write(root, "other.json", "{}");
                Write(root, "Library/remote_config.json", "{}");
                Write(root, "node_modules/x/remote_config.json", "{}");
                Write(root, ".git/remote_config.json", "{}");
                Write(root, "Temp/remote_config.json", "{}");

                var docs = ExportScan.FindDocFiles(root);
                CollectionAssert.Contains(docs, "README.md");
                CollectionAssert.Contains(docs, "README");
                CollectionAssert.Contains(docs, "docs/a.md");
                CollectionAssert.Contains(docs, "docs/sub/b.txt");
                CollectionAssert.Contains(docs, "Documentation/c.md");
                CollectionAssert.DoesNotContain(docs, "docs/skip.png");
                CollectionAssert.DoesNotContain(docs, "notes.md");
                Assert.AreEqual(5, docs.Count, string.Join(", ", docs.ToArray()));

                var config = ExportScan.FindConfigFiles(root);
                CollectionAssert.AreEqual(new[] { "Assets/Cfg/remote_config_prod.json", "remote_config.json" }, config);

                // sorted ordinal, so two exports of one project match
                var again = ExportScan.FindDocFiles(root);
                CollectionAssert.AreEqual(docs, again);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void DocAndConfigSweep_OnAMissingRoot_IsEmpty_NotAnException()
        {
            var missing = Path.Combine(Path.GetTempPath(), "novakit-nope-" + Path.GetRandomFileName());
            CollectionAssert.IsEmpty(ExportScan.FindDocFiles(missing));
            CollectionAssert.IsEmpty(ExportScan.FindConfigFiles(missing));
            CollectionAssert.IsEmpty(ExportScan.FindDocFiles(""));
        }

        private static void Write(string root, string rel, string body)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
        }
    }
}
