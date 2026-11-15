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

        /// <summary>Gets or sets the SHA-1 archive.org listed for this content (empty in ledgers written before 1.5).</summary>
        public string Sha1 { get; set; } = string.Empty;

        /// <summary>Gets or sets the CRC32 archive.org listed for this content (empty in ledgers written before 1.5).</summary>
        public string Crc32 { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets where the proof came from: "download", "check" (an existing file was checked), "hash" (Sync's
        /// Hash button) or "session &lt;id&gt;" (taken over from an earlier session by Sync's baseline).
        /// </summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets archive.org's MD5 of a version proven to hold exactly the same files as this one (a re-zip): your
        /// copy keeps its own <see cref="Md5"/> but counts as up to date while archive.org lists this MD5.
        /// </summary>
        public string EquivalentMd5 { get; set; } = string.Empty;

        /// <summary>Gets or sets the size archive.org lists for <see cref="EquivalentMd5"/>.</summary>
        public long EquivalentSize { get; set; }

        /// <summary>Gets or sets why the copy counts as equivalent (the repack proof's note).</summary>
        public string ProofNote { get; set; } = string.Empty;

        /// <summary>Returns true when this entry is the listed version, or was proven to hold the same files.</summary>
        public bool Covers(string md5, long size, bool sizeKnown = true)
        {
            bool Same(string m, long s) => m.Length > 0 && m.Equals(md5, StringComparison.OrdinalIgnoreCase) && (!sizeKnown || s == size);

            return Same(this.Md5, this.Size) || Same(this.EquivalentMd5, this.EquivalentSize);
        }

        /// <summary>Gets a value indicating whether this entry counts as up to date only through a repack proof.</summary>
        public bool IsRepackFor(string md5) => this.EquivalentMd5.Length > 0 && this.EquivalentMd5.Equals(md5, StringComparison.OrdinalIgnoreCase);
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

        /// <summary>Moves a folder's ledger to another folder (after its files were moved there). Entries already there win.</summary>
        public static void Move(string oldFolder, string newFolder)
        {
            var from = VerifiedLedger.Load(oldFolder);

            if (from.Count == 0)
            {
                return;
            }

            var to = VerifiedLedger.Load(newFolder);

            foreach (var (name, entry) in from.Files)
            {
                to.Files.TryAdd(name, entry);
            }

            to.Save();

            try
            {
                System.IO.File.Delete(from.path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

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
                || !(entry.Covers(item.Md5, item.Size) || (item.Md5.Length == 0 && entry.Md5.Length == 0 && entry.Size == item.Size))
                || (requireMd5 && item.Md5.Length > 0 && !entry.Md5Verified))
            {
                return false;
            }

            var info = new FileInfo(localPath);
            return info.Exists && info.Length == entry.LocalLength && info.LastWriteTimeUtc == entry.LocalWriteUtc;
        }

        /// <summary>
        /// Returns the entry for <paramref name="name"/> when the file on disk still has the length and write time it had
        /// when it was verified, whatever archive.org lists now. Sync uses it to tell which version you have.
        /// </summary>
        public LedgerEntry? Current(string name, FileInfo local)
        {
            return this.Files.TryGetValue(name, out var entry)
                && local.Exists
                && local.Length == entry.LocalLength
                && local.LastWriteTimeUtc == entry.LocalWriteUtc
                ? entry
                : null;
        }

        public void Put(string name, LedgerEntry entry)
        {
            this.Files[name] = entry;
        }

        /// <summary>Moves an entry to a new name after Sync renamed the file on disk (a rename keeps the write time).</summary>
        public void Rename(string oldName, string newName)
        {
            if (this.Files.TryRemove(oldName, out var entry))
            {
                this.Files[newName] = entry;
            }
        }

        public void Record(FileItem item, string localPath, bool md5Verified, string source = "download")
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
                Md5Verified = md5Verified,
                Sha1 = item.Sha1,
                Crc32 = item.Crc32,
                Source = source
            };

            if (Interlocked.Increment(ref this.unsaved) % VerifiedLedger.SaveEvery == 0)
            {
                this.Save();
            }
        }

        /// <summary>Gets the entry when it vouches for <paramref name="item"/> only through a repack proof (else null).</summary>
        public LedgerEntry? RepackEntry(FileItem item)
        {
            return this.Files.TryGetValue(item.Name, out var entry) && entry.IsRepackFor(item.Md5) && entry.EquivalentSize == item.Size ? entry : null;
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
