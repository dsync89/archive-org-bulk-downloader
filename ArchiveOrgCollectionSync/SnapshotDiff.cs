namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public enum ChangeKind
    {
        /// <summary>New on archive.org.</summary>
        Added,

        /// <summary>Gone from archive.org.</summary>
        Removed,

        /// <summary>Same content under a new file name.</summary>
        Renamed,

        /// <summary>Same file name and content in another folder.</summary>
        Moved,

        /// <summary>Same name, different content (size or checksum).</summary>
        Changed,

        /// <summary>Same name and content; only archive.org's upload time moved.</summary>
        Touched
    }

    /// <summary>One difference between two file lists. <see cref="A"/> is the older side, <see cref="B"/> the newer.</summary>
    public sealed class DiffEntry
    {
        public DiffEntry(ChangeKind kind, File? a, File? b)
        {
            this.Kind = kind;
            this.A = a;
            this.B = b;
        }

        public ChangeKind Kind { get; }

        public File? A { get; }

        public File? B { get; }

        /// <summary>Gets the current name (the newer one for renames and moves, the old one for removals).</summary>
        public string Name => (this.B ?? this.A)!.Name;

        /// <summary>Gets the name in the older list (null for additions).</summary>
        public string? OldName => this.A?.Name;

        public string Glyph => SnapshotDiff.Glyph(this.Kind);
    }

    /// <summary>
    /// Compares two archive.org file lists (snapshots) and names what changed. Pure: no disk, no network, so it is
    /// unit-tested on its own.
    /// </summary>
    public static class SnapshotDiff
    {
        public static string Glyph(ChangeKind kind) => kind switch
        {
            ChangeKind.Added => "+",
            ChangeKind.Removed => "−",
            ChangeKind.Renamed => "~",
            ChangeKind.Moved => "⇄",
            ChangeKind.Changed => "*",
            _ => "·"
        };

        /// <summary>
        /// Files a diff looks at. archive.org's kept copies of replaced files (history/, old_version) feed the file
        /// history instead, and the item's own &lt;id&gt;_files.xml changes on every edit, so both are left out.
        /// With <paramref name="originalsOnly"/>, derivatives archive.org regenerates (thumbnails, _meta.xml…) are too.
        /// </summary>
        public static bool IsTracked(File file, string collection, bool originalsOnly)
        {
            if (file.OldVersion
                || file.Name.StartsWith("history/", StringComparison.OrdinalIgnoreCase)
                || file.Name.Equals($"{collection}_files.xml", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !originalsOnly || file.Source.Length == 0 || file.Source.Equals("original", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets a key for "same content": MD5 and size when there is an MD5, else SHA-1 and size; null when the list
        /// gives neither (such files can't be matched across names).
        /// </summary>
        public static string? ContentKey(File file)
        {
            return file.Md5.Length > 0 ? $"md5:{file.Md5.ToLowerInvariant()}:{file.Size}"
                : file.Sha1.Length > 0 ? $"sha1:{file.Sha1.ToLowerInvariant()}:{file.Size}"
                : null;
        }

        /// <summary>Gets a value indicating whether two entries for the same name hold different content.</summary>
        public static bool ContentDiffers(File a, File b)
        {
            if (a.SizeSpecified && b.SizeSpecified && a.Size != b.Size)
            {
                return true;
            }

            if (a.Md5.Length > 0 && b.Md5.Length > 0)
            {
                return !a.Md5.Equals(b.Md5, StringComparison.OrdinalIgnoreCase);
            }

            // One side (e.g. a snapshot rebuilt from an old session) has no MD5: fall back to SHA-1, then size.
            return a.Sha1.Length > 0 && b.Sha1.Length > 0 && !a.Sha1.Equals(b.Sha1, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Compares <paramref name="older"/> with <paramref name="newer"/>. Order of matching:
        /// <list type="number">
        /// <item>Same name: changed when the content differs, touched when only the upload time moved.</item>
        /// <item>Same content under another name: moved when only the folder changed, else renamed.</item>
        /// <item>Whatever is left is added or removed (a file renamed and changed at once shows as both).</item>
        /// </list>
        /// </summary>
        public static List<DiffEntry> Compare(IEnumerable<File> older, IEnumerable<File> newer)
        {
            var a = SnapshotDiff.ByName(older);
            var b = SnapshotDiff.ByName(newer);
            var result = new List<DiffEntry>();

            foreach (var (name, fa) in a)
            {
                if (!b.TryGetValue(name, out var fb))
                {
                    continue;
                }

                if (SnapshotDiff.ContentDiffers(fa, fb))
                {
                    result.Add(new DiffEntry(ChangeKind.Changed, fa, fb));
                }
                else if (fa.Mtime.Length > 0 && fb.Mtime.Length > 0 && fa.Mtime != fb.Mtime)
                {
                    result.Add(new DiffEntry(ChangeKind.Touched, fa, fb));
                }
            }

            var onlyA = a.Values.Where(f => !b.ContainsKey(f.Name)).ToList();
            var onlyB = b.Values.Where(f => !a.ContainsKey(f.Name)).ToList();

            // Same content, new name. Several identical files are paired in name order so the result is stable.
            var pool = onlyB
                .Where(f => SnapshotDiff.ContentKey(f) != null)
                .GroupBy(f => SnapshotDiff.ContentKey(f)!)
                .ToDictionary(g => g.Key, g => new Queue<File>(g.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)));
            var matched = new HashSet<File>();

            foreach (var fa in onlyA.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (SnapshotDiff.ContentKey(fa) is { } key && pool.TryGetValue(key, out var candidates) && candidates.Count > 0)
                {
                    // Prefer a candidate with the same file name (a move) over any other.
                    var fb = candidates.FirstOrDefault(c => SnapshotDiff.FileName(c.Name).Equals(SnapshotDiff.FileName(fa.Name), StringComparison.OrdinalIgnoreCase))
                        ?? candidates.Peek();
                    var rest = candidates.Where(c => c != fb).ToList();
                    pool[key] = new Queue<File>(rest);

                    bool moved = SnapshotDiff.FileName(fa.Name).Equals(SnapshotDiff.FileName(fb.Name), StringComparison.OrdinalIgnoreCase);
                    result.Add(new DiffEntry(moved ? ChangeKind.Moved : ChangeKind.Renamed, fa, fb));
                    matched.Add(fa);
                    matched.Add(fb);
                }
            }

            result.AddRange(onlyA.Where(f => !matched.Contains(f)).Select(f => new DiffEntry(ChangeKind.Removed, f, null)));
            result.AddRange(onlyB.Where(f => !matched.Contains(f)).Select(f => new DiffEntry(ChangeKind.Added, null, f)));

            return result
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Kind)
                .ToList();
        }

        /// <summary>Gets the folder part of an archive.org file name ("" for files at the item root).</summary>
        public static string Folder(string name)
        {
            int slash = name.LastIndexOf('/');
            return slash < 0 ? string.Empty : name[..(slash + 1)];
        }

        private static string FileName(string name)
        {
            int slash = name.LastIndexOf('/');
            return slash < 0 ? name : name[(slash + 1)..];
        }

        private static Dictionary<string, File> ByName(IEnumerable<File> files)
        {
            var map = new Dictionary<string, File>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                map.TryAdd(file.Name, file);
            }

            return map;
        }
    }
}
