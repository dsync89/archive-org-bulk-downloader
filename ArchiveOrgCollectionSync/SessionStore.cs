namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public enum SessionState
    {
        Running,
        Paused,
        Stopped,
        Completed
    }

    public sealed class SessionFile
    {
        public string Name { get; set; } = string.Empty;

        public long Size { get; set; }

        public bool HasSize { get; set; }

        public string Md5 { get; set; } = string.Empty;

        public FileStatus Status { get; set; }

        public string? Detail { get; set; }
    }

    /// <summary>One sync run (TeraCopy-style history entry), saved as sessions\&lt;Id&gt;.json next to &lt;Id&gt;.log.</summary>
    public sealed class SessionRecord
    {
        public string Id { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string Collection { get; set; } = string.Empty;

        public string Folder { get; set; } = string.Empty;

        public SyncOptions Options { get; set; } = new();

        public DateTime Started { get; set; }

        public DateTime? Ended { get; set; }

        public SessionState State { get; set; }

        public double ActiveSeconds { get; set; }

        public long BytesDownloaded { get; set; }

        public List<SessionFile> Files { get; set; } = new();

        [JsonIgnore]
        public int FailedCount => this.Files.Count(f => f.Status == FileStatus.Failed);

        [JsonIgnore]
        public int FinishedCount => this.Files.Count(f => f.Status is FileStatus.Done or FileStatus.Skipped);

        [JsonIgnore]
        public string WhenText => this.Started.ToString("yyyy-MM-dd HH:mm");

        [JsonIgnore]
        public string FilesText => this.FailedCount > 0
            ? $"{this.FinishedCount} / {this.Files.Count} ({this.FailedCount} ✖)"
            : $"{this.FinishedCount} / {this.Files.Count}";

        [JsonIgnore]
        public string SizeText => Format.Bytes(this.Files.Sum(f => f.Size));

        [JsonIgnore]
        public string DownloadedText => Format.Bytes(this.BytesDownloaded);

        [JsonIgnore]
        public string SpeedText => this.ActiveSeconds > 0 ? Format.Speed(this.BytesDownloaded / this.ActiveSeconds) : string.Empty;

        [JsonIgnore]
        public string ResultText => this.State switch
        {
            SessionState.Running => "⬇ running",
            SessionState.Paused => "⏸ paused",
            SessionState.Stopped => "■ stopped",
            _ when this.FailedCount > 0 => $"✖ {this.FailedCount} failed",
            _ => "✔ complete"
        };

        public void Capture(IEnumerable<FileItem> items)
        {
            this.Files = items.Select(i => new SessionFile
            {
                Name = i.Name,
                Size = i.Size,
                HasSize = i.HasSize,
                Md5 = i.Md5,
                Status = i.CurrentStatus,
                Detail = i.CurrentDetail
            }).ToList();
        }

        public List<FileItem> Restore()
        {
            return this.Files.Select(f =>
            {
                var item = new FileItem(new File { Name = f.Name, Size = f.Size, SizeSpecified = f.HasSize, Md5 = f.Md5 });

                // Anything that was mid-flight when the session was saved goes back to the queue.
                var status = f.Status is FileStatus.Checking or FileStatus.Downloading or FileStatus.Verifying ? FileStatus.Queued : f.Status;
                item.Restore(status, status == FileStatus.Queued && f.Status != FileStatus.Queued ? "paused" : f.Detail);
                return item;
            }).ToList();
        }
    }

    public static class SessionStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

        public static string Folder { get; } = Path.Combine(Settings.AppDataFolder, "sessions");

        public static SessionRecord Create(string url, string collection, string folder, SyncOptions options)
        {
            string safe = string.Concat(collection.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

            return new SessionRecord
            {
                Id = $"{DateTime.Now:yyyyMMdd_HHmmss}_{safe}",
                Url = url,
                Collection = collection,
                Folder = folder,
                Options = options,
                Started = DateTime.Now,
                State = SessionState.Running
            };
        }

        public static string LogPath(SessionRecord record) => Path.Combine(SessionStore.Folder, record.Id + ".log");

        public static void Save(SessionRecord record)
        {
            try
            {
                Directory.CreateDirectory(SessionStore.Folder);
                string path = Path.Combine(SessionStore.Folder, record.Id + ".json");
                System.IO.File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(record, SessionStore.JsonOptions));
                System.IO.File.Move(path + ".tmp", path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // History is best-effort; the sync itself must carry on.
            }
        }

        public static List<SessionRecord> LoadAll()
        {
            if (!Directory.Exists(SessionStore.Folder))
            {
                return new List<SessionRecord>();
            }

            var records = new List<SessionRecord>();

            foreach (string path in Directory.EnumerateFiles(SessionStore.Folder, "*.json"))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<SessionRecord>(System.IO.File.ReadAllText(path), SessionStore.JsonOptions);

                    if (record != null)
                    {
                        records.Add(record);
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    // Skip unreadable history files rather than hiding all of them.
                }
            }

            return records.OrderByDescending(r => r.Started).ToList();
        }

        public static void Delete(SessionRecord record)
        {
            System.IO.File.Delete(Path.Combine(SessionStore.Folder, record.Id + ".json"));
            System.IO.File.Delete(SessionStore.LogPath(record));
        }

        public static void ExportCsv(SessionRecord record, string path)
        {
            var csv = new StringBuilder("Name,Size,Status,Detail\r\n");

            foreach (var f in record.Files)
            {
                csv.Append(SessionStore.Csv(f.Name)).Append(',').Append(f.Size).Append(',')
                    .Append(f.Status).Append(',').Append(SessionStore.Csv(f.Detail ?? string.Empty)).Append("\r\n");
            }

            System.IO.File.WriteAllText(path, csv.ToString(), Encoding.UTF8);
        }

        private static string Csv(string value)
        {
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
