namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;

    public sealed class LedgerEntry
    {
        public long Size { get; set; }

        public string Md5 { get; set; } = string.Empty;

        public long LocalLength { get; set; }

        public DateTime LocalWriteUtc { get; set; }

        public DateTime VerifiedUtc { get; set; }

        /// <summary>Gets or sets a value indicating whether the file was hashed (MD5) rather than only size-checked.</summary>
        public bool Md5Verified { get; set; }
    }

    /// <summary>
    /// Remembers which files in a destination folder were already verified (JDownloader-style), so Start doesn't
    /// re-hash hundreds of GB every time. An entry is trusted only while archive.org still lists the same size/MD5 and
    /// the local file's length and last-write time are unchanged.
    /// Stored in %LocalAppData%\ArchiveOrgCollectionSync\verified\&lt;hash of folder path&gt;.json.
    /// </summary>
    public sealed class VerifiedLedger
    {
        private const int SaveEvery = 25;

        private static readonly string LedgerFolder = Path.Combine(Settings.AppDataFolder, "verified");

        private readonly object saveLock = new();
        private readonly string path;
        private int unsaved;

        private VerifiedLedger(string folder, string path)
        {
            this.Folder = folder;
            this.path = path;
        }

        public string Folder { get; private set; }

        public ConcurrentDictionary<string, LedgerEntry> Files { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        public int Count => this.Files.Count;

        public static VerifiedLedger Load(string folder)
        {
            string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..16];
            var ledger = new VerifiedLedger(full, Path.Combine(VerifiedLedger.LedgerFolder, key + ".json"));

            try
            {
                if (System.IO.File.Exists(ledger.path)
                    && JsonSerializer.Deserialize<Stored>(System.IO.File.ReadAllText(ledger.path)) is { } stored)
                {
                    foreach (var (name, entry) in stored.Files)
                    {
                        ledger.Files[name] = entry;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A lost ledger only means files are checked once more.
            }

            return ledger;
        }

        /// <summary>Returns true when the file at <paramref name="localPath"/> was verified before and nothing changed since.</summary>
        public bool IsVerified(FileItem item, string localPath, bool requireMd5)
        {
            if (!this.Files.TryGetValue(item.Name, out var entry)
                || entry.Size != item.Size
                || !string.Equals(entry.Md5, item.Md5, StringComparison.OrdinalIgnoreCase)
                || (requireMd5 && item.Md5.Length > 0 && !entry.Md5Verified))
            {
                return false;
            }

            var info = new FileInfo(localPath);
            return info.Exists && info.Length == entry.LocalLength && info.LastWriteTimeUtc == entry.LocalWriteUtc;
        }

        public void Record(FileItem item, string localPath, bool md5Verified)
        {
            var info = new FileInfo(localPath);

            if (!info.Exists)
            {
                return;
            }

            this.Files[item.Name] = new LedgerEntry
            {
                Size = item.Size,
                Md5 = item.Md5,
                LocalLength = info.Length,
                LocalWriteUtc = info.LastWriteTimeUtc,
                VerifiedUtc = DateTime.UtcNow,
                Md5Verified = md5Verified
            };

            if (Interlocked.Increment(ref this.unsaved) % VerifiedLedger.SaveEvery == 0)
            {
                this.Save();
            }
        }

        public void Remove(string name)
        {
            this.Files.TryRemove(name, out _);
        }

        public void Save()
        {
            lock (this.saveLock)
            {
                try
                {
                    Directory.CreateDirectory(VerifiedLedger.LedgerFolder);
                    var stored = new Stored { Folder = this.Folder, Files = new(this.Files, StringComparer.OrdinalIgnoreCase) };
                    System.IO.File.WriteAllText(this.path + ".tmp", JsonSerializer.Serialize(stored));
                    System.IO.File.Move(this.path + ".tmp", this.path, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort, like session history.
                }
            }
        }

        private sealed class Stored
        {
            public string Folder { get; set; } = string.Empty;

            public System.Collections.Generic.Dictionary<string, LedgerEntry> Files { get; set; } = new();
        }
    }
}
