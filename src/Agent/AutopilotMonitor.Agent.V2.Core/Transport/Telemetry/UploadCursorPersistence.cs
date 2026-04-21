#nullable enable
using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Atomarer Writer für <c>upload-cursor.json</c> (enthält nur <c>LastUploadedItemId</c>). Plan §2.7a.
    /// <para>
    /// Keine Checksum: bei JSON-Parse-Fehler wird der Cursor auf -1 zurückgesetzt — backend dedupliziert
    /// via (<c>PartitionKey</c>, <c>RowKey</c>), Doppel-Upload ist no-op (§2.7a Drain-Semantik).
    /// </para>
    /// </summary>
    public sealed class UploadCursorPersistence
    {
        private readonly string _path;
        private readonly string _tempPath;
        private readonly object _lock = new object();

        public UploadCursorPersistence(string path)
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

        public long Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_path)) return -1;
                try
                {
                    var json = File.ReadAllText(_path, Encoding.UTF8);
                    var dto = JsonConvert.DeserializeObject<CursorDto>(json);
                    return dto?.LastUploadedItemId ?? -1;
                }
                catch
                {
                    return -1;
                }
            }
        }

        public void Save(long lastUploadedItemId)
        {
            lock (_lock)
            {
                var dto = new CursorDto { LastUploadedItemId = lastUploadedItemId };
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

        private sealed class CursorDto
        {
            public long LastUploadedItemId { get; set; }
        }
    }
}
