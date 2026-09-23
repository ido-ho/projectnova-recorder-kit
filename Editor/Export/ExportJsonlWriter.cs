using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// One sharded jsonl facts file inside the export (spec §2/§4). Rows buffer in memory only up
    /// to a shard (16 MiB raw by default), then the shard goes into the archive and the buffer is
    /// reset — so a 12,000-asset inventory never sits in editor memory twice.
    ///
    /// One row per line, UTF-8 with NO byte-order mark, `\n` only. A BOM would land in the middle
    /// of the hosted side's line reader as an invisible first character of row 0.
    /// </summary>
    public sealed class ExportJsonlWriter : IDisposable
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly ExportArchiveWriter _owner;
        private readonly string _baseName;
        private readonly List<string> _shards = new List<string>();
        private readonly MemoryStream _buffer = new MemoryStream();

        private int _bufferedRows;
        private int _rows;
        private int _shardIndex;
        private bool _closed;

        internal ExportJsonlWriter(ExportArchiveWriter owner, string baseName)
        {
            _owner = owner;
            _baseName = baseName;
        }

        /// <summary>Rows written across every shard — the number the header reports and the hosted
        /// side reconciles against.</summary>
        public int Rows => _rows;

        /// <summary>Shard entry names, in order.</summary>
        public IReadOnlyList<string> Shards => _shards;

        public void WriteRow(JObject row)
        {
            if (_closed) throw new ObjectDisposedException(nameof(ExportJsonlWriter));
            var line = (row ?? new JObject()).ToString(Formatting.None);
            var bytes = Utf8NoBom.GetBytes(line);
            _buffer.Write(bytes, 0, bytes.Length);
            _buffer.WriteByte((byte)'\n');
            _bufferedRows++;
            _rows++;

            var caps = _owner.Caps;
            if (_bufferedRows >= caps.JsonlShardRows || _buffer.Length >= caps.JsonlShardBytes)
                FlushShard();
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            // Always emit shard 0, even with no rows: a facts file`s `shards` list is never empty, so the
            // hosted side's unzip-and-count path has one shape instead of two.
            if (_bufferedRows > 0 || _shards.Count == 0) FlushShard();
            _buffer.Dispose();
            _owner.JsonlClosed(this);
        }

        private void FlushShard()
        {
            var name = ExportFormat.ShardName(_baseName, _shardIndex);
            _owner.AddBytes(name, _buffer.ToArray());
            _shards.Add(name);
            _shardIndex++;
            _buffer.SetLength(0);
            _buffer.Position = 0;
            _bufferedRows = 0;
        }
    }
}
