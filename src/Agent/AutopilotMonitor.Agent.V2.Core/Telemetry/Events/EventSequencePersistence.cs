#nullable enable
using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Telemetry.Events
{
    /// <summary>
    /// Atomarer Writer für <c>event-sequence.json</c> (enthält nur <c>LastAssignedSequence</c>).
    /// Plan §2.2 / §4.x M4.4-Adjustments („analog <see cref="Transport.Telemetry.UploadCursorPersistence"/>").
    /// <para>
    /// Keine Checksum: bei Parse-Fehler wird der Counter auf <c>0</c> zurückgesetzt.
    /// Konsequenz: im pathologischen Fall (komplett korruptes File) kann dieselbe
    /// Sequence-Nummer nach Crash erneut vergeben werden. Backend-Ingest dedupliziert via
    /// <c>(PartitionKey, RowKey)</c> — Doppel-Upload ist no-op (§2.7a Drain-Semantik). Die
    /// RowKey-Dimension enthält Event.Sequence, daher bleibt der Effekt auf „doppeltes
    /// Event wird einmal persistiert" begrenzt.
    /// </para>
    /// </summary>
    public sealed class EventSequencePersistence
    {
        private readonly string _path;
        private readonly string _tempPath;
        private readonly object _lock = new object();

        public EventSequencePersistence(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path is mandatory.", nameof(path));
            _path = path;
            _tempPath = path + ".tmp";

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        /// <summary>Lädt den letzten persisted Sequence-Wert. <c>0</c> bei leerem / korruptem File.</summary>
        public long Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_path)) return 0;
                try
                {
                    var json = File.ReadAllText(_path, Encoding.UTF8);
                    var dto = JsonConvert.DeserializeObject<SequenceDto>(json);
                    return dto?.LastAssignedSequence ?? 0;
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// Atomarer Write: temp-File → <see cref="File.Replace(string,string,string?)"/>.
        /// <see cref="FileOptions.WriteThrough"/> + explicit Flush-to-disk (§2.7 L.12).
        /// </summary>
        public void Save(long lastAssignedSequence)
        {
            lock (_lock)
            {
                var dto = new SequenceDto { LastAssignedSequence = lastAssignedSequence };
                var json = JsonConvert.SerializeObject(dto);
                var bytes = Encoding.UTF8.GetBytes(json);

                using (var fs = new FileStream(
                    _tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 512,
                    options: FileOptions.WriteThrough))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }

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

        private sealed class SequenceDto
        {
            public long LastAssignedSequence { get; set; }
        }
    }
}
