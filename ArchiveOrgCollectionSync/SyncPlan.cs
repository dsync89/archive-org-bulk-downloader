namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using System.Threading;

    /// <summary>What the destination folder holds for one diff row, judged without reading file contents.</summary>
    public enum LocalState
    {
        /// <summary>No file at that name.</summary>
        Missing,

        /// <summary>The older version (A).</summary>
        MatchesA,

        /// <summary>The newer version (B): nothing to do.</summary>
        MatchesB,

        /// <summary>Neither version (a different size, or a hash that matches neither).</summary>
        Differs,

        /// <summary>A file is there but was never verified, or changed since: only a hash can tell.</summary>
        Unknown
    }

    public enum SyncAction
    {
        None,

        /// <summary>Fetch the file (it isn't on disk, or the old copy is moved away first).</summary>
        Download,

        /// <summary>Rename the local file to the new name; no download.</summary>
        Rename,

        /// <summary>Move the local file to _removed\ (removed from archive.org).</summary>
        MoveAway,

        /// <summary>Move the old copy to _removed\, then download the new version.</summary>
        Replace,

        /// <summary>Unknown local copy: the download step checks it and replaces it only if it differs.</summary>
        Check
    }

    /// <summary>Counts from the baseline pass (stat only), shown above the diff.</summary>
    public sealed class BaselineResult
    {
        public int Trusted { get; set; }

        public int Adopted { get; set; }

        public int NeedHash { get; set; }

        public long NeedHashBytes { get; set; }

        public int Different { get; set; }

        public int Missing { get; set; }

        public List<string> NeedHashNames { get; } = new();
    }

    /// <summary>What <see cref="SyncPlan.Classify"/> found for one row (computed off the UI thread).</summary>
    public readonly record struct Classification(LocalState State, bool Hashed, bool TargetExists, bool Repack, string? RepackNote);

    /// <summary>A planned disk move, also written to _removed\&lt;stamp&gt;\undo.json.</summary>
    public sealed class MoveRecord
    {
        public string From { get; set; } = string.Empty;

        public string To { get; set; } = string.Empty;
    }

    /// <summary>One row of the Sync dialog: a diff entry, what's on disk, and what applying it would do.</summary>
    public sealed class SyncRow : INotifyPropertyChanged
    {
        private bool isChecked;
        private LocalState local;
        private bool localHashed;
        private bool targetExists;
        private bool repack;
        private ProofResult? proof;

        /// <param name="wanted">Whether the file was ticked in the last session (new files: whether everything was).</param>
        public SyncRow(DiffEntry entry, bool wanted)
        {
            this.Entry = entry;
            this.Wanted = wanted;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public DiffEntry Entry { get; }

        /// <summary>Gets a value indicating whether you want this file at all (it was ticked); missing unwanted files stay missing.</summary>
        public bool Wanted { get; }

        public ChangeKind Kind => this.Entry.Kind;

        public string Glyph => this.Entry.Glyph;

        public string Name => this.Entry.Name;

        public string Folder => SnapshotDiff.Folder(this.Entry.Name);

        public string NameText => this.Entry.Kind is ChangeKind.Renamed or ChangeKind.Moved ? $"{this.Entry.Name}\n   was: {this.Entry.OldName}" : this.Entry.Name;

        public string AText => SyncRow.Describe(this.Entry.A, this.Entry.B);

        public string BText => this.Entry.B == null ? "— gone from item" : SyncRow.Describe(this.Entry.B, this.Entry.A);

        public bool IsChecked
        {
            get => this.isChecked;
            set
            {
                if (this.isChecked != value && (this.IsActionable || !value))
                {
                    this.isChecked = value;
                    this.OnPropertyChanged();
                    this.OnPropertyChanged(nameof(this.ActionText));
                }
            }
        }

        /// <summary>Gets a value indicating whether ticking the row would change anything on disk.</summary>
        public bool IsActionable => this.Action != SyncAction.None;

        public LocalState Local
        {
            get => this.local;
            set
            {
                this.local = value;
                this.Refresh();
            }
        }

        /// <summary>Gets or sets a value indicating whether <see cref="Local"/> rests on an MD5 rather than only a size.</summary>
        public bool LocalHashed
        {
            get => this.localHashed;
            set
            {
                this.localHashed = value;
                this.Refresh();
            }
        }

        /// <summary>Gets or sets a value indicating whether a file already sits at the new name (renames and moves).</summary>
        public bool TargetExists
        {
            get => this.targetExists;
            set
            {
                this.targetExists = value;
                this.Refresh();
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether your copy counts as B only because it was proven to hold the same files
        /// (archive.org re-zipped it). Such rows can still be ticked to fetch archive.org's exact bytes.
        /// </summary>
        public bool Repack
        {
            get => this.repack;
            set
            {
                this.repack = value;
                this.Refresh();
            }
        }

        /// <summary>Gets or sets what the repack proof found for this row (null = not checked).</summary>
        public ProofResult? Proof
        {
            get => this.proof;
            set
            {
                this.proof = value;
                this.OnPropertyChanged(nameof(this.ProofText));
                this.OnPropertyChanged(nameof(this.ProofNote));
            }
        }

        /// <summary>Gets the Inside column: ≡ same files (proven), ≠ contents changed, ? couldn't prove.</summary>
        public string ProofText => this.repack ? "≡" : this.proof?.Verdict switch
        {
            ProofVerdict.Same => "≡",
            ProofVerdict.Changed => "≠",
            ProofVerdict.NotProven => "?",
            _ => string.Empty
        };

        public string? ProofNote => this.proof?.Note ?? (this.repack ? this.RepackNote : null);

        /// <summary>Gets or sets the stored proof note for rows proven in an earlier Sync.</summary>
        public string? RepackNote { get; set; }

        public string LocalText => this.local switch
        {
            LocalState.Missing => "—",
            LocalState.MatchesA => this.localHashed ? "= A ✔" : "= A (size)",
            LocalState.MatchesB when this.repack => "≡ B (repack)",
            LocalState.MatchesB => this.localHashed ? "= B ✔" : "= B (size)",
            LocalState.Differs => "≠ A,B",
            _ => "? unknown"
        };

        /// <summary>Gets what ticking the row does. A proven re-zip can still be replaced with archive.org's exact bytes.</summary>
        public SyncAction Action => this.repack && this.local == LocalState.MatchesB && this.Kind != ChangeKind.Removed
            ? SyncAction.Replace
            : SyncRow.Plan(this.Entry.Kind, this.local, this.targetExists, this.Wanted);

        public string ActionText => !this.IsActionable ? this.NoActionText
            : !this.isChecked ? (this.repack ? "keep yours: same files inside (proven)" : "leave as is")
            : this.Action switch
            {
                SyncAction.Download => $"download {Format.Bytes(this.Entry.B!.Size)}",
                SyncAction.Rename => "rename on disk (0 B download)",
                SyncAction.MoveAway => "move to _removed\\",
                SyncAction.Replace => $"old → _removed\\, download {Format.Bytes(this.Entry.B!.Size)}",
                SyncAction.Check => "check at download, replace if different",
                _ => string.Empty
            };

        /// <summary>Gets a warning for the confirm step, or null.</summary>
        public string? Warning => !this.isChecked ? null
            : this.local == LocalState.Differs && this.Kind != ChangeKind.Added ? "your copy matches neither archive.org version"
            : this.Kind == ChangeKind.Removed ? "removed from archive.org; you may not get it back"
            : this.targetExists ? "a file already exists at the new name"
            : this.Action == SyncAction.Check ? "unknown local copy: hash it first to be sure"
            : null;

        public bool HasWarning => this.Warning != null;

        public long DownloadBytes => this.isChecked && this.Action is SyncAction.Download or SyncAction.Replace ? this.Entry.B!.Size : 0;

        private string NoActionText => this.Kind == ChangeKind.Touched ? "nothing (upload time only)"
            : this.local == LocalState.MatchesB ? "already up to date"
            : this.local == LocalState.Missing ? "nothing (not on disk, not ticked)"
            : "nothing";

        /// <summary>
        /// Decides what a ticked row does. Unknown copies are never moved away blind: the download step checks them.
        /// </summary>
        public static SyncAction Plan(ChangeKind kind, LocalState local, bool targetExists, bool wanted)
        {
            var fetch = wanted ? SyncAction.Download : SyncAction.None;

            return kind switch
            {
                ChangeKind.Added => local switch
                {
                    LocalState.MatchesB => SyncAction.None,
                    LocalState.Missing => fetch,
                    LocalState.Differs => SyncAction.Replace,
                    _ => SyncAction.Check
                },
                ChangeKind.Removed => local == LocalState.Missing ? SyncAction.None : SyncAction.MoveAway,
                ChangeKind.Renamed or ChangeKind.Moved => targetExists
                    ? (local == LocalState.Missing ? SyncAction.None : SyncAction.MoveAway)
                    : local switch
                    {
                        LocalState.Missing => fetch,
                        LocalState.Differs => SyncAction.Replace,
                        _ => SyncAction.Rename
                    },
                ChangeKind.Changed => local switch
                {
                    LocalState.MatchesB => SyncAction.None,
                    LocalState.Missing => fetch,
                    LocalState.Unknown => SyncAction.Check,
                    _ => SyncAction.Replace
                },
                _ => SyncAction.None
            };
        }

        public void Refresh()
        {
            if (!this.IsActionable && this.isChecked)
            {
                this.isChecked = false;
                this.OnPropertyChanged(nameof(this.IsChecked));
            }

            this.OnPropertyChanged(nameof(this.Local));
            this.OnPropertyChanged(nameof(this.LocalText));
            this.OnPropertyChanged(nameof(this.ProofText));
            this.OnPropertyChanged(nameof(this.ProofNote));
            this.OnPropertyChanged(nameof(this.Action));
            this.OnPropertyChanged(nameof(this.ActionText));
            this.OnPropertyChanged(nameof(this.IsActionable));
            this.OnPropertyChanged(nameof(this.Warning));
            this.OnPropertyChanged(nameof(this.HasWarning));
        }

        private static string Describe(File? file, File? other)
        {
            if (file == null)
            {
                return "—";
            }

            string hash = file.Md5.Length >= 4 ? "md5 " + file.Md5[..4] + "…" : file.Sha1.Length >= 4 ? "sha1 " + file.Sha1[..4] + "…" : string.Empty;
            string text = $"{Format.Bytes(file.Size)}  {hash}";

            if (file.Mtime.Length > 0 && long.TryParse(file.Mtime, out long seconds) && (other == null || other.Mtime != file.Mtime))
            {
                text += "\n" + DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("yyyy-MM-dd");
            }

            return text;
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// The disk side of Sync: the baseline pass that adopts verifications from earlier sessions into the ledger, the
    /// Local column, and applying the ticked rows (renames and moves into _removed\). Runs on a background thread.
    /// </summary>
    public static class SyncPlan
    {
        /// <summary>SMB servers stamp write times with their own clock; allow this much skew when comparing.</summary>
        private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

        public static string LocalPath(string folder, string name) => Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>
        /// Stats every listed file once and, where the ledger has no current entry, takes over the proof from the newest
        /// session that finished the file: same size, and not written after that session ended. Old sessions ran with
        /// MD5 verification, so those files count as hashed without hashing them again.
        /// </summary>
        public static BaselineResult Baseline(
            string folder,
            VerifiedLedger ledger,
            IReadOnlyList<SessionRecord> sessions,
            IReadOnlyCollection<File> live,
            IProgress<(int Done, int Total, string Name)>? progress,
            CancellationToken ct)
        {
            var result = new BaselineResult();
            var evidence = new Dictionary<string, List<(SessionFile File, SessionRecord Session, DateTime EndUtc)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var session in sessions.OrderByDescending(s => s.Started))
            {
                var end = (session.Ended ?? SyncPlan.SavedTime(session) ?? session.Started).ToUniversalTime();

                foreach (var f in session.Files.Where(f => f.Status is FileStatus.Done or FileStatus.Skipped))
                {
                    if (!evidence.TryGetValue(f.Name, out var list))
                    {
                        evidence[f.Name] = list = new();
                    }

                    list.Add((f, session, end));
                }
            }

            int done = 0;

            foreach (var file in live)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((++done, live.Count, file.Name));
                var info = new FileInfo(SyncPlan.LocalPath(folder, file.Name));

                if (!info.Exists)
                {
                    result.Missing++;
                    continue;
                }

                if (ledger.Current(file.Name, info) is { } entry)
                {
                    // Fill in the checksums the ledger lacked (written before 1.5) when it's the same content.
                    if (entry.Sha1.Length == 0 && entry.Md5.Equals(file.Md5, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.Sha1 = file.Sha1;
                        entry.Crc32 = file.Crc32;
                    }

                    result.Trusted++;
                    continue;
                }

                var proof = evidence.TryGetValue(file.Name, out var candidates)
                    ? candidates.FirstOrDefault(c => c.File.HasSize && c.File.Size == info.Length && info.LastWriteTimeUtc <= c.EndUtc + SyncPlan.ClockSkew)
                    : default;

                if (proof.Session != null)
                {
                    bool sameContent = proof.File.Md5.Equals(file.Md5, StringComparison.OrdinalIgnoreCase);
                    ledger.Put(file.Name, new LedgerEntry
                    {
                        Size = proof.File.Size,
                        Md5 = proof.File.Md5,
                        Sha1 = sameContent ? file.Sha1 : proof.File.Sha1,
                        Crc32 = sameContent ? file.Crc32 : proof.File.Crc32,
                        LocalLength = info.Length,
                        LocalWriteUtc = info.LastWriteTimeUtc,
                        VerifiedUtc = proof.EndUtc,
                        Md5Verified = proof.Session.Options.Verify == VerifyMode.Md5 && proof.File.Md5.Length > 0,
                        Source = "session " + proof.Session.Id
                    });
                    result.Adopted++;
                    result.Trusted++;
                }
                else if (file.SizeSpecified && info.Length != file.Size)
                {
                    result.Different++;
                }
                else
                {
                    result.NeedHash++;
                    result.NeedHashBytes += info.Length;
                    result.NeedHashNames.Add(file.Name);
                }
            }

            ledger.Save();
            return result;
        }

        /// <summary>
        /// Works out what's on disk for every row from a stat and the ledger. Safe on any thread: it only reads, and
        /// <see cref="ApplyClassification"/> sets the (bound) rows afterwards on the UI thread.
        /// </summary>
        public static List<Classification> Classify(string folder, VerifiedLedger ledger, IReadOnlyList<SyncRow> rows, CancellationToken ct)
        {
            var result = new List<Classification>(rows.Count);

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                string name = row.Entry.OldName ?? row.Entry.Name;
                var (state, hashed) = SyncPlan.Judge(folder, ledger, name, row.Entry.A, row.Entry.B);
                bool target = row.Kind is ChangeKind.Renamed or ChangeKind.Moved
                    && System.IO.File.Exists(SyncPlan.LocalPath(folder, row.Entry.Name));
                var entry = state == LocalState.MatchesB && row.Entry.B is { } b && ledger.Files.TryGetValue(name, out var e) && e.IsRepackFor(b.Md5) && !e.Md5.Equals(b.Md5, StringComparison.OrdinalIgnoreCase)
                    ? e
                    : null;
                result.Add(new Classification(state, hashed, target, entry != null, entry?.ProofNote));
            }

            return result;
        }

        public static void ApplyClassification(IReadOnlyList<SyncRow> rows, List<Classification> results)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].LocalHashed = results[i].Hashed;
                rows[i].TargetExists = results[i].TargetExists;
                rows[i].RepackNote = results[i].RepackNote;
                rows[i].Repack = results[i].Repack;
                rows[i].Local = results[i].State;
            }
        }

        /// <summary>
        /// Gets the rows worth a repack proof: a changed zip you have on disk in some other version. With
        /// <paramref name="keepRepacks"/> off (strict), none.
        /// </summary>
        public static List<SyncRow> ProofCandidates(IEnumerable<SyncRow> rows, bool keepRepacks)
        {
            return keepRepacks
                ? rows.Where(r => r.Kind == ChangeKind.Changed && !r.Repack && r.Entry.B is { } b && b.Md5.Length > 0
                    && b.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && r.Local is LocalState.MatchesA or LocalState.Differs or LocalState.Unknown).ToList()
                : new List<SyncRow>();
        }

        /// <summary>
        /// Proves one row against archive.org's zip index and, when the files inside are the same, records your copy as
        /// equivalent to archive.org's new version in the ledger. Reads your file; never writes it.
        /// </summary>
        public static ProofResult Prove(
            string collection,
            string folder,
            VerifiedLedger ledger,
            SyncRow row,
            Func<string, CancellationToken, Stream> openRemote,
            Action<long>? bytesRead,
            CancellationToken ct)
        {
            var b = row.Entry.B!;
            string name = row.Entry.OldName ?? row.Entry.Name;
            string path = SyncPlan.LocalPath(folder, name);
            var before = new FileInfo(path);
            ProofResult result;

            try
            {
                List<ZipEntryInfo> remote;

                using (var stream = openRemote(b.Name, ct))
                {
                    remote = ZipIndex.ReadCentral(stream);
                }

                using var local = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                result = RepackProof.Prove(local, remote, bytesRead, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or NotSupportedException)
            {
                return new ProofResult(ProofVerdict.NotProven, "couldn't prove: " + ex.Message);
            }

            if (result.Verdict != ProofVerdict.Same)
            {
                return result;
            }

            var after = new FileInfo(path);

            if (!after.Exists || after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc)
            {
                return new ProofResult(ProofVerdict.NotProven, "couldn't prove: your file changed while it was being read");
            }

            // Your copy's own MD5: from the ledger when it vouches for this exact file, else hashed now.
            var known = ledger.Current(name, after);
            string md5 = known is { Md5Verified: true, Md5.Length: > 0 }
                ? known.Md5
                : DownloadEngine.HashMd5Async(path, null, ct).GetAwaiter().GetResult();

            if (!new FileInfo(path).LastWriteTimeUtc.Equals(after.LastWriteTimeUtc))
            {
                return new ProofResult(ProofVerdict.NotProven, "couldn't prove: your file changed while it was being read");
            }

            ledger.Put(name, new LedgerEntry
            {
                Size = after.Length,
                Md5 = md5,
                Sha1 = known?.Sha1 ?? string.Empty,
                Crc32 = known?.Crc32 ?? string.Empty,
                LocalLength = after.Length,
                LocalWriteUtc = after.LastWriteTimeUtc,
                VerifiedUtc = DateTime.UtcNow,
                Md5Verified = true,
                Source = "repack proof",
                EquivalentMd5 = b.Md5,
                EquivalentSize = b.Size,
                ProofNote = result.Note
            });
            return result;
        }

        public static (LocalState State, bool Hashed) Judge(string folder, VerifiedLedger ledger, string name, File? a, File? b)
        {
            var info = new FileInfo(SyncPlan.LocalPath(folder, name));

            if (!info.Exists)
            {
                return (LocalState.Missing, false);
            }

            if (ledger.Current(name, info) is { } entry)
            {
                bool Same(File? f) => f != null && entry.Covers(f.Md5, f.Size, f.SizeSpecified);

                return (Same(b) ? LocalState.MatchesB : Same(a) ? LocalState.MatchesA : LocalState.Differs, entry.Md5Verified);
            }

            bool fitsA = a != null && (!a.SizeSpecified || a.Size == info.Length);
            bool fitsB = b != null && (!b.SizeSpecified || b.Size == info.Length);
            return (fitsA || fitsB ? LocalState.Unknown : LocalState.Differs, false);
        }

        /// <summary>
        /// Hashes local files the baseline couldn't vouch for and records what they are in the ledger.
        /// <paramref name="progress"/> gets bytes read.
        /// </summary>
        public static async System.Threading.Tasks.Task HashAsync(
            string folder,
            VerifiedLedger ledger,
            IEnumerable<(string Name, File[] Versions)> files,
            IProgress<long> progress,
            Action<string> onFile,
            CancellationToken ct)
        {
            foreach (var (name, versions) in files)
            {
                ct.ThrowIfCancellationRequested();
                onFile(name);
                string path = SyncPlan.LocalPath(folder, name);
                var before = new FileInfo(path);

                if (!before.Exists)
                {
                    continue;
                }

                string md5 = await DownloadEngine.HashMd5Async(path, progress, ct);
                var info = new FileInfo(path);
                var match = versions.FirstOrDefault(v => v.Md5.Equals(md5, StringComparison.OrdinalIgnoreCase));

                ledger.Put(name, new LedgerEntry
                {
                    Size = match?.Size ?? info.Length,
                    Md5 = md5,
                    Sha1 = match?.Sha1 ?? string.Empty,
                    Crc32 = match?.Crc32 ?? string.Empty,
                    LocalLength = info.Length,
                    LocalWriteUtc = info.LastWriteTimeUtc,
                    VerifiedUtc = DateTime.UtcNow,
                    Md5Verified = true,
                    Source = "hash"
                });
            }

            ledger.Save();
        }

        /// <summary>
        /// Applies the ticked rows' disk actions: renames, and moves into <paramref name="removedFolder"/> (never
        /// deletes). Downloads are left to the normal run that follows. Writes undo.json next to the moved files.
        /// </summary>
        /// <returns>The moves done and the errors hit (one line each).</returns>
        public static (List<MoveRecord> Moves, List<string> Errors) Apply(
            string folder,
            string removedFolder,
            VerifiedLedger ledger,
            IReadOnlyList<SyncRow> rows,
            IProgress<(int Done, int Total, string Name)>? progress,
            CancellationToken ct)
        {
            var moves = new List<MoveRecord>();
            var errors = new List<string>();
            var work = rows.Where(r => r.IsChecked && r.Action is SyncAction.Rename or SyncAction.MoveAway or SyncAction.Replace).ToList();
            int done = 0;

            foreach (var row in work)
            {
                ct.ThrowIfCancellationRequested();
                string oldName = row.Entry.OldName ?? row.Entry.Name;
                progress?.Report((++done, work.Count, oldName));
                string from = SyncPlan.LocalPath(folder, oldName);

                try
                {
                    if (row.Action == SyncAction.Rename)
                    {
                        string to = SyncPlan.LocalPath(folder, row.Entry.Name);
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        System.IO.File.Move(from, to);
                        ledger.Rename(oldName, row.Entry.Name);
                        moves.Add(new MoveRecord { From = from, To = to });
                    }
                    else
                    {
                        string to = SyncPlan.LocalPath(removedFolder, oldName);
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        System.IO.File.Move(from, to);
                        ledger.Remove(oldName);
                        moves.Add(new MoveRecord { From = from, To = to });
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{oldName}: {ex.Message}");
                }
            }

            if (moves.Count > 0)
            {
                try
                {
                    Directory.CreateDirectory(removedFolder);
                    System.IO.File.WriteAllText(Path.Combine(removedFolder, "undo.json"), JsonSerializer.Serialize(moves, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"undo.json: {ex.Message}");
                }
            }

            ledger.Save();
            return (moves, errors);
        }

        private static DateTime? SavedTime(SessionRecord session)
        {
            string path = Path.Combine(SessionStore.Folder, session.Id + ".json");
            return System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTime(path) : null;
        }
    }
}
