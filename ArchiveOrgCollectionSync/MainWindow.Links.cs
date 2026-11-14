namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Data;
    using System.Windows.Media;

    using Microsoft.WindowsAPICodePack.Dialogs;

    /// <summary>
    /// The Links tab: every download link (identifier + folder) with its runs, snapshots, an any-to-any diff and the
    /// history of single files. Everything that reads the network or a destination folder runs in the background with
    /// progress and Cancel.
    /// </summary>
    public partial class MainWindow
    {
        private readonly Dictionary<string, File[]> snapshotCache = new(StringComparer.OrdinalIgnoreCase);
        private List<SessionRecord> allSessions = new();
        private CancellationTokenSource? checkAll;
        private CancellationTokenSource? diffRun;
        private CancellationTokenSource? linkDetails;
        private List<SyncRow> diffRows = new();
        private ListCollectionView? diffView;
        private string? diffFolderFilter;
        private LinkRecord? diffLink;

        private LinkRecord? SelectedLink => (this.LinksListBox.SelectedItem as LinkItem)?.Link;

        private List<SessionRecord> SessionsOf(LinkRecord link) => this.allSessions
            .Where(s => s.Collection.Equals(link.Collection, StringComparison.OrdinalIgnoreCase) && MainWindow.SameFolderSafe(s.Folder, link.Folder))
            .ToList();

        private static bool SameFolderSafe(string a, string b)
        {
            try
            {
                return MainWindow.SameFolder(a, b);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        private static bool IsTracked(File f, string collection, bool originalsOnly) => SnapshotDiff.IsTracked(f, collection, originalsOnly);

        /// <summary>Reloads sessions and links and keeps the selected link.</summary>
        private void RefreshLinks()
        {
            string? selectedId = this.SelectedLink?.Id;
            this.allSessions = SessionStore.LoadAll();
            var items = new List<LinkItem> { LinkItem.AllRuns(this.allSessions.Count) };
            items.AddRange(LinkStore.All(this.allSessions).Select(l =>
            {
                var runs = this.SessionsOf(l);
                return new LinkItem(l, runs.Count, runs.Select(r => r.Ended ?? r.Started).DefaultIfEmpty().Max());
            }));
            this.LinksListBox.ItemsSource = items;
            this.LinksListBox.SelectedItem = items.FirstOrDefault(i => i.Link != null && i.Link.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        }

        /// <summary>Selects the link for a collection and folder and shows one of its tabs.</summary>
        private void ShowLink(string collection, string folder, TabItem tab)
        {
            this.Tabs.SelectedItem = this.LinksTabItem;
            this.RefreshLinks();
            string id;

            try
            {
                id = LinkStore.KeyFor(collection, folder);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            if (this.LinksListBox.ItemsSource is List<LinkItem> items && items.FirstOrDefault(i => i.Link?.Id.Equals(id, StringComparison.OrdinalIgnoreCase) == true) is { } item)
            {
                this.LinksListBox.SelectedItem = item;
                this.LinkTabs.SelectedItem = tab;
            }
        }

        private void LinksListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            this.ShowSessionsOfSelection();
            this.LoadLinkDetails();
            this.UpdateSessionButtons();
        }

        private void LinksListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            this.LinkOpenButton_Click(sender, e);
        }

        private void ShowSessionsOfSelection()
        {
            string? selectedId = this.SelectedSession?.Id;
            var records = this.SelectedLink is { } link ? this.SessionsOf(link) : this.allSessions;
            this.SessionsListView.ItemsSource = records;
            this.SessionsListView.SelectedItem = records.FirstOrDefault(r => r.Id == selectedId);
        }

        private void UpdateLinkButtons()
        {
            var link = this.SelectedLink;
            bool idle = !this.running && this.syncWindow == null;
            this.LinkOpenButton.IsEnabled = idle && link != null;
            this.LinkRelocateButton.IsEnabled = idle && link != null;
            this.LinkForgetButton.IsEnabled = idle && link != null;
            this.CheckLinksButton.IsEnabled = this.checkAll == null;
            bool hasSnapshots = link?.Snapshots.Count > 0;
            this.SnapshotComparePreviousButton.IsEnabled = hasSnapshots;
            this.SnapshotCompareLiveButton.IsEnabled = hasSnapshots;
            this.SnapshotExportButton.IsEnabled = hasSnapshots;
            this.DiffCompareButton.IsEnabled = link != null;
            this.DiffSyncButton.IsEnabled = idle && link != null;
            this.FileHistoryShowButton.IsEnabled = hasSnapshots;
        }

        // ----- Link details: header, snapshots, diff choices, file names -----

        private async void LoadLinkDetails()
        {
            this.linkDetails?.Cancel();
            var link = this.SelectedLink;
            var snapshotsTab = this.LinkTabs.Items.OfType<TabItem>().Where(t => t != this.LinkTabs.Items[0]);

            foreach (var tab in snapshotsTab)
            {
                tab.IsEnabled = link != null;
            }

            if (link == null)
            {
                this.LinkHeaderTextBlock.Text = $"All runs ({this.allSessions.Count}), every collection and folder. Pick a link on the left for its snapshots, diff and file history.";
                this.LinkTabs.SelectedIndex = 0;
                this.SnapshotsListView.ItemsSource = null;
                return;
            }

            var (glyph, status) = LinkHistory.Describe(link, (this.LinksListBox.SelectedItem as LinkItem)?.LastRun);
            this.LinkHeaderTextBlock.Text = $"{link.Collection} → {link.Folder}   {glyph} {status}";
            this.SnapshotsInfoTextBlock.Text = link.Snapshots.Count == 0
                ? "No snapshots yet. ⟳ Sync (or Start) saves the item's file list, so later Syncs can tell what changed."
                : $"{Format.Plural(link.Snapshots.Count, "snapshot")}. A snapshot is saved when Sync or Start sees a list that differs from the last one."
                    + (link.LastChecked is { } at ? $" Last Sync check {at:yyyy-MM-dd HH:mm}." : string.Empty);

            var rows = link.Snapshots.Select((s, i) => new SnapshotRow(i + 1, s)).Reverse().ToList();
            this.SnapshotsListView.ItemsSource = rows;
            this.SnapshotsListView.SelectedIndex = rows.Count > 0 ? 0 : -1;

            var choices = link.Snapshots.Select((s, i) => new SnapshotChoice($"#{i + 1}  {s.WhenText}  ({s.Source})", s)).Reverse().ToList();
            this.DiffAComboBox.ItemsSource = choices;
            this.DiffBComboBox.ItemsSource = new[] { SnapshotChoice.Live }.Concat(choices).ToList();
            this.DiffAComboBox.SelectedIndex = choices.Count > 0 ? 0 : -1;
            this.DiffBComboBox.SelectedIndex = 0;
            this.UpdateLinkButtons();

            if (this.diffLink?.Id != link.Id)
            {
                this.ClearDiff();
                this.FileHistoryListView.ItemsSource = null;
                this.FileHistoryInfoTextBlock.Text = string.Empty;
                this.FileHistoryComboBox.ItemsSource = null;
                this.FileHistoryComboBox.Text = string.Empty;
            }

            if (link.Snapshots.Count == 0)
            {
                return;
            }

            // Δ vs previous and the file names, from the saved snapshots (local files only, but maybe many).
            using var cts = new CancellationTokenSource();
            this.linkDetails = cts;
            bool originalsOnly = this.OriginalsCheckBox.IsChecked == true;

            try
            {
                var lists = await Task.Run(() => link.Snapshots.Select(s => this.LoadSnapshotCached(link, s)).ToList(), cts.Token);

                if (cts.IsCancellationRequested)
                {
                    return;
                }

                var deltas = await Task.Run(
                    () => lists.Select((files, i) => i == 0
                        ? "first snapshot"
                        : MainWindow.DeltaText(SnapshotDiff.Compare(
                            lists[i - 1].Where(f => IsTracked(f, link.Collection, originalsOnly)),
                            files.Where(f => IsTracked(f, link.Collection, originalsOnly))))).ToList(),
                    cts.Token);

                foreach (var row in rows)
                {
                    row.Delta = deltas[row.Number - 1];
                }

                this.FileHistoryComboBox.ItemsSource = lists[^1]
                    .Where(f => IsTracked(f, link.Collection, false))
                    .Select(f => f.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
            {
            }
            finally
            {
                if (this.linkDetails == cts)
                {
                    this.linkDetails = null;
                }
            }
        }

        private static string DeltaText(List<DiffEntry> diff)
        {
            var real = diff.Where(d => d.Kind != ChangeKind.Touched).ToList();
            int touched = diff.Count - real.Count;
            return real.Count == 0 && touched == 0 ? "no changes"
                : MainWindow.ChangeSummary(real) + (touched > 0 ? $"{(real.Count > 0 ? "  " : string.Empty)}·{touched} touched" : string.Empty);
        }

        private File[] LoadSnapshotCached(LinkRecord link, SnapshotInfo info)
        {
            string key = link.Id + "/" + info.Id;

            lock (this.snapshotCache)
            {
                if (this.snapshotCache.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }

            var files = LinkStore.LoadSnapshot(link, info);

            lock (this.snapshotCache)
            {
                this.snapshotCache[key] = files;
            }

            return files;
        }

        // ----- Snapshots tab -----

        private void SnapshotComparePreviousButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SnapshotsListView.SelectedItem is not SnapshotRow row || this.DiffAComboBox.ItemsSource is not List<SnapshotChoice> choices)
            {
                return;
            }

            var b = choices.FirstOrDefault(c => c.Info == row.Info);
            var a = choices.FirstOrDefault(c => c.Info != null && c.Info.Taken < row.Info.Taken) ?? b;
            this.DiffAComboBox.SelectedItem = a;
            this.DiffBComboBox.SelectedItem = ((List<SnapshotChoice>)this.DiffBComboBox.ItemsSource).FirstOrDefault(c => c.Info == row.Info);
            this.LinkTabs.SelectedItem = this.DiffTabItem;
            this.DiffCompareButton_Click(sender, e);
        }

        private void SnapshotCompareLiveButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SnapshotsListView.SelectedItem is not SnapshotRow row || this.DiffAComboBox.ItemsSource is not List<SnapshotChoice> choices)
            {
                return;
            }

            this.DiffAComboBox.SelectedItem = choices.FirstOrDefault(c => c.Info == row.Info);
            this.DiffBComboBox.SelectedIndex = 0;
            this.LinkTabs.SelectedItem = this.DiffTabItem;
            this.DiffCompareButton_Click(sender, e);
        }

        private void SnapshotExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedLink is not { } link || this.SnapshotsListView.SelectedItem is not SnapshotRow row)
            {
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"{link.Collection}_{row.Info.Id}.csv", Filter = "CSV files (*.csv)|*.csv", DefaultExt = ".csv" };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var csv = new StringBuilder("Name,Source,Size,MD5,SHA1,CRC32,Uploaded,OldVersion\r\n");

            foreach (var f in this.LoadSnapshotCached(link, row.Info))
            {
                csv.Append(Format.Csv(f.Name)).Append(',').Append(f.Source).Append(',').Append(f.Size).Append(',').Append(f.Md5).Append(',')
                    .Append(f.Sha1).Append(',').Append(f.Crc32).Append(',').Append(LinkHistory.UnixDate(f.Mtime)).Append(',').Append(f.OldVersion).Append("\r\n");
            }

            System.IO.File.WriteAllText(dialog.FileName, csv.ToString(), Encoding.UTF8);
        }

        // ----- Diff tab -----

        private void ClearDiff()
        {
            this.diffRun?.Cancel();
            this.diffRows = new List<SyncRow>();
            this.diffView = null;
            this.diffLink = null;
            this.DiffRowsDataGrid.ItemsSource = null;
            this.DiffFoldersDataGrid.ItemsSource = null;
            this.DiffSummaryTextBlock.Text = string.Empty;
        }

        private async Task<File[]> LoadChoiceAsync(LinkRecord link, SnapshotChoice choice, CancellationToken ct)
        {
            return choice.Info == null
                ? await DownloadEngine.FetchFileListAsync(link.Collection, ct)
                : await Task.Run(() => this.LoadSnapshotCached(link, choice.Info), ct);
        }

        private async void DiffCompareButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedLink is not { } link || this.DiffAComboBox.SelectedItem is not SnapshotChoice a || this.DiffBComboBox.SelectedItem is not SnapshotChoice b)
            {
                return;
            }

            this.diffRun?.Cancel();
            using var cts = new CancellationTokenSource();
            this.diffRun = cts;
            bool originalsOnly = this.OriginalsCheckBox.IsChecked == true;
            this.DiffProgressPanel.Visibility = Visibility.Visible;
            this.DiffCompareButton.IsEnabled = false;

            try
            {
                this.DiffProgressTextBlock.Text = $"Loading A: {a.Label}…";
                var fa = await this.LoadChoiceAsync(link, a, cts.Token);
                this.DiffProgressTextBlock.Text = b.Info == null ? $"Reading archive.org's file list for {link.Collection}…" : $"Loading B: {b.Label}…";
                var fb = await this.LoadChoiceAsync(link, b, cts.Token);
                this.DiffProgressTextBlock.Text = "Comparing…";
                var entries = await Task.Run(() => SnapshotDiff.Compare(fa.Where(f => IsTracked(f, link.Collection, originalsOnly)), fb.Where(f => IsTracked(f, link.Collection, originalsOnly))), cts.Token);
                var rows = entries.Select(entry => new SyncRow(entry, true)).ToList();

                this.DiffProgressTextBlock.Text = $"Checking local files in {link.Folder} (stat only)…";
                var judged = await Task.Run(
                    () =>
                    {
                        if (!Directory.Exists(link.Folder))
                        {
                            return null;
                        }

                        var ledger = VerifiedLedger.Load(link.Folder);
                        return SyncPlan.Classify(link.Folder, ledger, rows, cts.Token);
                    },
                    cts.Token);

                if (judged != null)
                {
                    SyncPlan.ApplyClassification(rows, judged);
                }

                this.diffLink = link;
                this.diffRows = rows;
                this.diffFolderFilter = null;
                this.diffView = new ListCollectionView(rows) { Filter = this.FilterDiffRow };
                this.DiffRowsDataGrid.ItemsSource = this.diffView;

                var counts = fb.Where(f => IsTracked(f, link.Collection, originalsOnly)).GroupBy(f => SnapshotDiff.Folder(f.Name), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                var real = rows.Where(r => r.Kind != ChangeKind.Touched).ToList();
                var folders = real.GroupBy(r => r.Folder, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new SyncWindow.FolderSummary(g.Key, counts.TryGetValue(g.Key, out int n) ? n : 0, g.ToList()))
                    .OrderBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                folders.Insert(0, new SyncWindow.FolderSummary(null, counts.Values.Sum(), real));
                this.DiffFoldersDataGrid.ItemsSource = folders;

                foreach (var (box, kind) in new[]
                {
                    (this.DiffShowAdded, ChangeKind.Added), (this.DiffShowRemoved, ChangeKind.Removed), (this.DiffShowRenamed, ChangeKind.Renamed),
                    (this.DiffShowMoved, ChangeKind.Moved), (this.DiffShowChanged, ChangeKind.Changed), (this.DiffShowTouched, ChangeKind.Touched)
                })
                {
                    box.Content = $"{SnapshotDiff.Glyph(kind)} {kind.ToString().ToLowerInvariant()} {rows.Count(r => r.Kind == kind)}";
                }

                this.DiffSummaryTextBlock.Text = (real.Count == 0 ? "No changes" : MainWindow.DeltaText(entries))
                    + $" between A and B." + (judged == null ? " The destination folder isn't reachable, so Local is blank." : string.Empty);
            }
            catch (OperationCanceledException)
            {
                this.DiffSummaryTextBlock.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                this.DiffSummaryTextBlock.Text = "Compare failed: " + ex.Message;
            }
            finally
            {
                if (this.diffRun == cts)
                {
                    this.diffRun = null;
                    this.DiffProgressPanel.Visibility = Visibility.Collapsed;
                    this.DiffCompareButton.IsEnabled = this.SelectedLink != null;
                }
            }
        }

        private void DiffCancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.diffRun?.Cancel();
        }

        private bool FilterDiffRow(object value)
        {
            var row = (SyncRow)value;
            bool kind = row.Kind switch
            {
                ChangeKind.Added => this.DiffShowAdded.IsChecked == true,
                ChangeKind.Removed => this.DiffShowRemoved.IsChecked == true,
                ChangeKind.Renamed => this.DiffShowRenamed.IsChecked == true,
                ChangeKind.Moved => this.DiffShowMoved.IsChecked == true,
                ChangeKind.Changed => this.DiffShowChanged.IsChecked == true,
                _ => this.DiffShowTouched.IsChecked == true
            };
            string search = this.DiffSearchTextBox.Text.Trim();
            return kind
                && (this.diffFolderFilter == null || row.Folder.Equals(this.diffFolderFilter, StringComparison.OrdinalIgnoreCase))
                && (search.Length == 0 || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || (row.Entry.OldName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        private void DiffFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (this.IsLoaded)
            {
                this.diffView?.Refresh();
            }
        }

        private void DiffFoldersDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            this.diffFolderFilter = (this.DiffFoldersDataGrid.SelectedItem as SyncWindow.FolderSummary)?.Folder;
            this.diffView?.Refresh();
        }

        private void DiffExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.diffLink is not { } link || this.diffRows.Count == 0)
            {
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"{link.Collection}_diff.csv", Filter = "CSV files (*.csv)|*.csv", DefaultExt = ".csv" };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var csv = new StringBuilder("Change,Name,OldName,A Size,A MD5,A SHA1,A Uploaded,B Size,B MD5,B SHA1,B Uploaded,Local\r\n");

            foreach (var row in this.diffRows)
            {
                var (fa, fb) = (row.Entry.A, row.Entry.B);
                csv.Append(row.Kind).Append(',').Append(Format.Csv(row.Name)).Append(',').Append(Format.Csv(row.Entry.OldName ?? string.Empty)).Append(',')
                    .Append(fa?.Size.ToString() ?? string.Empty).Append(',').Append(fa?.Md5).Append(',').Append(fa?.Sha1).Append(',').Append(fa == null ? string.Empty : LinkHistory.UnixDate(fa.Mtime)).Append(',')
                    .Append(fb?.Size.ToString() ?? string.Empty).Append(',').Append(fb?.Md5).Append(',').Append(fb?.Sha1).Append(',').Append(fb == null ? string.Empty : LinkHistory.UnixDate(fb.Mtime)).Append(',')
                    .Append(Format.Csv(row.LocalText)).Append("\r\n");
            }

            System.IO.File.WriteAllText(dialog.FileName, csv.ToString(), Encoding.UTF8);
        }

        private void DiffSyncButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedLink is not { } link || this.running || this.syncWindow != null)
            {
                return;
            }

            this.UrlTextBox.Text = link.Url.Length > 0 ? link.Url : $"https://archive.org/details/{link.Collection}";
            this.FolderTextBox.Text = link.Folder;
            this.Tabs.SelectedIndex = 0;
            this.SyncButton_Click(this.SyncButton, e);
        }

        private void DiffHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.DiffRowsDataGrid.SelectedItem is SyncRow row)
            {
                this.FileHistoryComboBox.Text = row.Name;
                this.LinkTabs.SelectedItem = this.FileHistoryTabItem;
                this.FileHistoryShowButton_Click(sender, e);
            }
        }

        // ----- File history tab -----

        private async void FileHistoryShowButton_Click(object sender, RoutedEventArgs e)
        {
            string name = this.FileHistoryComboBox.Text.Trim();

            if (this.SelectedLink is not { } link || name.Length == 0)
            {
                return;
            }

            this.FileHistoryInfoTextBlock.Text = "Reading snapshots and the local copy…";
            this.FileHistoryShowButton.IsEnabled = false;

            try
            {
                var sessions = this.SessionsOf(link);
                var (versions, localText) = await Task.Run(() =>
                {
                    var lists = link.Snapshots.Select(s => (s, this.LoadSnapshotCached(link, s))).ToList();
                    string? md5 = null;
                    bool hashed = false;
                    string local = "not on disk";

                    if (Directory.Exists(link.Folder))
                    {
                        var info = new FileInfo(SyncPlan.LocalPath(link.Folder, name));

                        if (info.Exists)
                        {
                            var entry = VerifiedLedger.Load(link.Folder).Current(name, info);
                            md5 = entry?.Md5;
                            hashed = entry?.Md5Verified == true;
                            local = entry == null ? $"on disk ({Format.Bytes(info.Length)}), never verified" : $"on disk, md5 {entry.Md5} ({(hashed ? "hashed" : "size check")}, {entry.Source})";
                        }
                    }
                    else
                    {
                        local = "destination folder not reachable";
                    }

                    return (LinkHistory.Build(name, lists, sessions, md5, hashed), local);
                });

                this.FileHistoryListView.ItemsSource = versions;
                int kept = versions.Count(v => v.HistoryPath != null);
                int distinct = versions.Where(v => v.Md5.Length > 0).Select(v => v.Md5.ToLowerInvariant()).Distinct().Count();
                this.FileHistoryInfoTextBlock.Text = versions.Count == 0
                    ? "This file isn't in any snapshot of this link."
                    : $"{Format.Plural(distinct, "distinct version")}" + (kept > 0 ? $", {kept} kept by archive.org" : string.Empty) + $". Local: {localText}.";
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                this.FileHistoryInfoTextBlock.Text = "Couldn't read the history: " + ex.Message;
            }
            finally
            {
                this.FileHistoryShowButton.IsEnabled = true;
            }
        }

        private void FileHistoryDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedLink is { } link && this.FileHistoryListView.SelectedItem is FileVersion { HistoryPath: { } path })
            {
                MainWindow.OpenWithShell($"https://archive.org/download/{Uri.EscapeDataString(link.Collection)}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}");
            }
            else
            {
                this.FileHistoryInfoTextBlock.Text = "Select a row whose source is an archive.org kept copy (~n~) to download it.";
            }
        }

        private void FileHistoryCopyButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = this.FileHistoryListView.SelectedItem is FileVersion one ? new[] { one } : (this.FileHistoryListView.ItemsSource as IEnumerable<FileVersion>)?.ToArray() ?? Array.Empty<FileVersion>();
            string text = string.Join(Environment.NewLine, rows.Select(v => string.Join('\t', v.When, v.Source, v.SizeText, v.Md5, v.Sha1, v.Uploaded, v.Local, v.Note)));

            if (text.Length > 0)
            {
                Clipboard.SetText(text);
            }
        }

        private void HistoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedItems().FirstOrDefault() is { } item && this.session is { } current)
            {
                this.ShowLink(current.Collection, current.Folder, this.FileHistoryTabItem);
                this.FileHistoryComboBox.Text = item.Name;
                this.FileHistoryShowButton_Click(sender, e);
            }
        }

        // ----- Check all, open, relocate, forget -----

        private async void CheckLinksButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.checkAll != null || this.LinksListBox.ItemsSource is not List<LinkItem> items)
            {
                return;
            }

            var targets = items.Where(i => i.Link != null).ToList();

            if (targets.Count == 0)
            {
                return;
            }

            using var cts = new CancellationTokenSource();
            this.checkAll = cts;
            this.CheckAllPanel.Visibility = Visibility.Visible;
            this.UpdateLinkButtons();
            int done = 0, changed = 0, problems = 0;
            var gate = new SemaphoreSlim(4);

            void Progress(string name)
            {
                this.CheckAllProgressBar.Value = done * 1000.0 / targets.Count;
                this.CheckAllTextBlock.Text = $"{done} / {targets.Count} checked · {changed} changed · {problems} missing or unreachable · {name}";
            }

            Progress(string.Empty);

            async Task CheckOne(LinkItem item)
            {
                await gate.WaitAsync(cts.Token);

                try
                {
                    var link = item.Link!;
                    var check = await MainWindow.CheckLinkAsync(link, cts.Token);
                    var saved = await Task.Run(() =>
                    {
                        lock (LinkStore.Gate)
                        {
                            // Re-read: a Sync may have saved the link since the list was built. Links known only from
                            // runs are saved too, so the result survives a restart (their first Sync still seeds snapshot #0).
                            var fresh = LinkStore.Find(link.Collection, link.Folder) ?? link;
                            fresh.LastCheck = check;
                            LinkStore.Save(fresh);
                            return fresh;
                        }
                    });

                    item.Update(saved, check);
                    done++;
                    changed += item.Glyph == "▲" ? 1 : 0;
                    problems += item.Glyph is "✖" or "⚠" ? 1 : 0;
                    Progress(link.Collection);
                }
                finally
                {
                    gate.Release();
                }
            }

            try
            {
                await Task.WhenAll(targets.Select(CheckOne));
                this.CheckAllTextBlock.Text = $"Checked {targets.Count}: {changed} changed on archive.org, {problems} missing or unreachable.";
            }
            catch (OperationCanceledException)
            {
                this.CheckAllTextBlock.Text = $"Cancelled after {done} of {targets.Count}.";
            }
            finally
            {
                this.checkAll = null;
                this.CheckAllProgressBar.Value = 0;
                this.UpdateLinkButtons();
                this.LoadLinkDetails();
            }
        }

        /// <summary>Asks archive.org when the item last changed and whether the destination folder still exists (each capped at 20 s).</summary>
        private static async Task<LinkCheck> CheckLinkAsync(LinkRecord link, CancellationToken ct)
        {
            var check = new LinkCheck { Checked = DateTime.Now };
            var exists = Task.Run(() => Directory.Exists(link.Folder));

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                check.RemoteUpdated = await DownloadEngine.FetchItemLastUpdatedAsync(link.Collection, timeout.Token);
                check.ItemMissing = check.RemoteUpdated == null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
                check.Error = ex is OperationCanceledException ? "archive.org didn't answer in 20 s" : ex.Message;
            }

            if (await Task.WhenAny(exists, Task.Delay(TimeSpan.FromSeconds(20), ct)) == exists)
            {
                check.FolderMissing = !exists.Result;
            }

            return check;
        }

        private void CheckAllCancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.checkAll?.Cancel();
        }

        private void LinkOpenButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedLink is not { } link || this.running || this.syncWindow != null)
            {
                return;
            }

            this.UrlTextBox.Text = link.Url.Length > 0 ? link.Url : $"https://archive.org/details/{link.Collection}";
            this.FolderTextBox.Text = link.Folder;
            this.Tabs.SelectedIndex = 0;
        }

        /// <summary>Unloads the Sync tab's list when it belongs to a link that was just moved or forgotten.</summary>
        private void DropLoadedListOf(LinkRecord link)
        {
            if (this.session is { } current && current.Collection.Equals(link.Collection, StringComparison.OrdinalIgnoreCase) && MainWindow.SameFolderSafe(current.Folder, link.Folder))
            {
                this.session = null;
                this.LoadItems(Array.Empty<FileItem>());
                this.Tick();
            }
        }

        private void LinkRelocateButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedLink is not { } link || this.running || this.syncWindow != null)
            {
                return;
            }

            var dialog = new CommonOpenFileDialog { Title = $"Where are the files of {link.Collection} now?", IsFolderPicker = true, EnsurePathExists = true };

            if (dialog.ShowDialog(this) != CommonFileDialogResult.Ok)
            {
                return;
            }

            string target = dialog.FileName ?? string.Empty;
            var sessions = this.SessionsOf(link);

            if (MainWindow.SameFolderSafe(target, link.Folder)
                || MessageBox.Show(
                    this,
                    $"Point {link.Collection} at\n{target}\ninstead of\n{link.Folder}?\n\nIts {Format.Plural(link.Snapshots.Count, "snapshot")}, {Format.Plural(sessions.Count, "run")} and verified files move with it. No file is moved or changed; move the files yourself first.",
                    "Relocate Link",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            try
            {
                this.DropLoadedListOf(link);
                string oldFolder = link.Folder;
                LinkStore.Relocate(link, target, sessions);

                if (MainWindow.SameFolderSafe(this.FolderTextBox.Text.Trim(), oldFolder))
                {
                    this.FolderTextBox.Text = target;
                }

                this.SetStatus($"{link.Collection} now points at {target}.", LogLevel.Success);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, ex.Message, "Relocate Link", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            this.RefreshLinks();
        }

        private void LinkForgetButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedLink is not { } link || this.running || this.syncWindow != null)
            {
                return;
            }

            var sessions = this.SessionsOf(link);

            if (MessageBox.Show(
                    this,
                    $"Forget {link.Collection} → {link.Folder}?\n\nThis deletes its {Format.Plural(link.Snapshots.Count, "snapshot")} and {Format.Plural(sessions.Count, "run")} (history and logs). Downloaded files and the record of verified files are kept, so a later Sync or Start won't re-hash them.",
                    "Forget Link",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }

            this.DropLoadedListOf(link);
            LinkStore.Forget(link);

            foreach (var record in sessions)
            {
                SessionStore.Delete(record);
            }

            this.SetStatus($"Forgot {link.Collection} → {link.Folder}.", LogLevel.Info);
            this.RefreshLinks();
            this.RefreshLinkInfo(checkOnline: false);
        }

        /// <summary>A row of the Links list; the first row (no link) stands for all runs.</summary>
        public sealed class LinkItem : INotifyPropertyChanged
        {
            private static readonly Brush Green = Freeze(Brushes.ForestGreen);
            private static readonly Brush Orange = Freeze(Brushes.DarkOrange);
            private static readonly Brush Red = Freeze(Brushes.Firebrick);
            private static readonly Brush Grey = Freeze(Brushes.Gray);

            public LinkItem(LinkRecord link, int runs, DateTime lastRun)
            {
                this.Link = link;
                this.Runs = runs;
                this.LastRun = lastRun == default ? null : lastRun;
                this.Title = link.Collection;
                this.Folder = link.Folder;
                this.Refresh();
            }

            private LinkItem()
            {
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public LinkRecord? Link { get; private set; }

            public int Runs { get; }

            public DateTime? LastRun { get; }

            public string Title { get; private set; } = string.Empty;

            public string Folder { get; private set; } = string.Empty;

            public string Glyph { get; private set; } = string.Empty;

            public Brush GlyphBrush { get; private set; } = Grey;

            public string Status { get; private set; } = string.Empty;

            public static LinkItem AllRuns(int count) => new() { Title = "All runs", Folder = "every collection and folder", Glyph = "☰", Status = Format.Plural(count, "run") };

            public void Update(LinkRecord link, LinkCheck check)
            {
                link.LastCheck = check;
                this.Link = link;
                this.Refresh();
            }

            private static Brush Freeze(Brush brush)
            {
                var copy = brush.Clone();
                copy.Freeze();
                return copy;
            }

            private void Refresh()
            {
                var (glyph, status) = LinkHistory.Describe(this.Link!, this.LastRun);
                this.Glyph = glyph;
                this.GlyphBrush = glyph switch { "●" => Green, "▲" or "⚠" => Orange, "✖" => Red, _ => Grey };
                this.Status = $"{status} · {Format.Plural(this.Link!.Snapshots.Count, "snapshot")}, {Format.Plural(this.Runs, "run")}";
                this.OnPropertyChanged(nameof(this.Glyph));
                this.OnPropertyChanged(nameof(this.GlyphBrush));
                this.OnPropertyChanged(nameof(this.Status));
            }

            private void OnPropertyChanged([CallerMemberName] string? name = null)
            {
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        public sealed class SnapshotRow : INotifyPropertyChanged
        {
            private string delta = "…";

            public SnapshotRow(int number, SnapshotInfo info)
            {
                this.Number = number;
                this.Info = info;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public int Number { get; }

            public SnapshotInfo Info { get; }

            public string When => this.Info.WhenText;

            public string Source => this.Info.Source;

            public int Files => this.Info.FileCount;

            public string Size => Format.Bytes(this.Info.TotalSize);

            public string Delta
            {
                get => this.delta;
                set
                {
                    this.delta = value;
                    this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.Delta)));
                }
            }
        }

        /// <summary>A side of the diff: a snapshot, or archive.org now when <see cref="Info"/> is null.</summary>
        public sealed class SnapshotChoice
        {
            public SnapshotChoice(string label, SnapshotInfo? info)
            {
                this.Label = label;
                this.Info = info;
            }

            public static SnapshotChoice Live { get; } = new("archive.org now (live)", null);

            public string Label { get; }

            public SnapshotInfo? Info { get; }
        }
    }
}
