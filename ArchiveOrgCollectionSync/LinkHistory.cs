namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;

    /// <summary>The result of asking archive.org (and the disk) about one link, saved in link.json.</summary>
    public sealed class LinkCheck
    {
        public DateTime Checked { get; set; }

        /// <summary>Gets or sets archive.org's item_last_updated at the check (Unix seconds); null when it didn't answer.</summary>
        public long? RemoteUpdated { get; set; }

        public bool FolderMissing { get; set; }

        /// <summary>Gets or sets a value indicating whether archive.org has no such item (dark, deleted or renamed).</summary>
        public bool ItemMissing { get; set; }

        /// <summary>Gets or sets the error when archive.org couldn't be reached (the item may be fine).</summary>
        public string? Error { get; set; }
    }

    /// <summary>One distinct version of a file across snapshots, sessions and archive.org's own history/ copies.</summary>
    public sealed class FileVersion
    {
        public DateTime SortKey { get; set; }

        public string When { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public string SizeText { get; set; } = string.Empty;

        public string Md5 { get; set; } = string.Empty;

        public string Sha1 { get; set; } = string.Empty;

        /// <summary>Gets or sets the upload time on archive.org.</summary>
        public string Uploaded { get; set; } = string.Empty;

        public string Local { get; set; } = string.Empty;

        public string Note { get; set; } = string.Empty;

        /// <summary>Gets or sets the archive.org path of a kept copy (history/files/…), which can still be downloaded.</summary>
        public string? HistoryPath { get; set; }
    }

    /// <summary>Pure helpers for the Links tab: a link's status line and a file's version history.</summary>
    public static class LinkHistory
    {
        public static string UnixDate(string mtime) => long.TryParse(mtime, out long s) ? DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : string.Empty;

        public static string UnixDate(long? unix) => unix is { } s ? DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "?";

        /// <summary>
        /// Gets the glyph and text for a link in the Links list:
        /// ● up to date, ▲ changed since the last Sync, ? not synced yet, ✖ folder or item missing, ⚠ couldn't check.
        /// </summary>
        /// <param name="lastRun">When the newest run for this link ended; lets a link with no snapshot yet still say "changed since".</param>
        public static (string Glyph, string Text) Describe(LinkRecord link, DateTime? lastRun = null)
        {
            var check = link.LastCheck;
            string at = check != null ? $" (checked {check.Checked:MM-dd HH:mm})" : string.Empty;

            if (check?.FolderMissing == true)
            {
                return ("✖", "destination folder missing: Relocate… or Forget…");
            }

            if (check?.ItemMissing == true)
            {
                return ("✖", "item not found on archive.org" + at);
            }

            if (link.Latest is not { } latest)
            {
                if (check?.RemoteUpdated is { } r && lastRun is { } run && DateTimeOffset.FromUnixTimeSeconds(r).LocalDateTime > run)
                {
                    return ("▲", $"archive.org changed it {LinkHistory.UnixDate(r)}, after your last run ({run:yyyy-MM-dd}); ⟳ Sync shows what");
                }

                return ("?", "not synced yet" + (check?.RemoteUpdated is { } updated ? $"; archive.org updated {LinkHistory.UnixDate(updated)}" : string.Empty));
            }

            if (check?.Error is { } error)
            {
                return ("⚠", $"couldn't check: {error}");
            }

            if (check?.RemoteUpdated is not { } remote)
            {
                return ("·", $"last snapshot {latest.WhenText}; press Check all for updates");
            }

            // Synced: compare with the item date seen at that Sync. Only Start ran: with when the snapshot was taken.
            bool changed = link.ItemLastUpdated is { } seen
                ? remote > seen
                : DateTimeOffset.FromUnixTimeSeconds(remote).LocalDateTime > latest.Taken;

            return changed
                ? ("▲", $"archive.org changed it {LinkHistory.UnixDate(remote)}, after your last snapshot ({latest.WhenText})" + at)
                : ("●", "up to date" + at);
        }

        /// <summary>The archive.org name of the n-th kept copy of <paramref name="name"/> ("history/files/&lt;name&gt;.~n~").</summary>
        public static bool IsHistoryCopyOf(string historyName, string name, out int number)
        {
            number = 0;
            var match = Regex.Match(historyName, @"^history/files/(.+)\.~(\d+)~$", RegexOptions.IgnoreCase);
            return match.Success
                && match.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(match.Groups[2].Value, out number);
        }

        /// <summary>
        /// Builds the version history of one file: archive.org's kept copies first (they predate tracking), then one row
        /// per run of identical content across the snapshots, with the sessions that downloaded or verified it and
        /// whether the copy on disk is that version.
        /// </summary>
        /// <param name="localMd5">MD5 of the file on disk per the ledger, or null when unknown or missing.</param>
        public static List<FileVersion> Build(
            string name,
            IReadOnlyList<(SnapshotInfo Info, File[] Files)> snapshots,
            IEnumerable<SessionRecord> sessions,
            string? localMd5,
            bool localHashed)
        {
            var versions = new List<FileVersion>();
            string LocalFor(string md5) => localMd5 != null && md5.Length > 0 && md5.Equals(localMd5, StringComparison.OrdinalIgnoreCase)
                ? (localHashed ? "= on disk ✔" : "= on disk (size)")
                : string.Empty;

            // archive.org's kept copies of replaced versions, from any snapshot that listed them.
            var kept = snapshots.SelectMany(s => s.Files)
                .Where(f => LinkHistory.IsHistoryCopyOf(f.Name, name, out _))
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First());

            foreach (var copy in kept)
            {
                LinkHistory.IsHistoryCopyOf(copy.Name, name, out int n);
                versions.Add(new FileVersion
                {
                    SortKey = long.TryParse(copy.Mtime, out long t) ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime : DateTime.MinValue,
                    When = LinkHistory.UnixDate(copy.Mtime),
                    Source = $"archive.org kept copy ~{n}~",
                    SizeText = Format.Bytes(copy.Size),
                    Md5 = copy.Md5,
                    Sha1 = copy.Sha1,
                    Uploaded = LinkHistory.UnixDate(copy.Mtime),
                    Local = LocalFor(copy.Md5),
                    Note = "replaced before or during tracking; still downloadable",
                    HistoryPath = copy.Name
                });
            }

            versions = versions.OrderBy(v => v.SortKey).ToList();

            // Snapshots, oldest first; consecutive snapshots with the same content collapse into one row.
            FileVersion? current = null;
            string? currentKey = null;
            int runLength = 0;
            SnapshotInfo? first = null;

            foreach (var (info, files) in snapshots.OrderBy(s => s.Info.Taken))
            {
                var file = files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                string key = file == null ? "(absent)" : SnapshotDiff.ContentKey(file) ?? $"size:{file.Size}";

                if (key == currentKey && current != null)
                {
                    runLength++;
                    current.Source = $"snapshots {first!.WhenText} … {info.WhenText} ({runLength})";
                    continue;
                }

                currentKey = key;
                runLength = 1;
                first = info;
                current = file == null
                    ? new FileVersion { SortKey = info.Taken, When = info.WhenText, Source = $"snapshot {info.WhenText}", Note = "not listed (removed or renamed)" }
                    : new FileVersion
                    {
                        SortKey = info.Taken,
                        When = info.WhenText,
                        Source = $"snapshot {info.WhenText} ({info.Source})",
                        SizeText = Format.Bytes(file.Size),
                        Md5 = file.Md5,
                        Sha1 = file.Sha1,
                        Uploaded = LinkHistory.UnixDate(file.Mtime),
                        Local = LocalFor(file.Md5)
                    };
                versions.Add(current);
            }

            // Which runs fetched or verified each version.
            foreach (var session in sessions.OrderBy(s => s.Started))
            {
                var f = session.Files.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Status is FileStatus.Done or FileStatus.Skipped);
                var version = f == null ? null : versions.LastOrDefault(v => v.Md5.Length > 0 && v.Md5.Equals(f.Md5, StringComparison.OrdinalIgnoreCase));

                if (version != null)
                {
                    string what = $"{(f!.Status == FileStatus.Done ? "downloaded" : "verified")} {session.Started:MM-dd HH:mm}";
                    version.Note = version.Note.Length == 0 ? what : version.Note.Split(" · ").Length < 4 ? version.Note + " · " + what : version.Note;
                }
            }

            return versions;
        }
    }
}
