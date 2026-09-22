using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Slice D — the part packer, with no editor and no assets. Everything here is deliberately
    /// forced with TINY caps rather than with a 32 MiB fixture: the rule under test is the boundary,
    /// not the number.
    /// </summary>
    public class ExportArchiveWriterTests
    {
        private string _dir = "";

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "novakit-export-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        private static ExportCaps TinyCaps() => new ExportCaps
        {
            PartSoftBytes = 4096,
            PartMaxBytes = 65536,
            JsonlShardRows = 3,
            JsonlShardBytes = 1 << 20,
        };

        /// <summary>Incompressible bytes, so a Stored entry's size on disk is the size we asked for.</summary>
        private static byte[] Noise(int n, int seed)
        {
            var rng = new System.Random(seed);
            var b = new byte[n];
            rng.NextBytes(b);
            return b;
        }

        // ---- packing ---------------------------------------------------------------------

        [Test]
        public void GreedyPacking_ClosesAPartAtTheSoftCap_AndNeverExceedsTheHardCap()
        {
            var caps = TinyCaps();
            IReadOnlyList<ExportPartInfo> parts;
            using (var w = new ExportArchiveWriter(_dir, caps))
            {
                for (var i = 0; i < 12; i++)
                    w.AddBytes("art/files/e" + i + ".bin", Noise(2048, i));
                parts = w.Finish();
            }

            Assert.GreaterOrEqual(parts.Count, 3, "2 KiB entries against a 4 KiB soft cap must span several parts");

            var totalEntries = 0;
            foreach (var p in parts)
            {
                var path = Path.Combine(_dir, p.Name);
                Assert.IsTrue(File.Exists(path), p.Name + " is on disk");
                Assert.LessOrEqual(new FileInfo(path).Length, caps.PartMaxBytes, p.Name + " is under partMaxBytes");
                Assert.Greater(p.Entries, 0, "a part always holds at least one entry");
                totalEntries += p.Entries;
            }
            Assert.AreEqual(12, totalEntries);
        }

        [Test]
        public void APartNeverExceedsPartMaxBytes_EvenWhenTheNextEntryIsLarge()
        {
            // The soft cap ALONE does not guarantee the hard cap: a part just under soft, plus one
            // big entry, overshoots. The writer must look ahead at the incoming size.
            //
            // REWRITTEN 2026-09-21 (fresh-context audit): the earlier numbers could not fail. They
            // were 2,000 + 16,000 + 16,000 against a 24,576 ceiling, and the SOFT cap (4,096) rolled
            // every part long before the ceiling was near — delete the look-ahead and the test still
            // passed. These leave the soft cap out of reach on purpose: two 150,000-byte entries
            // total 300 KB against a 256 KiB ceiling while the soft cap sits at 250,000, so ONLY the
            // predictive roll can keep the part legal.
            var caps = new ExportCaps { PartSoftBytes = 250000, PartMaxBytes = 262144, JsonlShardRows = 3 };
            IReadOnlyList<ExportPartInfo> parts;
            using (var w = new ExportArchiveWriter(_dir, caps))
            {
                w.AddBytes("art/files/a.bin", Noise(150000, 1));
                w.AddBytes("art/files/b.bin", Noise(150000, 2));
                parts = w.Finish();
            }

            Assert.AreEqual(2, parts.Count, "without the look-ahead both entries land in one 300 KB part");
            foreach (var p in parts)
                Assert.LessOrEqual(new FileInfo(Path.Combine(_dir, p.Name)).Length, caps.PartMaxBytes, p.Name);
        }

        [Test]
        public void APartNeverExceedsPartMaxBytes_EvenWithHundredsOfTinyEntries()
        {
            // The 64 KiB reserve was a CONSTANT, but the zip's central directory is not: it costs
            // ~46 bytes plus the entry NAME per entry, and it is written after the last one. Several
            // hundred entries with long names plus one large file therefore sailed past the ceiling
            // the server refuses (fresh-context audit, 2026-09-21). The reserve has to scale.
            var caps = new ExportCaps { PartSoftBytes = 262144, PartMaxBytes = 262144 };
            var longName = new string('a', 140);
            IReadOnlyList<ExportPartInfo> parts;
            using (var w = new ExportArchiveWriter(_dir, caps))
            {
                for (var i = 0; i < 700; i++)
                    w.AddBytes("art/files/" + longName + i + ".bin", new byte[] { 7 });
                w.AddBytes("art/files/big.bin", Noise(60000, 4));
                parts = w.Finish();
            }

            var totalEntries = 0;
            foreach (var p in parts)
            {
                var onDisk = new FileInfo(Path.Combine(_dir, p.Name)).Length;
                Assert.LessOrEqual(onDisk, caps.PartMaxBytes,
                    p.Name + " is " + onDisk + " bytes — the server refuses the header for it");
                Assert.AreEqual(onDisk, p.Bytes, p.Name + " byte count");
                totalEntries += p.Entries;
            }
            Assert.AreEqual(701, totalEntries, "and nothing was dropped to make room");
        }

        [Test]
        public void PartInfo_BytesAndSha256_AreTheFileOnDisk()
        {
            IReadOnlyList<ExportPartInfo> parts;
            using (var w = new ExportArchiveWriter(_dir, TinyCaps()))
            {
                for (var i = 0; i < 8; i++)
                    w.AddBytes("art/files/e" + i + ".bin", Noise(1500, i));
                parts = w.Finish();
            }

            Assert.Greater(parts.Count, 1);
            for (var i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                Assert.AreEqual(i, p.Index);
                Assert.AreEqual(ExportFormat.PartName(i), p.Name);

                var path = Path.Combine(_dir, p.Name);
                Assert.AreEqual(new FileInfo(path).Length, p.Bytes, p.Name + " byte count");

                // Recomputed here independently of the writer's own helper.
                string expected;
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(path))
                {
                    var sb = new StringBuilder();
                    foreach (var b in sha.ComputeHash(fs)) sb.Append(b.ToString("x2"));
                    expected = sb.ToString();
                }
                Assert.AreEqual(expected, p.Sha256, p.Name + " sha256");
                Assert.AreEqual(64, p.Sha256.Length);
                StringAssert.IsMatch("^[0-9a-f]{64}$", p.Sha256);

                using (var fs = File.OpenRead(path))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                    Assert.AreEqual(p.Entries, zip.Entries.Count, p.Name + " entry count");
            }
        }

        [Test]
        public void EveryEntry_ReadsBackByteEqual()
        {
            var written = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using (var w = new ExportArchiveWriter(_dir, TinyCaps()))
            {
                for (var i = 0; i < 10; i++)
                {
                    var name = "art/files/e" + i + ".bin";
                    var bytes = Noise(1200 + i * 37, i);
                    written[name] = bytes;
                    w.AddBytes(name, bytes);
                }
                w.Finish();
            }

            var read = ReadAllEntries(_dir);
            Assert.AreEqual(written.Count, read.Count);
            foreach (var kv in written)
            {
                Assert.IsTrue(read.ContainsKey(kv.Key), kv.Key + " is in a part");
                CollectionAssert.AreEqual(kv.Value, read[kv.Key], kv.Key + " bytes");
            }
        }

        [Test]
        public void AddFile_StreamCopiesTheSource()
        {
            var src = Path.Combine(_dir, "source.bin");
            var bytes = Noise(150000, 9);
            File.WriteAllBytes(src, bytes);

            using (var w = new ExportArchiveWriter(_dir, new ExportCaps()))
            {
                w.AddFile("art/files/copy.bin", src);
                w.Finish();
            }

            var read = ReadAllEntries(_dir);
            CollectionAssert.AreEqual(bytes, read["art/files/copy.bin"]);
        }

        [Test]
        public void AddFile_RefusesASourceOVERTheCapItWasGiven_AndLeavesNoEntry()
        {
            // M4 (audit round 4). The collector stats every candidate when it PLANS, then stats it
            // again and reads it when it WRITES — but the cap was never compared with the bytes
            // that actually came back. Every entry being at or under `artFileMaxBytes` is the whole
            // reason no part can pass the 32 MiB ceiling the server refuses, so the check has to
            // sit against the read, which is the last word on how big the file was.
            var src = Path.Combine(_dir, "grew.bin");
            File.WriteAllBytes(src, Noise(20000, 3));

            using (var w = new ExportArchiveWriter(_dir, new ExportCaps()))
            {
                w.AddBytes("build.json", new byte[] { 1 });
                var e = Assert.Catch<Exception>(() => w.AddFile("art/files/grew.bin", src, 10000));
                Assert.IsNotInstanceOf<ExportArchiveFatalException>(e,
                    "a file over its cap is a per-file fact, never a broken archive");
                StringAssert.Contains("20000", e!.Message, e.Message);

                // POSITIVE CONTROL: the same file under a cap that covers it goes in whole, and a
                // cap of 0 means "no cap", which is how every other caller uses it.
                Assert.AreEqual(20000L, w.AddFile("art/files/ok.bin", src, 20000));
                Assert.AreEqual(20000L, w.AddFile("art/files/nocap.bin", src));
                w.Finish();
            }

            var read = ReadAllEntries(_dir);
            Assert.IsFalse(read.ContainsKey("art/files/grew.bin"),
                "a source over its cap must leave NO entry — the part header would not count it");
            Assert.IsTrue(read.ContainsKey("art/files/ok.bin"));
            Assert.IsTrue(read.ContainsKey("art/files/nocap.bin"));
        }

        [Test]
        public void AddFile_LeavesNoEntryBehindWhenTheSourceCannotBeRead()
        {
            // ATOMIC PER ENTRY. It used to stream the source straight into the entry, so a copy that
            // failed halfway left a truncated entry the part header did not count — and the server
            // compares `parts[].entries` with the zip's own central directory, so ONE unreadable
            // file failed the WHOLE part (fresh-context audit K4).
            var missing = Path.Combine(_dir, "not-here.bin");
            IReadOnlyList<ExportPartInfo> parts;
            using (var w = new ExportArchiveWriter(_dir, new ExportCaps()))
            {
                w.AddBytes("build.json", new byte[] { 1 });
                Assert.Catch<Exception>(() => w.AddFile("art/files/gone.png", missing));
                Assert.IsNotInstanceOf<ExportArchiveFatalException>(
                    Assert.Catch<Exception>(() => w.AddFile("art/files/gone2.png", missing)),
                    "a missing SOURCE is a per-file fact, never a broken archive");
                // …and the writer is still usable, which is the whole point.
                w.AddBytes("identity.json", new byte[] { 2 });
                parts = w.Finish();
            }

            var read = ReadAllEntries(_dir);
            CollectionAssert.AreEquivalent(new[] { "build.json", "identity.json" }, new List<string>(read.Keys));
            var entries = 0;
            foreach (var p in parts)
            {
                entries += p.Entries;
                using (var fs = File.OpenRead(Path.Combine(_dir, p.Name)))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                    Assert.AreEqual(p.Entries, zip.Entries.Count,
                        p.Name + ": the header's entry count IS the zip's central directory");
            }
            Assert.AreEqual(2, entries);
        }

        [Test]
        public void APartThatCannotBeClosed_IsFatal_NotAPerFileError()
        {
            // A part that cannot be closed leaves a GAP in the part indexes, and the hosted side
            // refuses the header for it. Forced for real by unlinking the open part (POSIX allows
            // it), so `Sha256Hex` cannot reopen it when ClosePart measures the file.
            var caps = new ExportCaps { PartSoftBytes = 1, PartMaxBytes = 65536 };
            using (var w = new ExportArchiveWriter(_dir, caps))
            {
                w.AddBytes("art/files/a.bin", Noise(64, 1));
                var open = Path.Combine(_dir, ExportFormat.PartName(0));
                Assert.IsTrue(File.Exists(open));
                try { File.Delete(open); }
                catch (Exception) { Assert.Ignore("this platform will not unlink an open file"); }
                if (File.Exists(open)) Assert.Ignore("this platform will not unlink an open file");

                // The soft cap of 1 byte makes the NEXT entry roll the part, which closes it.
                Assert.Throws<ExportArchiveFatalException>(() => w.AddBytes("art/files/b.bin", Noise(64, 2)));
                // …and the archive refuses to be used afterwards rather than quietly carrying on.
                Assert.Throws<ExportArchiveFatalException>(() => w.AddBytes("art/files/c.bin", Noise(64, 3)));
            }
        }

        [Test]
        public void UnsafeAndDuplicateEntryNames_AreRefused()
        {
            using (var w = new ExportArchiveWriter(_dir, new ExportCaps()))
            {
                foreach (var bad in new[] { "/abs.json", "../escape.json", @"back\slash.json", "a//b.json", "trailing/", "" })
                    Assert.Throws<ArgumentException>(() => w.AddBytes(bad, new byte[] { 1 }), "entry name: " + bad);

                w.AddBytes("build.json", new byte[] { 1 });
                Assert.Throws<ArgumentException>(() => w.AddBytes("build.json", new byte[] { 2 }), "duplicate");
                w.Finish();
            }
        }

        // ---- jsonl sharding --------------------------------------------------------------

        [Test]
        public void Jsonl_ShardsByRowCount()
        {
            var caps = new ExportCaps { JsonlShardRows = 3, JsonlShardBytes = 1 << 20 };
            int rows;
            using (var w = new ExportArchiveWriter(_dir, caps))
            {
                using (var j = w.OpenJsonl(ExportFormat.InventoryBase))
                {
                    for (var i = 0; i < 7; i++) j.WriteRow(new JObject { ["i"] = i });
                    rows = j.Rows;
                }
                // Shards are only complete once the jsonl writer is disposed (it flushes the tail).
                w.Finish();
            }
            Assert.AreEqual(7, rows);

            var read = ReadAllEntries(_dir);
            Assert.IsTrue(read.ContainsKey("inventory.0000.jsonl"));
            Assert.IsTrue(read.ContainsKey("inventory.0001.jsonl"));
            Assert.IsTrue(read.ContainsKey("inventory.0002.jsonl"));
            Assert.IsFalse(read.ContainsKey("inventory.0003.jsonl"));
            Assert.AreEqual(3, LineCount(read["inventory.0000.jsonl"]));
            Assert.AreEqual(3, LineCount(read["inventory.0001.jsonl"]));
            Assert.AreEqual(1, LineCount(read["inventory.0002.jsonl"]));

            // and the rows survive intact, in order
            var all = new List<int>();
            for (var s = 0; s < 3; s++)
                foreach (var line in Lines(read[ExportFormat.ShardName(ExportFormat.InventoryBase, s)]))
                    all.Add(JObject.Parse(line)["i"]!.Value<int>());
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5, 6 }, all);
        }

        [Test]
        public void Jsonl_ShardsByRawBytes()
        {
            var caps = new ExportCaps { JsonlShardRows = 100000, JsonlShardBytes = 200 };
            List<string> shards;
            using (var w = new ExportArchiveWriter(_dir, caps))
            {
                ExportJsonlWriter j = w.OpenJsonl(ExportFormat.PatternsBase);
                for (var i = 0; i < 20; i++)
                    j.WriteRow(new JObject { ["text"] = new string('x', 40), ["i"] = i });
                j.Dispose();
                shards = new List<string>(j.Shards);
                Assert.AreEqual(20, j.Rows);
                w.Finish();
            }

            Assert.GreaterOrEqual(shards.Count, 3, "20 rows of ~55 bytes against a 200-byte shard cap");
            var read = ReadAllEntries(_dir);
            var total = 0;
            foreach (var s in shards)
            {
                Assert.IsTrue(read.ContainsKey(s), s);
                total += LineCount(read[s]);
            }
            Assert.AreEqual(20, total);
        }

        [Test]
        public void Jsonl_WithNoRows_StillEmitsShardZero()
        {
            List<string> shards;
            using (var w = new ExportArchiveWriter(_dir, new ExportCaps()))
            {
                var j = w.OpenJsonl(ExportFormat.PatternsBase);
                j.Dispose();
                shards = new List<string>(j.Shards);
                Assert.AreEqual(0, j.Rows);
                w.Finish();
            }
            CollectionAssert.AreEqual(new[] { "patterns.0000.jsonl" }, shards);
            Assert.AreEqual(0, ReadAllEntries(_dir)["patterns.0000.jsonl"].Length);
        }

        [Test]
        public void Jsonl_IsUtf8WithNoByteOrderMark()
        {
            using (var w = new ExportArchiveWriter(_dir, new ExportCaps()))
            {
                using (var j = w.OpenJsonl(ExportFormat.InventoryBase))
                    j.WriteRow(new JObject { ["path"] = "Assets/Art/café.png" });
                w.Finish();
            }
            var bytes = ReadAllEntries(_dir)["inventory.0000.jsonl"];
            Assert.AreNotEqual(0xEF, bytes[0], "a BOM would be row 0's invisible first character");
            Assert.AreEqual((byte)'{', bytes[0]);
            Assert.AreEqual((byte)'\n', bytes[bytes.Length - 1]);
            Assert.AreEqual(-1, Array.IndexOf(bytes, (byte)'\r'));
            Assert.AreEqual("Assets/Art/café.png",
                JObject.Parse(new UTF8Encoding(false).GetString(bytes).Trim())["path"]!.Value<string>());
        }

        [Test]
        public void TwoJsonlWritersAtOnce_AreRefused_BecauseEntryOrderIsPartOfTheContract()
        {
            using (var w = new ExportArchiveWriter(_dir, new ExportCaps()))
            {
                var a = w.OpenJsonl(ExportFormat.InventoryBase);
                Assert.Throws<InvalidOperationException>(() => w.OpenJsonl(ExportFormat.DependenciesBase));
                Assert.Throws<InvalidOperationException>(() => w.Finish());
                a.Dispose();
                w.Finish();
            }
        }

        // ---- helpers ---------------------------------------------------------------------

        internal static Dictionary<string, byte[]> ReadAllEntries(string outDir)
        {
            var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var parts = Directory.GetFiles(outDir, "part-*.zip");
            Array.Sort(parts, StringComparer.Ordinal);
            foreach (var f in parts)
                using (var fs = File.OpenRead(f))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                    foreach (var e in zip.Entries)
                    {
                        using (var s = e.Open())
                        using (var ms = new MemoryStream())
                        {
                            s.CopyTo(ms);
                            map[e.FullName] = ms.ToArray();
                        }
                    }
            return map;
        }

        /// <summary>Entry names in the order the parts hold them — spec §3 pins that order.</summary>
        internal static List<string> ReadEntryOrder(string outDir)
        {
            var names = new List<string>();
            var parts = Directory.GetFiles(outDir, "part-*.zip");
            Array.Sort(parts, StringComparer.Ordinal);
            foreach (var f in parts)
                using (var fs = File.OpenRead(f))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                    foreach (var e in zip.Entries)
                        names.Add(e.FullName);
            return names;
        }

        internal static List<string> Lines(byte[] bytes)
        {
            var list = new List<string>();
            foreach (var line in new UTF8Encoding(false).GetString(bytes).Split('\n'))
                if (line.Length > 0) list.Add(line);
            return list;
        }

        internal static int LineCount(byte[] bytes) => Lines(bytes).Count;
    }
}
