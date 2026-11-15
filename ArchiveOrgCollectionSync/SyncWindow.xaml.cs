namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Data;

    /// <summary>What a finished Sync hands back to the main window to download.</summary>
    public sealed class SyncOutcome
    {
        public SyncOutcome(LinkRecord link, File[] live, IReadOnlyList<SyncRow> rows, List<MoveRecord> moves, List<string> errors, string removedFolder)
        {
            this.Link = link;
            this.Live = live;
            this.Rows = rows;
            this.Moves = moves;
            this.Errors = errors;
            this.RemovedFolder = removedFolder;
        }

        public LinkRecord Link { get; }

        /// <summary>Gets archive.org's full file list as read by this sync.</summary>
        public File[] Live { get; }

        public IReadOnlyList<SyncRow> Rows { get; }

        public List<MoveRecord> Moves { get; }

        public List<string> Errors { get; }

        public string RemovedFolder { get; }
    }

    /// <summary>
    /// Sync: compare the last snapshot of a link with archive.org now, show what changed and what it means for the files
    /// on disk, then apply the ticked rows. Every step runs off the UI thread with progress, and the window can be
    /// hidden ("Run in background") while it works; <see cref="StatusChanged"/> feeds the main window's status strip.
    /// </summary>
    public partial class SyncWindow
    {
        private readonly string url;
        private readonly string collection;
        private readonly string folder;
        private readonly SyncOptions options;
        private readonly IReadOnlyList<SessionRecord> sessions;
        private readonly CancellationTokenSource cancellation = new();
        private readonly ObservableCollection<Stage> stages = new();
        private readonly ObservableCollection<SyncRow> rows = new();
        private readonly ListCollectionView rowsView;
        private readonly Stopwatch stageClock = new();

        private LinkRecord? link;
        private VerifiedLedger? ledger;
        private File[] live = Array.Empty<File>();
        private File[] older = Array.Empty<File>();
        private long? itemLastUpdated;
        private BaselineResult? baseline;
        private string? folderFilter;
        private CancellationTokenSource? skipStage;
        private bool busy;
        private bool finished;

        public SyncWindow(string url, string collection, string folder, SyncOptions options, IReadOnlyList<SessionRecord> sessions)
        {
            this.InitializeComponent();
            this.url = url;
            this.collection = collection;
            this.folder = folder;
            this.options = options;
            this.sessions = sessions;
            this.StagesItemsControl.ItemsSource = this.stages;
            this.rowsView = new ListCollectionView(this.rows) { Filter = this.FilterRow };
            this.RowsDataGrid.ItemsSource = this.rowsView;
            this.Title = $"Sync {collection}";
            this.HeaderTextBlock.Text = $"Sync {collection} → {folder}";
            this.SubTextBlock.Text = "Checking what changed on archive.org. Nothing on disk is touched until you confirm.";
        }

        /// <summary>Raised with a one-line status and a 0..1 fraction (null = no bar) whenever progress moves.</summary>
        public event Action<string, double?>? StatusChanged;

        /// <summary>Raised after the disk actions ran; the main window then downloads what's missing.</summary>
        public event Action<SyncOutcome>? Applied;

        /// <summary>Gets a value indicating whether a step is still running (the main window shows a progress strip).</summary>
        public bool IsBusy => this.busy;

        public string StatusText { get; private set; } = string.Empty;

        public double? StatusFraction { get; private set; }

        /// <summary>Starts the checking steps. Called once, right after Show.</summary>
        /// <param name="force">Read the list even when archive.org reports no change (the banner already found changes).</param>
        public async Task StartAsync(bool force = false)
        {
            await this.CheckAsync(force);
        }

        private CancellationToken Token => this.cancellation.Token;

        private static string When(long? unix) => unix is { } s ? DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "?";

        private async Task CheckAsync(bool force)
        {
            this.SetBusy(true);
            this.ShowPanel(this.StagesPanel);
            this.stages.Clear();

            try
            {
                // 1. The link and its history (creates snapshot #0 from the folder's _files.xml or the last session).
                var history = this.Begin("History", "reading earlier sessions and snapshots…");
                this.link = await Task.Run(() =>
                {
                    lock (LinkStore.Gate)
                    {
                        return LinkStore.GetOrCreate(this.url, this.collection, this.folder, this.sessions);
                    }
                });
                history.Done(this.link.Latest is { } latest
                    ? $"{Format.Plural(this.link.Snapshots.Count, "snapshot")}, latest {latest.WhenText} ({latest.Source}, {latest.FileCount} files)"
                    : "no earlier list of this item: everything will show as added");

                // 2. Has archive.org changed the item at all? (a few bytes)
                var metadata = this.Begin("archive.org metadata", "asking when the item last changed…");
                this.itemLastUpdated = await DownloadEngine.FetchItemLastUpdatedAsync(this.collection, this.Token);
                bool unchanged = this.itemLastUpdated != null && this.link.ItemLastUpdated != null && this.link.Latest != null
                    && this.itemLastUpdated <= this.link.ItemLastUpdated;
                metadata.Done($"item_last_updated {SyncWindow.When(this.itemLastUpdated)}"
                    + (this.link.ItemLastUpdated != null ? (unchanged ? " (same as at your last sync)" : $" (newer than your last sync, {SyncWindow.When(this.link.ItemLastUpdated)})") : string.Empty));

                if (unchanged && !force)
                {
                    var lastSync = this.link.LastChecked;
                    this.link.LastChecked = DateTime.Now;
                    this.link.LastCheck = new LinkCheck { Checked = DateTime.Now, RemoteUpdated = this.itemLastUpdated };
                    LinkStore.Save(this.link);
                    this.UpToDateTextBlock.Text = $"● Up to date.\n\narchive.org last changed {this.collection} on {SyncWindow.When(this.itemLastUpdated)}, before your last Sync"
                        + (lastSync is { } at ? $" ({at:yyyy-MM-dd HH:mm})" : string.Empty)
                        + $".\nYour snapshot of {this.link.Latest!.WhenText} is current. Nothing to do.";
                    this.ShowPanel(this.UpToDatePanel);
                    this.CompareAnywayButton.Visibility = Visibility.Visible;
                    this.CancelButton.Content = "Close";
                    this.Report($"Sync {this.collection}: up to date", null);
                    return;
                }

                // 3. The file list.
                var list = this.Begin("File list", $"{this.collection}_files.xml…");
                this.live = await DownloadEngine.FetchFileListAsync(this.collection, this.Token);
                var tracked = this.live.Where(f => SnapshotDiff.IsTracked(f, this.collection, this.options.OriginalsOnly)).ToArray();
                list.Done($"{this.live.Length} files listed, {tracked.Length} compared ({Format.Bytes(tracked.Sum(f => f.Size))})");

                // 4. Baseline: what's on disk, from a stat and earlier sessions (no hashing).
                var stat = this.Begin("Local files (stat only)", string.Empty);
                this.ledger = await Task.Run(() => VerifiedLedger.Load(this.folder));
                var progress = new Progress<(int Done, int Total, string Name)>(p => this.Step(stat, p.Done, p.Total, p.Name));
                this.baseline = await Task.Run(() => SyncPlan.Baseline(this.folder, this.ledger, this.sessions, tracked, progress, this.Token));
                stat.Done($"{this.baseline.Trusted} verified ({this.baseline.Adopted} taken over from earlier sessions), {this.baseline.NeedHash} unknown, {this.baseline.Different} different, {this.baseline.Missing} not on disk");

                // 5. The diff and what each change means for the local copy.
                var diff = this.Begin("Changes", string.Empty);
                var previous = this.link.Latest;
                this.older = previous == null ? Array.Empty<File>() : await Task.Run(() => LinkStore.LoadSnapshot(this.link, previous));
                var olderTracked = this.older.Where(f => SnapshotDiff.IsTracked(f, this.collection, this.options.OriginalsOnly)).ToArray();
                var entries = await Task.Run(() => SnapshotDiff.Compare(olderTracked, tracked));

                var lastSession = this.sessions.FirstOrDefault(s => s.Files.Count > 0);
                var ticks = lastSession?.Files.ToDictionary(f => f.Name, f => f.Checked, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                bool allTicked = ticks.Values.All(v => v);
                var newRows = entries.Select(e => new SyncRow(e, ticks.TryGetValue(e.OldName ?? e.Name, out bool t) ? t : allTicked)).ToList();
                var judged = await Task.Run(() => SyncPlan.Classify(this.folder, this.ledger, newRows, this.Token));
                SyncPlan.ApplyClassification(newRows, judged);

                // 6. Changed zips you have: look inside, keep the ones archive.org only re-zipped.
                await this.ProveAsync(newRows);

                foreach (var row in newRows)
                {
                    row.IsChecked = row.IsActionable && !row.Repack && (row.Wanted || row.Local != LocalState.Missing);
                    row.PropertyChanged += this.Row_PropertyChanged;
                    this.rows.Add(row);
                }

                int real = newRows.Count(r => r.Kind != ChangeKind.Touched);
                diff.Done(real == 0 ? "no changes" : string.Join("  ", Enum.GetValues<ChangeKind>().Where(k => k != ChangeKind.Touched)
                    .Select(k => (k, n: newRows.Count(r => r.Kind == k))).Where(x => x.n > 0).Select(x => $"{SnapshotDiff.Glyph(x.k)}{x.n} {x.k.ToString().ToLowerInvariant()}")));

                this.ShowReview(previous);
            }
            catch (OperationCanceledException)
            {
                this.CloseNow();
            }
            catch (Exception ex)
            {
                this.Fail(ex.Message);
            }
            finally
            {
                this.SetBusy(false);
            }
        }

        /// <summary>
        /// Reads archive.org's index of every changed zip you have (a few KB each) and compares it with your copy; rows
        /// whose files are provably the same become "≡ B (repack)" and are kept. Skippable; anything unproven stays a
        /// normal change.
        /// </summary>
        private async Task ProveAsync(List<SyncRow> all)
        {
            var candidates = SyncPlan.ProofCandidates(all, this.options.KeepRepacks);

            if (candidates.Count == 0 || this.ledger == null)
            {
                return;
            }

            var ledger = this.ledger;
            long total = candidates.Sum(r => new FileInfo(SyncPlan.LocalPath(this.folder, r.Entry.OldName ?? r.Name)) is { Exists: true } info ? info.Length : 0);
            long read = 0;
            int done = 0;
            var results = new ProofResult?[candidates.Count];
            var stage = this.Begin("Look inside changed zips", $"0 / {candidates.Count}");
            using var skip = CancellationTokenSource.CreateLinkedTokenSource(this.Token);
            this.skipStage = skip;
            this.SkipButton.Visibility = Visibility.Visible;
            this.StageProgressBar.IsIndeterminate = false;
            string collection = this.collection;
            string folder = this.folder;

            var work = Task.Run(() => Parallel.For(
                0,
                candidates.Count,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = skip.Token },
                i =>
                {
                    results[i] = SyncPlan.Prove(collection, folder, ledger, candidates[i], (name, ct) => DownloadEngine.OpenRemote(collection, name, ct), n => Interlocked.Add(ref read, n), skip.Token);
                    Interlocked.Increment(ref done);
                }));

            bool skipped = false;

            try
            {
                while (await Task.WhenAny(work, Task.Delay(250)) != work)
                {
                    int d = Volatile.Read(ref done);
                    long r = Interlocked.Read(ref read);
                    this.StageProgressBar.Value = candidates.Count > 0 ? d * 1000.0 / candidates.Count : 0;
                    stage.Detail = $"{d} / {candidates.Count} zips · {Format.Bytes(r)} read · {this.stageClock.Elapsed.TotalSeconds:0} s";
                    this.StageDetailTextBlock.Text = "Comparing archive.org's zip index with your copies (a few KB each from archive.org; your files are only read).";
                    this.Report($"Sync {this.collection}: looking inside changed zips {d} / {candidates.Count}", (double)d / candidates.Count);
                }

                await work;
            }
            catch (OperationCanceledException) when (!this.Token.IsCancellationRequested)
            {
                skipped = true;
            }
            finally
            {
                this.skipStage = null;
                this.SkipButton.Visibility = Visibility.Collapsed;
                await Task.Run(ledger.Save);
            }

            this.Token.ThrowIfCancellationRequested();

            for (int i = 0; i < candidates.Count; i++)
            {
                candidates[i].Proof = results[i];
            }

            var judged = await Task.Run(() => SyncPlan.Classify(this.folder, ledger, all, this.Token));
            SyncPlan.ApplyClassification(all, judged);
            var proven = results.Where(r => r != null).ToList();
            int same = proven.Count(r => r!.Verdict == ProofVerdict.Same);
            long kept = candidates.Where((r, i) => results[i]?.Verdict == ProofVerdict.Same).Sum(r => r.Entry.B!.Size);
            string counts = $"≡ {same} same files inside (kept, {Format.Bytes(kept)} not downloaded) · ≠ {proven.Count(r => r!.Verdict == ProofVerdict.Changed)} changed · ? {proven.Count(r => r!.Verdict == ProofVerdict.NotProven)} couldn't prove";
            stage.Done(skipped ? $"skipped after {done} / {candidates.Count}: {counts}" : counts);
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            this.skipStage?.Cancel();
        }

        private void ShowReview(SnapshotInfo? previous)
        {
            int real = this.rows.Count(r => r.Kind != ChangeKind.Touched);
            this.SubTextBlock.Text = $"A = {(previous == null ? "nothing (first sync)" : $"snapshot {previous.WhenText} ({previous.Source})")}   →   B = archive.org now ({SyncWindow.When(this.itemLastUpdated)})";
            var b = this.baseline!;
            this.BaselineTextBlock.Text = $"Files archive.org lists, on disk (stat only): ✔ {b.Trusted} verified   ? {b.NeedHash} unknown{(b.NeedHash > 0 ? $" ({Format.Bytes(b.NeedHashBytes)})" : string.Empty)}   ✖ {b.Different} different size   — {b.Missing} not on disk."
                + (b.Adopted > 0 ? $"  {b.Adopted} verification(s) were taken over from earlier sessions, so nothing had to be re-hashed." : string.Empty);
            var repacks = this.rows.Where(r => r.Repack).ToList();
            var proofs = this.rows.Where(r => r.Proof != null && !r.Repack).ToList();

            if (repacks.Count > 0 || proofs.Count > 0)
            {
                this.BaselineTextBlock.Text += $"\nInside changed zips: ≡ {repacks.Count} hold the same files as yours (archive.org only re-zipped them; kept, {Format.Bytes(repacks.Sum(r => r.Entry.B?.Size ?? 0))} not downloaded)"
                    + $"   ≠ {proofs.Count(r => r.Proof!.Verdict == ProofVerdict.Changed)} really changed   ? {proofs.Count(r => r.Proof!.Verdict == ProofVerdict.NotProven)} couldn't be proven (treated as changed).";
            }

            this.ShowRepacks.Content = $"≡ re-zipped only {repacks.Count}";
            this.ShowRepacks.Visibility = this.TickRepacksButton.Visibility = repacks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            this.ShowAdded.Content = $"+ added {this.Count(ChangeKind.Added)}";
            this.ShowRemoved.Content = $"− removed {this.Count(ChangeKind.Removed)}";
            this.ShowRenamed.Content = $"~ renamed {this.Count(ChangeKind.Renamed)}";
            this.ShowMoved.Content = $"⇄ moved {this.Count(ChangeKind.Moved)}";
            this.ShowChanged.Content = $"* changed {this.Count(ChangeKind.Changed)}";
            this.ShowTouched.Content = $"· touched {this.Count(ChangeKind.Touched)}";
            this.RefreshFolders();
            this.ShowPanel(this.ReviewPanel);
            this.SnapshotOnlyButton.Visibility = Visibility.Visible;
            this.ConfirmButton.Visibility = real > 0 ? Visibility.Visible : Visibility.Collapsed;
            this.CancelButton.Content = real > 0 ? "Cancel" : "Close";
            this.UpdateHashButton();
            this.UpdateFooter();

            if (real == 0)
            {
                this.FooterTextBlock.Text = previous == null
                    ? "First sync: nothing to compare with yet. Save the snapshot so the next Sync can tell what changed."
                    : "No changes since the last snapshot. Save the snapshot to mark this check.";
            }

            this.Report(real == 0 ? $"Sync {this.collection}: no changes" : $"Sync {this.collection}: {Format.Plural(real, "change")} ready to review", null);
        }

        private int Count(ChangeKind kind) => this.rows.Count(r => r.Kind == kind);

        private void RefreshFolders()
        {
            var counts = this.live.Where(f => SnapshotDiff.IsTracked(f, this.collection, this.options.OriginalsOnly))
                .GroupBy(f => SnapshotDiff.Folder(f.Name), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var summaries = this.rows.Where(r => r.Kind != ChangeKind.Touched)
                .GroupBy(r => r.Folder, StringComparer.OrdinalIgnoreCase)
                .Select(g => new FolderSummary(g.Key, counts.TryGetValue(g.Key, out int n) ? n : 0, g.ToList()))
                .OrderBy(s => s.Folder, StringComparer.OrdinalIgnoreCase)
                .ToList();
            summaries.Insert(0, new FolderSummary(null, counts.Values.Sum(), this.rows.Where(r => r.Kind != ChangeKind.Touched).ToList()));
            this.FoldersDataGrid.ItemsSource = summaries;
        }

        private bool FilterRow(object value)
        {
            var row = (SyncRow)value;
            bool kind = row.Kind switch
            {
                ChangeKind.Added => this.ShowAdded.IsChecked == true,
                ChangeKind.Removed => this.ShowRemoved.IsChecked == true,
                ChangeKind.Renamed => this.ShowRenamed.IsChecked == true,
                ChangeKind.Moved => this.ShowMoved.IsChecked == true,
                ChangeKind.Changed => this.ShowChanged.IsChecked == true,
                _ => this.ShowTouched.IsChecked == true
            };
            string search = this.SearchTextBox.Text.Trim();

            return kind
                && (!row.Repack || this.ShowRepacks.IsChecked == true)
                && (this.folderFilter == null || row.Folder.Equals(this.folderFilter, StringComparison.OrdinalIgnoreCase))
                && (search.Length == 0
                    || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (row.Entry.OldName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (this.IsLoaded)
            {
                this.rowsView.Refresh();
            }
        }

        private void FoldersDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            this.folderFilter = (this.FoldersDataGrid.SelectedItem as FolderSummary)?.Folder;
            this.rowsView.Refresh();
        }

        private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SyncRow.IsChecked) or nameof(SyncRow.Action))
            {
                this.UpdateFooter();
            }
        }

        private void UpdateFooter()
        {
            var ticked = this.rows.Where(r => r.IsChecked).ToList();
            long bytes = ticked.Sum(r => r.DownloadBytes);
            int downloads = ticked.Count(r => r.Action is SyncAction.Download or SyncAction.Replace);
            int renames = ticked.Count(r => r.Action == SyncAction.Rename);
            int moves = ticked.Count(r => r.Action is SyncAction.MoveAway or SyncAction.Replace);
            int checks = ticked.Count(r => r.Action == SyncAction.Check);
            this.FooterTextBlock.Text = ticked.Count == 0
                ? "Nothing ticked: confirming only saves the snapshot."
                : $"Will: download {downloads} ({Format.Bytes(bytes)}) · rename {renames} · move {moves} to _removed\\"
                    + (checks > 0 ? $" · check {checks}" : string.Empty);
        }

        private List<SyncRow> UnknownRows() => this.rows.Where(r => r.Local == LocalState.Unknown).ToList();

        private void UpdateHashButton()
        {
            var names = this.HashTargets();
            long bytes = names.Sum(n => SafeLength(SyncPlan.LocalPath(this.folder, n.Name)));
            this.HashButton.Content = $"Hash unknown ({names.Count}, {Format.Bytes(bytes)})";
            this.HashButton.Visibility = names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            static long SafeLength(string path)
            {
                try
                {
                    return new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    return 0;
                }
            }
        }

        /// <summary>Gets the local files to hash with the archive.org versions they might be: diff rows first, then other unknown files.</summary>
        private List<(string Name, File[] Versions)> HashTargets()
        {
            var byName = new Dictionary<string, File[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in this.UnknownRows())
            {
                byName[row.Entry.OldName ?? row.Name] = new[] { row.Entry.A, row.Entry.B }.Where(f => f != null).Cast<File>().ToArray();
            }

            var liveByName = this.live.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var olderByName = this.older.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (string name in this.baseline?.NeedHashNames ?? new List<string>())
            {
                if (!byName.ContainsKey(name))
                {
                    byName[name] = new[] { liveByName.GetValueOrDefault(name), olderByName.GetValueOrDefault(name) }.Where(f => f != null).Cast<File>().ToArray();
                }
            }

            return byName.Select(p => (p.Key, p.Value)).ToList();
        }

        private async void HashButton_Click(object sender, RoutedEventArgs e)
        {
            var targets = this.HashTargets();

            if (targets.Count == 0 || this.ledger == null || this.busy)
            {
                return;
            }

            long total = targets.Sum(t => new FileInfo(SyncPlan.LocalPath(this.folder, t.Name)) is { Exists: true } info ? info.Length : 0);
            long done = 0;
            string current = string.Empty;
            this.SetBusy(true);
            this.ShowPanel(this.StagesPanel);
            var hash = this.Begin("Hashing", string.Empty);
            this.stageClock.Restart();

            var progress = new Progress<long>(bytes =>
            {
                done += bytes;
                double seconds = this.stageClock.Elapsed.TotalSeconds;
                double speed = seconds > 0 ? done / seconds : 0;
                string eta = speed > 1 ? " · ETA " + Format.Duration(TimeSpan.FromSeconds((total - done) / speed)) : string.Empty;
                this.StageProgressBar.Value = total > 0 ? done * 1000.0 / total : 0;
                hash.Detail = $"{Format.Bytes(done)} / {Format.Bytes(total)} · {Format.Speed(speed)}{eta}";
                this.StageDetailTextBlock.Text = current;
                this.Report($"Sync {this.collection}: hashing {Format.Bytes(done)} / {Format.Bytes(total)}{eta}", total > 0 ? (double)done / total : null);
            });

            try
            {
                await Task.Run(() => SyncPlan.HashAsync(this.folder, this.ledger, targets, progress, name => this.Dispatcher.BeginInvoke(() => current = name), this.Token));
                hash.Done($"{Format.Plural(targets.Count, "file")} hashed ({Format.Bytes(total)}) in {Format.Duration(this.stageClock.Elapsed)}");
                var all = this.rows.ToList();
                var wasActionable = all.Where(r => r.IsActionable).ToHashSet();
                var judged = await Task.Run(() => SyncPlan.Classify(this.folder, this.ledger, all, this.Token));
                SyncPlan.ApplyClassification(all, judged);
                await this.ProveAsync(all);
                this.baseline = await Task.Run(() => SyncPlan.Baseline(
                    this.folder, this.ledger, this.sessions, this.live.Where(f => SnapshotDiff.IsTracked(f, this.collection, this.options.OriginalsOnly)).ToArray(), null, this.Token));

                // Rows the hash made actionable get the usual default; the user's own ticks stay as they were.
                foreach (var row in all.Where(r => r.IsActionable && !r.Repack && !wasActionable.Contains(r) && (r.Wanted || r.Local != LocalState.Missing)))
                {
                    row.IsChecked = true;
                }

                this.ShowReview(this.link?.Latest);
            }
            catch (OperationCanceledException)
            {
                this.CloseNow();
            }
            catch (Exception ex)
            {
                this.Fail(ex.Message);
            }
            finally
            {
                this.SetBusy(false);
            }
        }

        private void TickAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (SyncRow row in this.rowsView)
            {
                row.IsChecked = row.IsActionable;
            }
        }

        private void TickRepacksButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (SyncRow row in this.rowsView)
            {
                if (row.Repack)
                {
                    row.IsChecked = true;
                }
            }
        }

        private void TickNoneButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (SyncRow row in this.rowsView)
            {
                row.IsChecked = false;
            }
        }

        private void RowsDataGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Space)
            {
                return;
            }

            var selected = this.RowsDataGrid.SelectedItems.OfType<SyncRow>().Where(r => r.IsActionable).ToList();
            bool value = selected.Any(r => !r.IsChecked);

            foreach (var row in selected)
            {
                row.IsChecked = value;
            }

            e.Handled = true;
        }

        private void SnapshotOnlyButton_Click(object sender, RoutedEventArgs e)
        {
            this.SaveSnapshot();
            this.Report($"Sync {this.collection}: snapshot saved", null);
            this.finished = true;
            this.Close();
        }

        private async void CompareAnywayButton_Click(object sender, RoutedEventArgs e)
        {
            this.CompareAnywayButton.Visibility = Visibility.Collapsed;
            this.CancelButton.Content = "Cancel";
            await this.CheckAsync(force: true);
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.link == null || this.ledger == null || this.busy)
            {
                return;
            }

            var confirm = new ConfirmSyncWindow(this.folder, this.options, this.rows.ToList(), this.older.Length) { Owner = this };

            if (confirm.ShowDialog() != true)
            {
                return;
            }

            string removedFolder = Path.Combine(this.folder, "_removed", DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
            this.SetBusy(true);
            this.ShowPanel(this.StagesPanel);
            var apply = this.Begin("Renames and moves", string.Empty);
            var progress = new Progress<(int Done, int Total, string Name)>(p => this.Step(apply, p.Done, p.Total, p.Name));

            try
            {
                var snapshotRows = this.rows.ToList();
                var (moves, errors) = await Task.Run(() => SyncPlan.Apply(this.folder, removedFolder, this.ledger, snapshotRows, progress, this.Token));
                apply.Done($"{Format.Plural(moves.Count, "file")} renamed or moved" + (errors.Count > 0 ? $", {errors.Count} failed" : string.Empty));
                this.SaveSnapshot();
                this.finished = true;
                this.CloseNow();
                this.Applied?.Invoke(new SyncOutcome(this.link, this.live, snapshotRows, moves, errors, removedFolder));
            }
            catch (OperationCanceledException)
            {
                this.CloseNow();
            }
            catch (Exception ex)
            {
                this.Fail(ex.Message);
            }
            finally
            {
                this.SetBusy(false);
            }
        }

        private void SaveSnapshot()
        {
            if (this.link == null || this.live.Length == 0)
            {
                return;
            }

            lock (LinkStore.Gate)
            {
                LinkStore.AddSnapshot(this.link, this.live, "sync", DateTime.Now);
                this.link.ItemLastUpdated = this.itemLastUpdated ?? this.link.ItemLastUpdated;
                this.link.LastChecked = DateTime.Now;
                this.link.LastCheck = new LinkCheck { Checked = DateTime.Now, RemoteUpdated = this.itemLastUpdated };
                this.link.Url = this.url;
                LinkStore.Save(this.link);
            }
        }

        private void BackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            this.Report(this.StatusText, this.StatusFraction);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (this.busy)
            {
                // Let the running step notice the cancel and close the window itself.
                this.cancellation.Cancel();
                this.CancelButton.IsEnabled = false;
                this.CancelButton.Content = "Cancelling…";
                e.Cancel = true;
                return;
            }

            if (!this.finished && this.link != null)
            {
                this.link.LastChecked = DateTime.Now;
                LinkStore.Save(this.link);
            }

            this.Report(string.Empty, null);
        }

        /// <summary>The main window is exiting: stop whatever runs and let the window close without asking.</summary>
        public void Abandon()
        {
            this.cancellation.Cancel();
            this.busy = false;
            this.finished = true;
        }

        private void CloseNow()
        {
            this.SetBusy(false);
            this.Close();
        }

        private void Fail(string message)
        {
            this.SubTextBlock.Text = "Sync stopped: " + message;
            this.SubTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            this.stages.LastOrDefault()?.Fail(message);
            this.CancelButton.Content = "Close";
            this.Report($"Sync {this.collection} failed: {message}", null);

            if (!this.IsVisible)
            {
                this.Show();
            }
        }

        private void SetBusy(bool value)
        {
            this.busy = value;
            this.BackgroundButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            this.ConfirmButton.IsEnabled = this.SnapshotOnlyButton.IsEnabled = this.HashButton.IsEnabled = !value;
            this.StageProgressBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowPanel(FrameworkElement panel)
        {
            foreach (var p in new FrameworkElement[] { this.StagesPanel, this.UpToDatePanel, this.ReviewPanel })
            {
                p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
            }

            if (panel != this.ReviewPanel)
            {
                this.ConfirmButton.Visibility = this.SnapshotOnlyButton.Visibility = this.HashButton.Visibility = Visibility.Collapsed;
                this.FooterTextBlock.Text = string.Empty;
            }
        }

        private Stage Begin(string title, string detail)
        {
            var stage = new Stage(title) { Detail = detail };
            this.stages.Add(stage);
            this.stageClock.Restart();
            this.StageProgressBar.IsIndeterminate = true;
            this.StageDetailTextBlock.Text = string.Empty;
            this.Report($"Sync {this.collection}: {title.ToLowerInvariant()}…", null);
            return stage;
        }

        private void Step(Stage stage, int done, int total, string name)
        {
            this.StageProgressBar.IsIndeterminate = false;
            this.StageProgressBar.Value = total > 0 ? done * 1000.0 / total : 0;
            stage.Detail = $"{done} / {total} files · {this.stageClock.Elapsed.TotalSeconds:0.0} s";
            this.StageDetailTextBlock.Text = name;
            this.Report($"Sync {this.collection}: {stage.Title.ToLowerInvariant()} {done} / {total}", total > 0 ? (double)done / total : null);
        }

        private void Report(string text, double? fraction)
        {
            this.StatusText = text;
            this.StatusFraction = fraction;
            this.StatusChanged?.Invoke(text, fraction);
        }

        /// <summary>One line of the checking list: ▶ running, ✔ done, ✖ failed.</summary>
        public sealed class Stage : INotifyPropertyChanged
        {
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private string glyph = "▶";
            private string detail = string.Empty;

            public Stage(string title)
            {
                this.Title = title;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public string Title { get; }

            public string Glyph
            {
                get => this.glyph;
                private set
                {
                    this.glyph = value;
                    this.OnPropertyChanged();
                }
            }

            public string Detail
            {
                get => this.detail;
                set
                {
                    this.detail = value;
                    this.OnPropertyChanged();
                }
            }

            public void Done(string detail)
            {
                this.Glyph = "✔";
                this.Detail = $"{detail} · {this.clock.Elapsed.TotalSeconds:0.0} s";
            }

            public void Fail(string message)
            {
                if (this.Glyph == "▶")
                {
                    this.Glyph = "✖";
                    this.Detail = message;
                }
            }

            private void OnPropertyChanged([CallerMemberName] string? name = null)
            {
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        /// <summary>A row of the folder summary grid; a null folder is the "all folders" row.</summary>
        public sealed class FolderSummary
        {
            public FolderSummary(string? folder, int files, List<SyncRow> rows)
            {
                this.Folder = folder;
                this.Files = files;
                this.Added = rows.Count(r => r.Kind == ChangeKind.Added);
                this.Removed = rows.Count(r => r.Kind == ChangeKind.Removed);
                this.Renamed = rows.Count(r => r.Kind == ChangeKind.Renamed);
                this.Moved = rows.Count(r => r.Kind == ChangeKind.Moved);
                this.Changed = rows.Count(r => r.Kind == ChangeKind.Changed);
            }

            public string? Folder { get; }

            public string FolderText => this.Folder == null ? "(all folders)" : this.Folder.Length == 0 ? "(item root)" : this.Folder;

            public int Files { get; }

            public int Added { get; }

            public int Removed { get; }

            public int Renamed { get; }

            public int Moved { get; }

            public int Changed { get; }
        }
    }
}
