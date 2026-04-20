#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Persistence
{
    /// <summary>
    /// File-backed <see cref="ISnapshotPersistence"/>. Plan §2.7.
    /// <para>
    /// On-disk format: a single JSON object with two properties:
    /// <c>{ "Checksum": "&lt;sha256-hex&gt;", "State": { … DecisionState … } }</c>
    /// </para>
    /// <para>
    /// Save is atomic: serialize → write <c>snapshot.json.tmp</c> → flush-to-disk → rename
    /// to <c>snapshot.json</c>. A crash mid-save leaves either the old snapshot intact or
    /// produces an orphan <c>.tmp</c> that's ignored on <see cref="Load"/>.
    /// </para>
    /// </summary>
    public sealed class SnapshotPersistence : ISnapshotPersistence
    {
        private readonly string _path;
        private readonly string _tempPath;
        private readonly string _quarantineRoot;
        private readonly object _lock = new object();
        private readonly Func<DateTime> _utcNow;

        public SnapshotPersistence(string path)
            : this(path, () => DateTime.UtcNow)
        {
        }

        /// <summary>Overload for deterministic tests — <paramref name="utcNow"/> stamps quarantine dirs.</summary>
        public SnapshotPersistence(string path, Func<DateTime> utcNow)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path is mandatory.", nameof(path));
            _path = path;
            _tempPath = path + ".tmp";
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));

            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
            {
                throw new ArgumentException("Snapshot path must have a directory component.", nameof(path));
            }

            _quarantineRoot = Path.Combine(dir, ".quarantine");

            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        public void Save(DecisionState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            lock (_lock)
            {
                var stateJson = StateSerializer.Serialize(state);
                var checksum = ComputeSha256Hex(stateJson);
                var envelopeJson = BuildEnvelope(checksum, stateJson);
                var bytes = Encoding.UTF8.GetBytes(envelopeJson);

                // Write to temp + flush, then atomic rename.
                using (var fs = new FileStream(
                    _tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    options: FileOptions.WriteThrough))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }

                // File.Replace is the atomic rename on Windows (preserves attributes);
                // fall back to Move if no target exists yet.
                if (File.Exists(_path))
                {
                    File.Replace(_tempPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(_tempPath, _path);
                }
            }
        }

        public DecisionState? Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_path)) return null;

                string envelopeJson;
                try
                {
                    envelopeJson = File.ReadAllText(_path, Encoding.UTF8);
                }
                catch
                {
                    return null;
                }

                if (!TryParseEnvelope(envelopeJson, out var declaredChecksum, out var stateJson))
                {
                    return null;
                }

                var actualChecksum = ComputeSha256Hex(stateJson);
                if (!string.Equals(declaredChecksum, actualChecksum, StringComparison.OrdinalIgnoreCase))
                {
                    // §2.7 Sonderfall 2: Checksum-Mismatch → null zurück. Caller entscheidet
                    // ob Quarantine + Replay.
                    return null;
                }

                try
                {
                    return StateSerializer.Deserialize(stateJson);
                }
                catch
                {
                    return null;
                }
            }
        }

        public void Quarantine(string reason)
        {
            if (reason == null) reason = string.Empty;

            lock (_lock)
            {
                if (!File.Exists(_path)) return;

                if (!Directory.Exists(_quarantineRoot))
                {
                    Directory.CreateDirectory(_quarantineRoot);
                }

                var stamp = _utcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'");
                var bucket = Path.Combine(_quarantineRoot, stamp);
                Directory.CreateDirectory(bucket);

                var snapshotName = Path.GetFileName(_path);
                File.Move(_path, Path.Combine(bucket, snapshotName));
                File.WriteAllText(Path.Combine(bucket, "reason.txt"), reason, Encoding.UTF8);
            }
        }

        private static string ComputeSha256Hex(string payload)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        private static string BuildEnvelope(string checksum, string stateJson)
        {
            // Minimal hand-rolled envelope — keeps the inner stateJson byte-identical to
            // what the checksum was computed over. Round-tripping through Newtonsoft at
            // this layer would reformat and break the checksum.
            var escapedChecksum = EscapeJsonString(checksum);
            return "{\"Checksum\":\"" + escapedChecksum + "\",\"State\":" + stateJson + "}";
        }

        private static bool TryParseEnvelope(string envelopeJson, out string checksum, out string stateJson)
        {
            checksum = string.Empty;
            stateJson = string.Empty;
            if (string.IsNullOrWhiteSpace(envelopeJson)) return false;

            const string checksumKey = "\"Checksum\":\"";
            var cIdx = envelopeJson.IndexOf(checksumKey, StringComparison.Ordinal);
            if (cIdx < 0) return false;
            var cStart = cIdx + checksumKey.Length;
            var cEnd = envelopeJson.IndexOf('"', cStart);
            if (cEnd < 0) return false;
            checksum = envelopeJson.Substring(cStart, cEnd - cStart);

            const string stateKey = "\"State\":";
            var sIdx = envelopeJson.IndexOf(stateKey, cEnd, StringComparison.Ordinal);
            if (sIdx < 0) return false;
            var sStart = sIdx + stateKey.Length;

            // Scan to the matching closing brace of the State object.
            while (sStart < envelopeJson.Length && char.IsWhiteSpace(envelopeJson[sStart])) sStart++;
            if (sStart >= envelopeJson.Length || envelopeJson[sStart] != '{') return false;

            var depth = 0;
            var inString = false;
            var escape = false;
            var sEnd = -1;
            for (var i = sStart; i < envelopeJson.Length; i++)
            {
                var ch = envelopeJson[i];
                if (escape) { escape = false; continue; }
                if (ch == '\\' && inString) { escape = true; continue; }
                if (ch == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (ch == '{') depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0) { sEnd = i; break; }
                }
            }

            if (sEnd < 0) return false;
            stateJson = envelopeJson.Substring(sStart, sEnd - sStart + 1);
            return true;
        }

        private static string EscapeJsonString(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < 0x20) sb.AppendFormat("\\u{0:x4}", (int)ch);
                else sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
