using System.Collections.Generic;
using System.IO.Compression;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>Slice D — the entry-name rules and the pattern table. Field and entry names in
    /// `docs/design/2026-09-21-kit-export-format.md` ARE the wire, so these are contract tests, not
    /// implementation tests.</summary>
    public class ExportFormatTests
    {
        [Test]
        public void SchemaVersion_IsOne()
        {
            Assert.AreEqual(1, ExportFormat.SchemaVersion);
            Assert.AreEqual(1, new ExportHeader().SchemaVersion);
        }

        [Test]
        public void EntryNames_AreExactlyTheShapesTheSpecPrints()
        {
            Assert.AreEqual("part-0000.zip", ExportFormat.PartName(0));
            Assert.AreEqual("part-0007.zip", ExportFormat.PartName(7));
            Assert.AreEqual("part-0123.zip", ExportFormat.PartName(123));

            Assert.AreEqual("inventory.0000.jsonl", ExportFormat.ShardName(ExportFormat.InventoryBase, 0));
            Assert.AreEqual("dependencies.0001.jsonl", ExportFormat.ShardName(ExportFormat.DependenciesBase, 1));
            Assert.AreEqual("art/index.0000.jsonl", ExportFormat.ShardName(ExportFormat.ArtIndexBase, 0));

            Assert.AreEqual("art/files/abc123.png", ExportFormat.ArtFileEntry("abc123", ".png"));
            Assert.AreEqual("art/files/abc123.png", ExportFormat.ArtFileEntry("abc123", "png"));
            Assert.AreEqual("art/files/abc123", ExportFormat.ArtFileEntry("abc123", ""));
            Assert.AreEqual("art/thumbs/abc123.png", ExportFormat.ArtThumbEntry("abc123"));

            // The relative path is kept WHOLE, so `docs/a.md` and `Docs/a.md` cannot collide.
            Assert.AreEqual("docs/docs/guide.md", ExportFormat.DocEntry("docs/guide.md"));
            Assert.AreEqual("docs/README.md", ExportFormat.DocEntry("README.md"));
            Assert.AreEqual("config/Assets/remote_config.json", ExportFormat.ConfigEntry("Assets/remote_config.json"));
            Assert.AreEqual("docs/a/b.md", ExportFormat.DocEntry(@"a\b.md"));
        }

        [Test]
        public void IsSafeEntryName_RefusesEveryShapeTheSpecForbids()
        {
            Assert.IsTrue(ExportFormat.IsSafeEntryName("build.json"));
            Assert.IsTrue(ExportFormat.IsSafeEntryName("art/files/abc.png"));
            Assert.IsTrue(ExportFormat.IsSafeEntryName("docs/docs/a b.md"));
            Assert.IsTrue(ExportFormat.IsSafeEntryName("a/b/c/d/e.jsonl"));

            Assert.IsFalse(ExportFormat.IsSafeEntryName(null), "null");
            Assert.IsFalse(ExportFormat.IsSafeEntryName(""), "empty");
            Assert.IsFalse(ExportFormat.IsSafeEntryName("/build.json"), "leading slash");
            Assert.IsFalse(ExportFormat.IsSafeEntryName("../escape.json"), "parent");
            Assert.IsFalse(ExportFormat.IsSafeEntryName("art/../../escape.json"), "parent in the middle");
            Assert.IsFalse(ExportFormat.IsSafeEntryName("art/./a.png"), "dot segment");
            Assert.IsFalse(ExportFormat.IsSafeEntryName(".."), "bare parent");
            Assert.IsFalse(ExportFormat.IsSafeEntryName(@"art\files\a.png"), "backslash");
            Assert.IsFalse(ExportFormat.IsSafeEntryName("art//a.png"), "empty segment");
            Assert.IsFalse(ExportFormat.IsSafeEntryName("art/"), "trailing slash");
            Assert.IsFalse(ExportFormat.IsSafeEntryName("C:/art/a.png"), "drive");
            Assert.IsFalse(ExportFormat.IsSafeEntryName("a.png:stream"), "alternate stream");
            Assert.IsFalse(ExportFormat.IsSafeEntryName("a\u0000b.png"), "control character");
            Assert.IsFalse(ExportFormat.IsSafeEntryName("a\nb.png"), "newline");
        }

        [Test]
        public void CompressionFor_StoresArt_AndDeflatesTheFactsFiles()
        {
            // Art bytes do not deflate; Deflate on a gigabyte of them is a stall for nothing, and
            // Stored is what makes the writer's "does this still fit" check exact.
            Assert.AreEqual(CompressionLevel.NoCompression, ExportFormat.CompressionFor("art/files/abc.png"));
            Assert.AreEqual(CompressionLevel.NoCompression, ExportFormat.CompressionFor("art/thumbs/abc.png"));
            Assert.AreEqual(CompressionLevel.Fastest, ExportFormat.CompressionFor("art/index.0000.jsonl"));
            Assert.AreEqual(CompressionLevel.Fastest, ExportFormat.CompressionFor("build.json"));
            Assert.AreEqual(CompressionLevel.Fastest, ExportFormat.CompressionFor("inventory.0000.jsonl"));
            Assert.AreEqual(CompressionLevel.Fastest, ExportFormat.CompressionFor("docs/README.md"));
        }

        [Test]
        public void PatternTable_IsTheSevenIdsOfTheSpec_InOrder()
        {
            var expected = new[]
            {
                "unity-editor-ifdef", "debug-class", "random-seed", "remote-config",
                "tutorial-key", "feature-unlock", "playerprefs-key",
                "dev-command",   // spec §8.5
            };
            Assert.AreEqual(expected.Length, ExportFormat.Patterns.Count);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], ExportFormat.Patterns[i].Id);
                Assert.IsNotNull(ExportFormat.Patterns[i].Regex);
            }
        }

        // ---- ScrubPaths: nothing absolute leaves the machine ----

        private static KeyValuePair<string, string> Root(string path, string label) =>
            new KeyValuePair<string, string>(path, label);

        [Test]
        public void ScrubPaths_TakesTheMachineOutOfADotNetExceptionMessage()
        {
            // The exact text the first real export carried to the server.
            var raw = "Assets/Misc/ghost.png: could not read size: Could not find file " +
                      "'/Users/ido/Desktop/Projects/Game/Assets/Misc/ghost.png'.";
            var scrubbed = ExportFormat.ScrubPaths(raw, new[]
            {
                Root("/Users/ido", "~"),
                Root("/Users/ido/Desktop/Projects/Game", "<project>"),
            });
            Assert.AreEqual(
                "Assets/Misc/ghost.png: could not read size: Could not find file '<project>/Assets/Misc/ghost.png'.",
                scrubbed);
            StringAssert.DoesNotContain("/Users/", scrubbed);
            StringAssert.DoesNotContain("ido", scrubbed);
        }

        [Test]
        public void ScrubPaths_LongestRootWins_BothSeparators_AnyCase()
        {
            var roots = new[] { Root("C:\\Users\\Dana", "~"), Root("C:\\Users\\Dana\\Game", "<project>") };
            Assert.AreEqual("<project>\\Assets\\a.png",
                ExportFormat.ScrubPaths("C:\\Users\\Dana\\Game\\Assets\\a.png", roots));
            Assert.AreEqual("<project>/Assets/a.png",
                ExportFormat.ScrubPaths("C:/Users/Dana/Game/Assets/a.png", roots), "the other separator form");
            Assert.AreEqual("<project>/Assets/a.png",
                ExportFormat.ScrubPaths("c:/users/dana/game/Assets/a.png", roots), "Windows and macOS paths are case-insensitive");
            Assert.AreEqual("~\\Downloads\\x and <project>\\y",
                ExportFormat.ScrubPaths("C:\\Users\\Dana\\Downloads\\x and C:\\Users\\Dana\\Game\\y", roots));
        }

        // ---- bounded listed strings (fresh-context audit minor, 2026-09-21) --------------

        [Test]
        public void Listed_TurnsControlCharactersIntoSpaces()
        {
            // The hosted schema refuses a listed string holding a control character, and these are
            // built from exception messages — one .NET message with a newline in it would fail a
            // WHOLE export, not one file.
            Assert.AreEqual("a b c d", ExportFormat.Listed("a\nb\rc\td"));
            Assert.AreEqual("x y", ExportFormat.Listed("x" + (char)0x00 + "y"));
            Assert.AreEqual("x y", ExportFormat.Listed("x" + (char)0x7F + "y"));
            // positive control: ordinary text, including non-ASCII, goes out byte for byte
            Assert.AreEqual("Assets/Art/café中.png", ExportFormat.Listed("Assets/Art/café中.png"));
        }

        [Test]
        public void Listed_NeverCutsASurrogatePairInHalf()
        {
            // Half a pair is not valid UTF-16, and the JSON carrying it is not valid UTF-8.
            var emoji = new string('a', ExportFormat.ListedTextMax - 2) + "\U0001F600z";
            var cut = ExportFormat.Listed(emoji);
            Assert.LessOrEqual(cut.Length, ExportFormat.ListedTextMax);
            for (var i = 0; i < cut.Length; i++)
                if (char.IsHighSurrogate(cut[i]))
                    Assert.IsTrue(i + 1 < cut.Length && char.IsLowSurrogate(cut[i + 1]), "lone high surrogate at " + i);
                else
                    Assert.IsFalse(char.IsLowSurrogate(cut[i]), "lone low surrogate at " + i);
            Assert.AreEqual(cut, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(cut)),
                "it must survive a UTF-8 round trip");
        }

        [Test]
        public void UniqueEntryName_RenamesACaseCollision_InsteadOfLosingTheFile()
        {
            var taken = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            Assert.AreEqual("docs/docs/a.md", ExportFormat.UniqueEntryName("docs/docs/a.md", taken));
            // `Docs/a.md` on a case-sensitive volume is a DIFFERENT FILE that a case-folding reader
            // would see as the same entry. It gets a name, not a grave: the old sweep dropped it.
            Assert.AreEqual("docs/Docs/a~2.md", ExportFormat.UniqueEntryName("docs/Docs/a.md", taken));
            Assert.AreEqual("docs/DOCS/A~3.MD", ExportFormat.UniqueEntryName("docs/DOCS/A.MD", taken));
            // positive control: a name nothing has claimed is returned untouched.
            Assert.AreEqual("docs/docs/b.md", ExportFormat.UniqueEntryName("docs/docs/b.md", taken));
            // no extension at all
            var more = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            Assert.AreEqual("docs/README", ExportFormat.UniqueEntryName("docs/README", more));
            Assert.AreEqual("docs/readme~2", ExportFormat.UniqueEntryName("docs/readme", more));
            Assert.IsTrue(ExportFormat.IsSafeEntryName("docs/docs/a~2.md"), "the renamed entry is still a legal name");
        }

        [Test]
        public void SweepPolicy_FiresOnACountORAByteBudget_AndATriggerOfZeroIsOff()
        {
            // N4: the thumbnail pass sweeps every 256 thumbnails, and a COUNT is not a bound on
            // memory — 256 of a shipped game's 2048² textures is several GB between sweeps. The
            // count trigger stays (a byte estimate that reads back 0 would never fire on its own).
            Assert.IsFalse(ExportSweepPolicy.ShouldSweep(255, 0, 256, 512));
            Assert.IsTrue(ExportSweepPolicy.ShouldSweep(256, 0, 256, 512), "the count trigger");
            Assert.IsTrue(ExportSweepPolicy.ShouldSweep(1, 512, 256, 512), "the byte trigger, long before the count");
            Assert.IsTrue(ExportSweepPolicy.ShouldSweep(1, 99999, 256, 512));
            Assert.IsFalse(ExportSweepPolicy.ShouldSweep(1, 511, 256, 512));

            // A trigger that is off must be OFF, not always-on — a caps value of 0 disables it.
            Assert.IsFalse(ExportSweepPolicy.ShouldSweep(9999, 9999, 0, 0));
            Assert.IsTrue(ExportSweepPolicy.ShouldSweep(9999, 0, 1, 0), "count only");
            Assert.IsTrue(ExportSweepPolicy.ShouldSweep(0, 1, 0, 1), "bytes only");
        }

        [Test]
        public void Listed_ReplacesTheThreeSEPARATORCharactersNewtonsoftEscapes_NotOnlyTheControlRange()
        {
            // N5: `Listed` scrubbed everything below U+0020 and U+007F, so "3 bytes per character is
            // the worst case" was the claim both sides' body-size proofs rest on. Newtonsoft escapes
            // U+0085 (NEL), U+2028 (LINE SEPARATOR) and U+2029 (PARAGRAPH SEPARATOR) as SIX bytes
            // each, so a header full of them costs twice what that proof allowed (audit round 3, N5).
            //
            // Built with (char) casts and never written as literals: U+2028 and U+2029 END A LINE
            // for the C# lexer itself, so a source file holding one does not compile.
            const char nel = (char)0x0085, ls = (char)0x2028, ps = (char)0x2029;

            Assert.AreEqual("a b c d", ExportFormat.Listed("a" + nel + "b" + ls + "c" + ps + "d"));
            Assert.AreEqual("   ", ExportFormat.Listed(new string(new[] { nel, ls, ps })));

            // the control range is still scrubbed, and ordinary text is still untouched
            Assert.AreEqual("a b", ExportFormat.Listed("a\nb"));
            Assert.AreEqual("a b", ExportFormat.Listed("a" + (char)0x007f + "b"));
            Assert.AreEqual("Assets/Art/hero.png", ExportFormat.Listed("Assets/Art/hero.png"));

            // …and a character NEXT DOOR in the same blocks is NOT touched: the rule is these three
            // exactly, not "anything that looks like whitespace".
            var neighbours = "a" + (char)0x0084 + "b" + (char)0x2027 + "c" + (char)0x202f + "d";
            Assert.AreEqual(neighbours, ExportFormat.Listed(neighbours));
        }

        [Test]
        public void ScrubPaths_LeavesOrdinaryTextAlone_AndIgnoresARootThatWouldEatEverySlash()
        {
            // The positive control: a message with no machine path comes back byte for byte.
            const string plain = "Assets/Art/hero.png: could not read dependencies: the asset is corrupt";
            Assert.AreEqual(plain, ExportFormat.ScrubPaths(plain, new[] { Root("/Users/ido/Game", "<project>") }));
            // A root of "/" (or empty, or null) must never be applied.
            Assert.AreEqual(plain, ExportFormat.ScrubPaths(plain, new[] { Root("/", "X"), Root("", "Y"), Root(null!, "Z") }));
            Assert.AreEqual("", ExportFormat.ScrubPaths(null, new[] { Root("/Users/ido", "~") }));
        }
    }
}
