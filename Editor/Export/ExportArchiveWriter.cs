using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// THE ARCHIVE ITSELF IS BROKEN — not one file in it. A part whose entry count no longer
    /// matches its central directory, or a part index the header can never name, is refused WHOLE
    /// by the hosted side (spec §7.3), so this can never be downgraded to a per-file `errors` row
    /// the way a missing source file is. The collector maps it, and only it, to a fatal export.
    /// </summary>
    public sealed class ExportArchiveFatalException : Exception
    {
        public ExportArchiveFatalException(string message) : base(message) { }
        public ExportArchiveFatalException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Packs the export's entries greedily into `part-NNNN.zip` files under `outDir` (spec §1/§3).
    ///
    /// ONE PART IS ONE UPLOAD. The kit never holds more than a part's worth of the export open, and
    /// a failed upload retries one part instead of the whole export. A part closes once its file on
    /// disk reaches <c>partSoftBytes</c>, or when the incoming entry would not still fit under
    /// <c>partMaxBytes</c> — the soft cap alone does NOT guarantee the hard cap (a part at
    /// soft-minus-one plus an 8 MiB art file overshoots), so both checks run.
    ///
    /// Pure <c>System.IO</c> and <c>System.IO.Compression</c> — no Unity API, so every rule here is
    /// covered by EditMode tests with no editor state at all. VERIFIED 2026-09-21: `ZipArchive`,
    /// `CompressionLevel` and `SHA256` resolve under this package's asmdef (overrideReferences with
    /// only Newtonsoft.Json.dll) under BOTH the .NET Standard 2.1 and the .NET Framework API
    /// compatibility levels. `ZipFile` is deliberately never used — it lives in
    /// System.IO.Compression.FileSystem.dll, which is the assembly that historically needs an
    /// explicit reference.
    /// </summary>
    public sealed class ExportArchiveWriter : IDisposable
    {
        /// <summary>Fixed headroom: the end-of-central-directory record (22 bytes), a Zip64 one if
        /// the runtime writes it, and slack. The PER-ENTRY part of the reserve is NOT here — see
        /// <see cref="CentralDirectoryCost"/>.</summary>
        private const long PartReserveBaseBytes = 8L * 1024L;

        /// <summary>
        /// What ONE entry costs in the central directory, which is written after the last entry and
        /// is therefore not in <c>_partLength</c> while a part is open: a 46-byte fixed record plus
        /// the entry NAME again, plus slack for a Zip64/UTF-8 extra field.
        ///
        /// This used to be folded into one 64 KiB constant, and a constant is the wrong shape:
        /// several hundred entries with long names cost more than 64 KiB of central directory on
        /// their own, so a part could pass <c>partMaxBytes</c> and be refused at the header door
        /// (fresh-context audit, 2026-09-21). A part with few entries now also gets its 8 MiB of
        /// headroom back.
        /// </summary>
        private static long CentralDirectoryCost(string entryName) =>
            46L + Encoding.UTF8.GetByteCount(entryName ?? "") + 64L;

        /// <summary>…and what the NEXT entry costs before its bytes: a local file header, which is
        /// 30 bytes plus the name again.</summary>
        private static long LocalHeaderCost(string entryName) =>
            30L + Encoding.UTF8.GetByteCount(entryName ?? "") + 64L;

        private readonly string _outDir;
        private readonly ExportCaps _caps;
        private readonly List<ExportPartInfo> _parts = new List<ExportPartInfo>();
        private readonly HashSet<string> _entryNames = new HashSet<string>(StringComparer.Ordinal);

        private FileStream? _fs;
        private ZipArchive? _zip;
        private int _partIndex = -1;
        private int _entries;
        private long _partLength;
        /// <summary>The central directory the OPEN part has accrued so far.</summary>
        private long _centralDirBytes;
        private bool _finished;
        private bool _disposed;
        private bool _faulted;
        private ExportJsonlWriter? _openJsonl;

        public ExportArchiveWriter(string outDir, ExportCaps caps)
        {
            if (string.IsNullOrEmpty(outDir)) throw new ArgumentException("outDir is required", nameof(outDir));
            _outDir = outDir;
            _caps = caps ?? new ExportCaps();
            Directory.CreateDirectory(_outDir);
        }

        internal ExportCaps Caps => _caps;

        /// <summary>The directory the parts are written to.</summary>
        public string OutDir => _outDir;

        /// <summary>Parts closed so far, plus the one still open.</summary>
        public int PartCount => _parts.Count + (_fs != null ? 1 : 0);

        public void AddBytes(string entryName, byte[]? bytes)
        {
            var b = bytes ?? Array.Empty<byte>();
            var entry = BeginEntry(entryName, b.LongLength);
            // PAST THIS LINE THE ENTRY EXISTS. A throw here leaves the part holding an entry that
            // `_entries` does not count, and the server compares `parts[].entries` with the zip's
            // own central directory — so it is the whole part that is lost, not this file.
            try
            {
                using (var s = entry.Open())
                    s.Write(b, 0, b.Length);
            }
            catch (Exception e)
            {
                throw Fault("could not write '" + entryName + "' into " + ExportFormat.PartName(_partIndex), e);
            }
            EndEntry(entryName);
        }

        /// <summary>
        /// Copies <paramref name="sourcePath"/> into the archive, ATOMICALLY with respect to the
        /// part: the whole source is read BEFORE the zip entry is created, so a file that is
        /// deleted, locked or short-read leaves no entry at all and is a per-file `errors` row.
        ///
        /// It used to stream straight into the entry, which is cheaper but not atomic: a copy that
        /// failed halfway left a truncated entry the part header did not count, and the server
        /// refuses the whole part for that (fresh-context audit, 2026-09-21). The buffer is bounded
        /// by <c>artFileMaxBytes</c> (8 MiB) and one file is held at a time, so the cost is a
        /// transient 8 MiB, not the gigabyte the streaming comment was written to avoid.
        /// </summary>
        /// <param name="maxBytes">The per-entry cap, or 0 for none. It is compared with the bytes
        /// that ACTUALLY came back, not with a stat: the caller's stat and this read are two
        /// different moments, and every entry being at or under the cap is the whole reason no part
        /// can pass the ceiling the server refuses (audit round 4, M4). Over the cap throws BEFORE
        /// an entry exists, so it is a per-file `errors` row, never a broken part.</param>
        /// <returns>How many bytes really went in — which is what the art index reports, because
        /// the PLANNED size is a number from before the file was read (spec §2a).</returns>
        public long AddFile(string entryName, string sourcePath, long maxBytes = 0)
        {
            if (string.IsNullOrEmpty(sourcePath)) throw new ArgumentException("sourcePath is required", nameof(sourcePath));
            var length = new FileInfo(sourcePath).Length;
            if (length > int.MaxValue)
                throw new IOException("'" + sourcePath + "' is " + length + " bytes — too large for one zip entry");
            var bytes = File.ReadAllBytes(sourcePath);
            if (bytes.LongLength != length)
                throw new IOException("'" + sourcePath + "' changed size while it was being read");
            if (maxBytes > 0 && bytes.LongLength > maxBytes)
                throw new IOException("'" + sourcePath + "' read back " + bytes.LongLength +
                                      " bytes, past its " + maxBytes + "-byte cap");
            AddBytes(entryName, bytes);
            return bytes.LongLength;
        }

        /// <summary>
        /// Opens a sharded jsonl stream. `inventory` -> `inventory.0000.jsonl`, `inventory.0001.jsonl`…;
        /// `art/index` -> `art/index.0000.jsonl`. A shard closes at <c>jsonlShardRows</c> rows or
        /// <c>JsonlShardBytes</c> raw bytes, whichever comes first, so no single entry can push a
        /// part over its ceiling. Only one may be open at a time — entry ORDER is part of the
        /// contract (spec §3), and interleaving two writers would shuffle it.
        /// </summary>
        public ExportJsonlWriter OpenJsonl(string baseName)
        {
            ThrowIfUnusable();
            if (string.IsNullOrEmpty(baseName)) throw new ArgumentException("baseName is required", nameof(baseName));
            if (_openJsonl != null)
                throw new InvalidOperationException("a jsonl writer is already open; dispose it before opening another");
            var w = new ExportJsonlWriter(this, baseName);
            _openJsonl = w;
            return w;
        }

        internal void JsonlClosed(ExportJsonlWriter w)
        {
            if (ReferenceEquals(_openJsonl, w)) _openJsonl = null;
        }

        /// <summary>Closes the last part and returns every part with its real on-disk byte count and
        /// lowercase-hex sha256.</summary>
        public IReadOnlyList<ExportPartInfo> Finish()
        {
            if (_finished) return _parts;
            if (_openJsonl != null)
                throw new InvalidOperationException("a jsonl writer is still open; dispose it before Finish()");
            ClosePart();
            _finished = true;
            return _parts;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_finished)
            {
                // Never leave a part's handle open — the caller deletes OutDir afterwards.
                try { ClosePart(); } catch (Exception) { /* a half-written part is already lost */ }
                _finished = true;
            }
        }

        // ---- internals -------------------------------------------------------------------

        private void ThrowIfUnusable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ExportArchiveWriter));
            if (_faulted) throw new ExportArchiveFatalException("this export archive already failed and cannot be written to");
            if (_finished) throw new InvalidOperationException("Finish() has already been called");
        }

        /// <summary>Marks the archive unusable and wraps the cause. Everything thrown through here
        /// is FATAL to the export: the part on disk no longer matches what the header would say
        /// about it, and the hosted side refuses a part whose entry count is off by one just as it
        /// refuses a gap in the part indexes.</summary>
        private ExportArchiveFatalException Fault(string what, Exception cause)
        {
            _faulted = true;
            return new ExportArchiveFatalException(what + ": " + cause.Message, cause);
        }

        private ZipArchiveEntry BeginEntry(string entryName, long incomingBytes)
        {
            ThrowIfUnusable();
            if (!ExportFormat.IsSafeEntryName(entryName))
                throw new ArgumentException("unsafe zip entry name: " + (entryName ?? "<null>"), nameof(entryName));
            if (!_entryNames.Add(entryName))
                throw new ArgumentException("duplicate zip entry name: " + entryName, nameof(entryName));
            RollIfNeeded(entryName, incomingBytes);
            EnsurePart();
            try
            {
                return _zip!.CreateEntry(entryName, ExportFormat.CompressionFor(entryName));
            }
            catch (Exception e)
            {
                _entryNames.Remove(entryName);
                throw Fault("could not create '" + entryName + "' in " + ExportFormat.PartName(_partIndex), e);
            }
        }

        private void EndEntry(string entryName)
        {
            if (_fs == null) return;
            try
            {
                _fs.Flush();
                _partLength = _fs.Length;
            }
            catch (Exception e)
            {
                throw Fault("could not flush " + ExportFormat.PartName(_partIndex), e);
            }
            _centralDirBytes += CentralDirectoryCost(entryName);
            _entries++;
        }

        private void RollIfNeeded(string entryName, long incomingBytes)
        {
            if (_fs == null || _entries == 0) return;   // a part always holds at least one entry
            if (_partLength >= _caps.PartSoftBytes) { ClosePart(); return; }
            var projected = _partLength
                            + incomingBytes
                            + LocalHeaderCost(entryName)
                            + _centralDirBytes
                            + CentralDirectoryCost(entryName)
                            + PartReserveBaseBytes;
            if (projected > _caps.PartMaxBytes) ClosePart();
        }

        private void EnsurePart()
        {
            if (_fs != null) return;
            _partIndex++;
            var path = Path.Combine(_outDir, ExportFormat.PartName(_partIndex));
            try
            {
                _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                _zip = new ZipArchive(_fs, ZipArchiveMode.Create, leaveOpen: true);
            }
            catch (Exception e)
            {
                throw Fault("could not open " + ExportFormat.PartName(_partIndex), e);
            }
            _entries = 0;
            _partLength = 0;
            _centralDirBytes = 0;
        }

        private void ClosePart()
        {
            if (_fs == null) return;
            var index = _partIndex;
            var entries = _entries;
            try
            {
                _zip?.Dispose();       // writes the central directory
            }
            catch (Exception e)
            {
                _zip = null;
                try { _fs.Dispose(); } catch (Exception) { /* already lost */ }
                _fs = null;
                throw Fault("could not close " + ExportFormat.PartName(index), e);
            }
            finally
            {
                _zip = null;
                if (_fs != null) _fs.Dispose();
                _fs = null;
                _entries = 0;
                _partLength = 0;
                _centralDirBytes = 0;
            }

            var name = ExportFormat.PartName(index);
            var path = Path.Combine(_outDir, name);
            try
            {
                _parts.Add(new ExportPartInfo
                {
                    Index = index,
                    Name = name,
                    Bytes = new FileInfo(path).Length,
                    Sha256 = Sha256Hex(path),
                    Entries = entries,
                });
            }
            catch (Exception e)
            {
                throw Fault("could not measure " + name, e);
            }
        }

        /// <summary>Streams the file through SHA256 — the parts are up to 32 MiB and there can be
        /// dozens of them.</summary>
        internal static string Sha256Hex(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                var hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
