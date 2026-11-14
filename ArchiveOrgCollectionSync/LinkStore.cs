namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Xml.Serialization;

    /// <summary>One saved archive.org file list (links\&lt;link&gt;\snapshots\&lt;Id&gt;.xml.gz).</summary>
    public sealed class SnapshotInfo
    {
        public string Id { get; set; } = string.Empty;

        public DateTime Taken { get; set; }

        /// <summary>Gets or sets what made it: "sync", "scan", "start", "folder" (the &lt;id&gt;_files.xml found in the destination) or "session &lt;id&gt;".</summary>
        public string Source { get; set; } = string.Empty;

        public int FileCount { get; set; }

        public long TotalSize { get; set; }

        /// <summary>Gets or sets a hash of the listed names, sizes and checksums, so an unchanged list isn't saved twice.</summary>
        public string Hash { get; set; } = string.Empty;

        [JsonIgnore]
        public string WhenText => this.Taken.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>
    /// A download link: one archive.org item synced into one folder. Runs (sessions) and snapshots of the item's file
    /// list hang off it, so Sync can say what changed on archive.org since last time. Saved as
    /// links\&lt;Id&gt;\link.json; sessions stay in sessions\ and are matched by collection and folder.
    /// </summary>
    public sealed class LinkRecord
    {
        public string Id { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string Collection { get; set; } = string.Empty;

        public string Folder { get; set; } = string.Empty;

        public DateTime Created { get; set; }

        /// <summary>Gets or sets when Sync last asked archive.org.</summary>
        public DateTime? LastChecked { get; set; }

        /// <summary>Gets or sets archive.org's item_last_updated (Unix seconds) as of the last check that read the file list.</summary>
        public long? ItemLastUpdated { get; set; }

        public List<SnapshotInfo> Snapshots { get; set; } = new();

        /// <summary>Gets or sets the last "Check all for updates" result (archive.org date, missing folder or item).</summary>
        public LinkCheck? LastCheck { get; set; }

        /// <summary>Gets a value indicating whether link.json exists (links seen only in session history aren't saved yet).</summary>
        [JsonIgnore]
        public bool IsSaved { get; set; }

        [JsonIgnore]
        public SnapshotInfo? Latest => this.Snapshots.Count > 0 ? this.Snapshots[^1] : null;
    }

    public static class LinkStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static readonly XmlSerializer ListSerializer = new(typeof(FileCollection));

        private static readonly object SaveLock = new();

        /// <summary>Held while creating a link or adding a snapshot, so Sync and a Scan in the background don't race.</summary>
        public static readonly object Gate = new();

        public static string Folder { get; } = Path.Combine(Settings.AppDataFolder, "links");

        public static string KeyFor(string collection, string folder)
        {
            string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            string safe = string.Concat(collection.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return safe + "_" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..8];
        }

        public static LinkRecord? Find(string collection, string folder)
        {
            string path = LinkStore.RecordPath(LinkStore.KeyFor(collection, folder));

            try
            {
                var link = System.IO.File.Exists(path) ? JsonSerializer.Deserialize<LinkRecord>(System.IO.File.ReadAllText(path), LinkStore.JsonOptions) : null;

                if (link != null)
                {
                    link.IsSaved = true;
                }

                return link;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the link for this collection and folder, creating it on first use. A new link starts with snapshot #0
        /// built from what is already there: the &lt;id&gt;_files.xml in the destination folder when one was
        /// downloaded (full checksums), else the newest session's list (MD5 only). Reads the destination folder, so
        /// call it off the UI thread.
        /// </summary>
        public static LinkRecord GetOrCreate(string url, string collection, string folder, IReadOnlyList<SessionRecord> sessions)
        {
            // A saved link without snapshots (e.g. relocated before its first Sync) still gets its snapshot #0.
            var existing = LinkStore.Find(collection, folder);

            if (existing is { Snapshots.Count: > 0 })
            {
                return existing;
            }

            var link = existing ?? new LinkRecord
            {
                Id = LinkStore.KeyFor(collection, folder),
                Url = url,
                Collection = collection,
                Folder = folder,
                Created = DateTime.Now
            };

            string listPath = Path.Combine(folder, $"{collection}_files.xml");
            File[]? seed = null;

            try
            {
                if (System.IO.File.Exists(listPath))
                {
                    using var stream = System.IO.File.OpenRead(listPath);
                    seed = ((FileCollection?)LinkStore.ListSerializer.Deserialize(stream))?.Files;

                    if (seed != null)
                    {
                        LinkStore.AddSnapshot(link, seed, "folder", System.IO.File.GetLastWriteTime(listPath), onlyIfChanged: false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                seed = null;
            }

            if (seed == null && sessions.FirstOrDefault(s => s.Files.Count > 0) is { } newest)
            {
                LinkStore.AddSnapshot(link, newest.Files.Select(f => f.ToFile()).ToArray(), "session " + newest.Id, newest.Ended ?? newest.Started, onlyIfChanged: false);
            }

            LinkStore.Save(link);
            return link;
        }

        public static void Save(LinkRecord link)
        {
            lock (LinkStore.SaveLock)
            {
                try
                {
                    string path = LinkStore.RecordPath(link.Id);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    System.IO.File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(link, LinkStore.JsonOptions));
                    System.IO.File.Move(path + ".tmp", path, true);
                    link.IsSaved = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // History is best-effort; syncing must carry on.
                }
            }
        }

        /// <summary>Saves <paramref name="files"/> as a new snapshot unless it lists exactly what the latest one does.</summary>
        /// <returns>The new snapshot, or null when nothing changed (or it couldn't be written).</returns>
        public static SnapshotInfo? AddSnapshot(LinkRecord link, IReadOnlyCollection<File> files, string source, DateTime taken, bool onlyIfChanged = true)
        {
            string hash = LinkStore.HashOf(files);

            if (onlyIfChanged && link.Latest?.Hash == hash)
            {
                return null;
            }

            string id = taken.ToString("yyyyMMdd_HHmmss");

            for (int n = 2; link.Snapshots.Any(s => s.Id == id); n++)
            {
                id = $"{taken:yyyyMMdd_HHmmss}_{n}";
            }

            var info = new SnapshotInfo
            {
                Id = id,
                Taken = taken,
                Source = source,
                FileCount = files.Count,
                TotalSize = files.Sum(f => f.Size),
                Hash = hash
            };

            try
            {
                string path = LinkStore.SnapshotPath(link, info);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                using (var stream = new GZipStream(System.IO.File.Create(path + ".tmp"), CompressionLevel.SmallestSize))
                {
                    LinkStore.ListSerializer.Serialize(stream, new FileCollection { Files = files.ToArray() });
                }

                System.IO.File.Move(path + ".tmp", path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            link.Snapshots.Add(info);
            link.Snapshots.Sort((x, y) => x.Taken.CompareTo(y.Taken));
            return info;
        }

        public static File[] LoadSnapshot(LinkRecord link, SnapshotInfo info)
        {
            using var stream = new GZipStream(System.IO.File.OpenRead(LinkStore.SnapshotPath(link, info)), CompressionMode.Decompress);
            return ((FileCollection?)LinkStore.ListSerializer.Deserialize(stream))?.Files ?? Array.Empty<File>();
        }

        /// <summary>
        /// Gets every link: the saved ones plus one per (collection, folder) seen in session history that has no link
        /// yet (those are created on their first Sync). Newest activity first. No access to destination folders.
        /// </summary>
        public static List<LinkRecord> All(IReadOnlyList<SessionRecord> sessions)
        {
            var links = new Dictionary<string, LinkRecord>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(LinkStore.Folder))
            {
                foreach (string path in Directory.EnumerateFiles(LinkStore.Folder, "link.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<LinkRecord>(System.IO.File.ReadAllText(path), LinkStore.JsonOptions) is { } link)
                        {
                            link.IsSaved = true;
                            links[link.Id] = link;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                    {
                    }
                }
            }

            foreach (var session in sessions)
            {
                string key;

                try
                {
                    key = LinkStore.KeyFor(session.Collection, session.Folder);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (!links.ContainsKey(key))
                {
                    links[key] = new LinkRecord { Id = key, Url = session.Url, Collection = session.Collection, Folder = session.Folder, Created = session.Started };
                }
            }

            DateTime Activity(LinkRecord l) => new[]
            {
                l.LastChecked ?? DateTime.MinValue,
                l.Latest?.Taken ?? DateTime.MinValue,
                sessions.Where(s => LinkStore.KeyFor(s.Collection, s.Folder).Equals(l.Id, StringComparison.OrdinalIgnoreCase)).Select(s => s.Started).DefaultIfEmpty(l.Created).Max()
            }.Max();

            return links.Values.OrderByDescending(Activity).ToList();
        }

        /// <summary>
        /// Points a link at the folder its files were moved to: the link, its sessions and its verified ledger all
        /// follow, so history and verifications are kept. Nothing in the destination folders is touched.
        /// </summary>
        public static LinkRecord Relocate(LinkRecord link, string newFolder, IEnumerable<SessionRecord> sessions)
        {
            lock (LinkStore.Gate)
            {
                string oldFolder = link.Folder;
                string oldId = link.Id;
                string newId = LinkStore.KeyFor(link.Collection, newFolder);

                if (Directory.Exists(LinkStore.LinkFolder(oldId)) && !oldId.Equals(newId, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(LinkStore.LinkFolder(newId)))
                    {
                        throw new IOException($"There is already a link for {link.Collection} in {newFolder}.");
                    }

                    Directory.Move(LinkStore.LinkFolder(oldId), LinkStore.LinkFolder(newId));
                }

                link.Id = newId;
                link.Folder = newFolder;
                link.LastCheck = null;
                LinkStore.Save(link);

                foreach (var session in sessions)
                {
                    session.Folder = newFolder;
                    SessionStore.Save(session);
                }

                VerifiedLedger.Move(oldFolder, newFolder);
                return link;
            }
        }

        /// <summary>Deletes a link's record and snapshots (not its sessions, not any downloaded file).</summary>
        public static void Forget(LinkRecord link)
        {
            lock (LinkStore.Gate)
            {
                if (Directory.Exists(LinkStore.LinkFolder(link.Id)))
                {
                    Directory.Delete(LinkStore.LinkFolder(link.Id), true);
                }

                link.IsSaved = false;
            }
        }

        public static string LinkFolder(string id) => Path.Combine(LinkStore.Folder, id);

        private static string RecordPath(string id) => Path.Combine(LinkStore.LinkFolder(id), "link.json");

        private static string SnapshotPath(LinkRecord link, SnapshotInfo info) => Path.Combine(LinkStore.LinkFolder(link.Id), "snapshots", info.Id + ".xml.gz");

        private static string HashOf(IEnumerable<File> files)
        {
            var text = new StringBuilder();

            foreach (var f in files.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                text.Append(f.Name).Append('|').Append(f.Size).Append('|').Append(f.Md5.ToLowerInvariant()).Append('|')
                    .Append(f.Sha1.ToLowerInvariant()).Append('|').Append(f.Mtime).Append('|').Append(f.Source).Append('|')
                    .Append(f.OldVersion ? '1' : '0').Append('\n');
            }

            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
    }
}
