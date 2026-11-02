namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Data;
    using System.Windows.Media;
    using System.Windows.Shell;
    using System.Windows.Threading;

    using Microsoft.WindowsAPICodePack.Dialogs;

    public partial class MainWindow
    {
        private const int MaxLogEntries = 50000;

        private static readonly TimeSpan SessionSaveInterval = TimeSpan.FromSeconds(10);

        private readonly ObservableCollection<LogEntry> logEntries = new();
        private readonly ListCollectionView logView;
        private readonly SyncLog log = new();
        private readonly DispatcherTimer timer;
        private readonly Settings settings;
        private readonly Stopwatch runClock = new();

        private ObservableCollection<FileItem> items = new();
        private ListCollectionView itemsView;
        private SessionRecord? session;
        private DownloadEngine? engine;
        private CancellationTokenSource? cancellation;
        private bool running;
        private bool stopRequested;
        private bool closeAfterRun;
        private DateTime lastTick = DateTime.UtcNow;
        private DateTime lastSave = DateTime.UtcNow;
        private long lastBytesReceived;
        private double overallSpeed;
        private long sessionBaseBytes;
        private double sessionBaseSeconds;

        public MainWindow()
        {
            this.InitializeComponent();

            this.itemsView = this.CreateItemsView(this.items);
            this.logView = new ListCollectionView(this.logEntries) { Filter = this.FilterLogEntry };
            this.LogListView.ItemsSource = this.logView;

            this.settings = Settings.Load();
            this.UrlTextBox.Text = this.settings.Url;
            this.FolderTextBox.Text = this.settings.Folder;
            this.ApplyOptions(this.settings.Options);

            this.VersionTextBlock.Text = $"Version {MainWindow.VersionText()}";
            this.RefreshSessions();

            this.timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => this.Tick(), this.Dispatcher);
            this.timer.Start();

            this.UpdateButtons();
            this.UrlTextBox.Focus();
        }

        public static string? ParseCollectionName(string url)
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
                || !uri.Host.EndsWith("archive.org", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length < 2
                || !(segments[0].Equals("details", StringComparison.OrdinalIgnoreCase) || segments[0].Equals("download", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return Uri.UnescapeDataString(segments[1]);
        }

        private static string VersionText()
        {
            string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.3";
            int plus = version.IndexOf('+');

            // "1.3.0+<full git sha>" -> "1.3.0 (abc1234)"
            return plus >= 0 && version.Length > plus + 8 ? $"{version[..plus]} ({version.Substring(plus + 1, 7)})" : version;
        }

        private static void OpenWithShell(string path)
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        private ListCollectionView CreateItemsView(ObservableCollection<FileItem> source)
        {
            var view = new ListCollectionView(source) { Filter = this.FilterItem, IsLiveFiltering = true };
            view.LiveFilteringProperties.Add(nameof(FileItem.Status));
            view.LiveFilteringProperties.Add(nameof(FileItem.IsChecked));
            this.FilesDataGrid.ItemsSource = view;
            return view;
        }

        private void LoadItems(IEnumerable<FileItem> source)
        {
            this.items = new ObservableCollection<FileItem>(source);
            this.itemsView = this.CreateItemsView(this.items);
        }

        private void ApplyOptions(SyncOptions options)
        {
            this.FilesNumericUpDown.Value = Math.Clamp(options.FilesInParallel, this.FilesNumericUpDown.MinValue, this.FilesNumericUpDown.MaxValue);
            this.ConnectionsNumericUpDown.Value = Math.Clamp(options.ConnectionsPerFile, this.ConnectionsNumericUpDown.MinValue, this.ConnectionsNumericUpDown.MaxValue);
            this.VerifyComboBox.SelectedIndex = options.Verify == VerifyMode.SizeOnly ? 1 : 0;
            this.OriginalsCheckBox.IsChecked = options.OriginalsOnly;
            this.DeleteFilesCheckBox.IsChecked = options.DeleteExtraFiles;
        }

        private SyncOptions ReadOptions()
        {
            return new SyncOptions
            {
                FilesInParallel = this.FilesNumericUpDown.Value,
                ConnectionsPerFile = this.ConnectionsNumericUpDown.Value,
                Verify = this.VerifyComboBox.SelectedIndex == 1 ? VerifyMode.SizeOnly : VerifyMode.Md5,
                OriginalsOnly = this.OriginalsCheckBox.IsChecked == true,
                DeleteExtraFiles = this.DeleteFilesCheckBox.IsChecked == true
            };
        }

        private void SaveSettings()
        {
            this.settings.Url = this.UrlTextBox.Text.Trim();
            this.settings.Folder = this.FolderTextBox.Text.Trim();
            this.settings.Options = this.ReadOptions();
            this.settings.Save();
        }

        private void PasteButton_Click(object sender, RoutedEventArgs e)
        {
            this.UrlTextBox.Text = Clipboard.GetText();
            this.UrlTextBox.Focus();
            this.UrlTextBox.SelectAll();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            string? folder = string.IsNullOrWhiteSpace(this.FolderTextBox.Text) || !Directory.Exists(this.FolderTextBox.Text) ? null : this.FolderTextBox.Text;

            var dialog = new CommonOpenFileDialog
            {
                Title = "Browse for Destination Folder",
                IsFolderPicker = true,
                AddToMostRecentlyUsedList = false,
                AllowNonFileSystemItems = false,
                EnsureFileExists = true,
                EnsurePathExists = true,
                EnsureReadOnly = false,
                EnsureValidNames = true,
                Multiselect = false,
                ShowPlacesList = true,
                InitialDirectory = folder,
                DefaultDirectory = folder
            };

            if (dialog.ShowDialog(this) != CommonFileDialogResult.Ok)
            {
                return;
            }

            this.FolderTextBox.Text = dialog.FileName;
            this.FolderTextBox.Focus();
            this.FolderTextBox.SelectAll();
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (this.IsLoaded)
            {
                this.UpdateButtons();
            }
        }

        private static bool SameFolder(string a, string b)
        {
            return Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Validates the URL and folder boxes, showing an error when they are unusable.</summary>
        private bool TryReadTarget(out string url, out string collectionName, out string folder)
        {
            url = this.UrlTextBox.Text.Trim();
            folder = this.FolderTextBox.Text.Trim();
            collectionName = MainWindow.ParseCollectionName(url) ?? string.Empty;

            if (collectionName.Length == 0)
            {
                MessageBox.Show(this, "Incorrect Archive.org Collection URL. A URL in the format of https://archive.org/details/<collection> or https://archive.org/download/<collection> is expected.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!Directory.Exists(folder))
            {
                MessageBox.Show(this, "Destination folder does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            return true;
        }

        /// <summary>Gets a value indicating whether the loaded file list belongs to this collection and folder.</summary>
        private bool HasListFor(string collectionName, string folder)
        {
            return this.session != null
                && this.items.Count > 0
                && this.session.Collection.Equals(collectionName, StringComparison.OrdinalIgnoreCase)
                && MainWindow.SameFolder(this.session.Folder, folder);
        }

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.running || !this.TryReadTarget(out string url, out string collectionName, out string folder))
            {
                return;
            }

            this.SaveSettings();
            this.session = SessionStore.Create(url, collectionName, folder, this.ReadOptions());
            this.LoadItems(Array.Empty<FileItem>());
            this.logEntries.Clear();

            await this.RunAsync(true, _ => false, process: false);
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.running || !this.TryReadTarget(out string url, out string collectionName, out string folder))
            {
                return;
            }

            this.SaveSettings();
            var options = this.ReadOptions();
            bool verifyAll = sender == this.VerifyAllButton;

            // A list for this collection and folder is already on screen (after Scan, a pause or an opened session):
            // work on its ticked files instead of scanning again, so the user's picks are kept.
            if (this.HasListFor(collectionName, folder) && this.session is { } loaded)
            {
                loaded.Options = options;

                if (verifyAll)
                {
                    foreach (var item in this.items.Where(i => i.IsChecked))
                    {
                        item.Restore(FileStatus.Queued, null);
                    }

                    await this.RunAsync(false, i => i.IsChecked && i.CurrentStatus == FileStatus.Queued, reverifyAll: true);
                    return;
                }

                if (this.items.Any(i => i.IsChecked && i.CurrentStatus is FileStatus.Queued or FileStatus.Failed))
                {
                    await this.RunAsync(false, i => i.IsChecked && i.CurrentStatus is FileStatus.Failed or FileStatus.Queued);
                    return;
                }

                // Everything ticked is finished: fall through to a fresh scan, which picks up new files.
            }

            if (!verifyAll && this.FindUnfinishedSession(collectionName, folder) is { } unfinished)
            {
                int failed = unfinished.FailedCount;
                int notFinished = unfinished.Files.Count(f => f.Checked && f.Status is not (FileStatus.Done or FileStatus.Skipped or FileStatus.Failed));
                var answer = MessageBox.Show(
                    this,
                    $"An unfinished session for {collectionName} in this folder exists (started {unfinished.WhenText}): {failed} failed, {notFinished} not finished.\n\n"
                    + "Yes: continue it (retry failed files, resume partial downloads).\n"
                    + "No: start a new scan (files verified earlier are still skipped without re-checking).\n"
                    + "Cancel: do nothing.",
                    "Continue Session?",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (answer == MessageBoxResult.Cancel)
                {
                    return;
                }

                if (answer == MessageBoxResult.Yes && this.OpenSession(unfinished))
                {
                    // Keep the options currently chosen in the UI rather than the ones saved with the session.
                    this.ApplyOptions(options);
                    unfinished.Options = options;
                    await this.RunAsync(false, i => i.IsChecked && i.CurrentStatus is FileStatus.Failed or FileStatus.Queued);
                    return;
                }
            }

            this.session = SessionStore.Create(url, collectionName, folder, options);
            this.LoadItems(Array.Empty<FileItem>());
            this.logEntries.Clear();

            await this.RunAsync(true, i => i.IsChecked && i.CurrentStatus == FileStatus.Queued, verifyAll);
        }

        private void VerifyAllButton_Click(object sender, RoutedEventArgs e)
        {
            this.StartButton_Click(sender, e);
        }

        private SessionRecord? FindUnfinishedSession(string collection, string folder)
        {
            // Only the latest session for this collection and folder counts; an older one superseded by a newer scan
            // shouldn't keep asking. Unticked files don't make a session unfinished.
            var latest = MainWindow.LatestSession(collection, folder);
            return latest != null && latest.IsUnfinished ? latest : null;
        }

        private static SessionRecord? LatestSession(string collection, string folder, string? exceptId = null)
        {
            return SessionStore.LoadAll().FirstOrDefault(r =>
                r.Id != exceptId
                && r.Collection.Equals(collection, StringComparison.OrdinalIgnoreCase)
                && MainWindow.SameFolder(r.Folder, folder));
        }

        private async void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.running)
            {
                this.SetStatus("Pausing… (partial downloads are kept and resume where they stopped)");
                this.cancellation?.Cancel();
                return;
            }

            await this.RunAsync(false, i => i.IsChecked && i.CurrentStatus == FileStatus.Queued);
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            this.stopRequested = true;
            this.SetStatus("Stopping…");
            this.cancellation?.Cancel();
        }

        private async void RetryFailedButton_Click(object sender, RoutedEventArgs e)
        {
            await this.RunAsync(false, i => i.IsChecked && i.CurrentStatus is FileStatus.Failed or FileStatus.Queued);
        }

        /// <summary>
        /// Runs the current session: optionally scans the collection first, then downloads the items matching
        /// <paramref name="selector"/> (skipped when <paramref name="process"/> is false, for Scan). Pause, Stop and
        /// errors all end here with the session saved.
        /// </summary>
        private async Task RunAsync(bool scan, Func<FileItem, bool> selector, bool reverifyAll = false, bool process = true)
        {
            if (this.running || this.session == null)
            {
                return;
            }

            var current = this.session;
            this.running = true;
            this.stopRequested = false;
            this.cancellation = new CancellationTokenSource();
            var ledger = VerifiedLedger.Load(current.Folder);
            this.engine = new DownloadEngine(current.Options, this.log, ledger, reverifyAll);
            this.lastBytesReceived = 0;
            this.overallSpeed = 0;
            this.sessionBaseBytes = current.BytesDownloaded;
            this.sessionBaseSeconds = current.ActiveSeconds;
            this.runClock.Restart();
            current.State = SessionState.Running;
            current.Ended = null;
            this.log.Open(SessionStore.LogPath(current));
            this.log.Write(
                LogLevel.Info,
                !process ? "Scan: listing files only; tick the ones you want, then press Start."
                : reverifyAll
                    ? "Verify All: every existing file is MD5-checked again, ignoring earlier verifications."
                    : $"{ledger.Count} file(s) in this folder were verified in earlier runs; unchanged ones are skipped without re-checking.");
            this.UpdateButtons();

            var token = this.cancellation.Token;
            bool failed = false;

            try
            {
                if (scan)
                {
                    this.SetStatus($"Scanning archive.org collection {current.Collection}…");
                    this.log.Write(LogLevel.Info, $"Scanning collection {current.Collection} → {current.Folder}");

                    File[] files = await DownloadEngine.FetchFileListAsync(current.Collection, token);
                    var wanted = current.Options.OriginalsOnly
                        ? files.Where(f => !f.OldVersion && (f.Source.Length == 0 || f.Source.Equals("original", StringComparison.OrdinalIgnoreCase))).ToArray()
                        : files;

                    this.LoadItems(wanted.Select(f => new FileItem(f)));
                    this.RestoreChecks(current);
                    this.log.Write(
                        LogLevel.Info,
                        current.Options.OriginalsOnly
                            ? $"{files.Length} file(s) listed, {wanted.Length} original(s) selected ({Format.Bytes(wanted.Sum(f => f.Size))})."
                            : $"{files.Length} file(s) listed ({Format.Bytes(files.Sum(f => f.Size))}).");

                    // Scan alone never touches the disk.
                    if (process)
                    {
                        await this.DeleteExtraFilesAsync(current, this.engine, files, token);
                    }

                    current.Capture(this.items);
                    SessionStore.Save(current);
                    this.RefreshSessions();
                }

                if (process)
                {
                    await this.ProcessQueueAsync(current, this.engine, scan, selector, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failed = true;
                this.log.Write(LogLevel.Error, $"Unable to sync the collection. - {ex.Message}");
            }

            this.runClock.Stop();
            this.Tick();

            int done = this.items.Count(i => i.IsChecked && i.CurrentStatus == FileStatus.Done);
            int skipped = this.items.Count(i => i.IsChecked && i.CurrentStatus == FileStatus.Skipped);
            int failedFiles = this.items.Count(i => i.IsChecked && i.CurrentStatus == FileStatus.Failed);
            int queued = this.items.Count(i => i.IsChecked && i.CurrentStatus == FileStatus.Queued);
            int unticked = this.items.Count(i => !i.IsChecked);

            current.State = this.stopRequested || failed ? SessionState.Stopped
                : !process && !token.IsCancellationRequested ? SessionState.Scanned
                : token.IsCancellationRequested || queued > 0 ? SessionState.Paused
                : SessionState.Completed;
            current.Ended = DateTime.Now;
            current.ActiveSeconds = this.sessionBaseSeconds + this.runClock.Elapsed.TotalSeconds;
            current.BytesDownloaded = this.sessionBaseBytes + this.engine.BytesReceived;
            current.Capture(this.items);

            var level = failed || failedFiles > 0 ? LogLevel.Error
                : current.State == SessionState.Completed ? LogLevel.Success
                : current.State == SessionState.Scanned ? LogLevel.Info
                : LogLevel.Warn;
            string summary = current.State == SessionState.Scanned
                ? $"Scanned: {this.items.Count} file(s), {this.items.Count - unticked} ticked ({Format.Bytes(this.items.Where(i => i.IsChecked).Sum(i => i.Size))}). Untick what you don't want, then press Start."
                : $"{current.State}: {done} downloaded, {skipped} already up to date, {failedFiles} failed, {queued} queued"
                    + (unticked > 0 ? $", {unticked} unticked. " : ". ")
                    + $"{Format.Bytes(this.engine.BytesReceived)} in {Format.Duration(this.runClock.Elapsed)}"
                    + (this.engine.BytesReceived > 0 ? $" ({Format.Speed(this.engine.BytesReceived / Math.Max(1, this.runClock.Elapsed.TotalSeconds))})." : ".");

            this.log.Write(level, summary);
            this.log.Close();
            this.Tick();

            SessionStore.Save(current);
            this.running = false;
            this.cancellation.Dispose();
            this.cancellation = null;
            this.SetStatus(summary, level);
            this.RefreshSessions();
            this.UpdateButtons();

            if (this.closeAfterRun)
            {
                this.Close();
            }
        }

        private async Task ProcessQueueAsync(SessionRecord current, DownloadEngine engine, bool scan, Func<FileItem, bool> selector, CancellationToken token)
        {
            var queue = this.items.Where(selector).ToList();

            if (!scan && await this.RefreshFileListAsync(current, queue, token) is { } latest)
            {
                // Delete extra files applies however the run started (e.g. ticked after Scan, then Start).
                await this.DeleteExtraFilesAsync(current, engine, latest, token);
            }

            foreach (var item in queue.Where(i => i.CurrentStatus == FileStatus.Failed))
            {
                item.Restore(FileStatus.Queued, null);
            }

            this.SetStatus($"Syncing {queue.Count} file(s) — {current.Options.FilesInParallel} in parallel, up to {current.Options.ConnectionsPerFile} connection(s) each…");
            this.log.Write(LogLevel.Info, $"Processing {queue.Count} ticked file(s) with {current.Options.FilesInParallel} file(s) in parallel and {current.Options.ConnectionsPerFile} connection(s) per file.");
            await engine.RunAsync(current.Collection, current.Folder, queue, token);
        }

        /// <summary>
        /// After a scan, carries the ticks over from the previous session for this collection and folder, so files
        /// the user unticked stay unticked. Files new to the list stay ticked.
        /// </summary>
        private void RestoreChecks(SessionRecord current)
        {
            if (MainWindow.LatestSession(current.Collection, current.Folder, current.Id) is not { } previous)
            {
                return;
            }

            var unticked = previous.Files.Where(f => !f.Checked).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (unticked.Count == 0)
            {
                return;
            }

            int count = 0;

            foreach (var item in this.items.Where(i => unticked.Contains(i.Name)))
            {
                item.IsChecked = false;
                count++;
            }

            this.log.Write(LogLevel.Info, $"{count} file(s) left unticked as in the previous session ({previous.WhenText}). Use Check: All to include them.");
        }

        /// <summary>
        /// Re-reads the collection's file list before a resume or retry. Uploaders replace files and archive.org
        /// regenerates its metadata files, so the size and MD5 saved at scan time can be stale and would fail forever.
        /// </summary>
        /// <returns>The full fresh file list, or null when archive.org couldn't be reached.</returns>
        private async Task<File[]?> RefreshFileListAsync(SessionRecord current, List<FileItem> queue, CancellationToken token)
        {
            this.SetStatus("Refreshing the file list from archive.org…");
            var latest = new Dictionary<string, File>(StringComparer.OrdinalIgnoreCase);
            File[] files;

            try
            {
                files = await DownloadEngine.FetchFileListAsync(current.Collection, token);

                foreach (var file in files)
                {
                    latest.TryAdd(file.Name, file);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.log.Write(LogLevel.Warn, $"Could not refresh the file list; using the one saved with the session. - {ex.Message}");
                return null;
            }

            int changed = 0;
            int removed = 0;

            foreach (var item in queue.ToList())
            {
                if (!latest.TryGetValue(item.Name, out var file))
                {
                    item.Restore(FileStatus.Failed, "no longer in the collection");
                    queue.Remove(item);
                    this.log.Write(LogLevel.Warn, "No longer in the collection on archive.org; skipped.", item.Name);
                    removed++;
                }
                else if (item.UpdateFrom(file))
                {
                    this.log.Write(LogLevel.Info, $"Changed on archive.org since the scan; now {Format.Bytes(item.Size)}, using the new size and checksum.", item.Name);
                    changed++;
                }
            }

            this.log.Write(LogLevel.Info, $"File list refreshed: {changed} file(s) changed, {removed} no longer listed.");
            return files;
        }

        /// <summary>
        /// Deletes local files that aren't in the collection when that option is on. Uses the full listing, so files
        /// that are only unticked are never deleted.
        /// </summary>
        private async Task DeleteExtraFilesAsync(SessionRecord current, DownloadEngine engine, File[] files, CancellationToken token)
        {
            if (!current.Options.DeleteExtraFiles)
            {
                return;
            }

            this.SetStatus("Deleting files that are not in the collection…");
            var (deleted, errors) = await Task.Run(() => engine.DeleteExtraFiles(current.Folder, files), token);
            this.log.Write(errors > 0 ? LogLevel.Warn : LogLevel.Info, $"{deleted} file(s) deleted because they are not in the collection, {errors} could not be deleted.");
        }

        private void Tick()
        {
            var now = DateTime.UtcNow;
            double elapsed = (now - this.lastTick).TotalSeconds;
            this.lastTick = now;

            int queued = 0, active = 0, done = 0, skipped = 0, failed = 0, unticked = 0, visibleTicked = 0;
            long totalBytes = 0, doneBytes = 0;

            foreach (var item in this.items)
            {
                if (item.IsActive || item.Status != item.CurrentStatus)
                {
                    item.Refresh(elapsed);
                }

                // Unticked files are out of the job: they don't count towards the totals or the progress.
                if (!item.IsChecked && !item.IsActive)
                {
                    unticked++;
                    continue;
                }

                switch (item.Status)
                {
                    case FileStatus.Queued: queued++; break;
                    case FileStatus.Done: done++; break;
                    case FileStatus.Skipped: skipped++; break;
                    case FileStatus.Failed: failed++; break;
                    default: active++; break;
                }

                totalBytes += item.Size;
                doneBytes += Math.Min(item.Size, item.BytesDone);
            }

            this.FilterAll.Content = $"All ({this.items.Count})";
            this.FilterActive.Content = $"Active ({active})";
            this.FilterQueued.Content = $"Queued ({queued})";
            this.FilterDone.Content = $"Done ({done})";
            this.FilterSkipped.Content = $"Skipped ({skipped})";
            this.FilterFailed.Content = $"Failed ({failed})";
            this.FilterUnchecked.Content = $"Unchecked ({unticked})";
            this.CheckedTextBlock.Text = this.items.Count == 0 ? string.Empty : $"☑ {this.items.Count - unticked} of {this.items.Count} ticked · {Format.Bytes(totalBytes)}";

            foreach (FileItem item in this.itemsView)
            {
                visibleTicked += item.IsChecked ? 1 : 0;
            }

            this.HeaderCheckBox.IsChecked = this.itemsView.Count > 0 && visibleTicked == this.itemsView.Count;

            this.DrainLog();

            if (this.items.Count == 0)
            {
                this.SummaryTextBlock.Text = string.Empty;
                this.ProgressBar.Value = 0;
                this.Taskbar.ProgressState = TaskbarItemProgressState.None;
                return;
            }

            string speedText = string.Empty;
            string etaText = string.Empty;
            string connectionsText = string.Empty;

            if (this.running && this.engine != null && elapsed > 0)
            {
                long received = this.engine.BytesReceived;
                double instant = Math.Max(0, received - this.lastBytesReceived) / elapsed;
                this.lastBytesReceived = received;
                this.overallSpeed = this.overallSpeed == 0 ? instant : (this.overallSpeed * 0.8) + (instant * 0.2);
                speedText = " · " + (Format.Speed(this.overallSpeed) is { Length: > 0 } s ? s : "0 B/s");
                etaText = this.overallSpeed > 1 ? " · ETA " + Format.Duration(TimeSpan.FromSeconds((totalBytes - doneBytes) / this.overallSpeed)) : string.Empty;
                connectionsText = $" · {this.engine.ActiveConnections} connection(s)";

                if (now - this.lastSave > MainWindow.SessionSaveInterval && this.session != null)
                {
                    this.lastSave = now;
                    this.session.ActiveSeconds = this.sessionBaseSeconds + this.runClock.Elapsed.TotalSeconds;
                    this.session.BytesDownloaded = this.sessionBaseBytes + received;
                    this.session.Capture(this.items);
                    SessionStore.Save(this.session);
                }
            }

            int jobFiles = this.items.Count - unticked;
            this.SummaryTextBlock.Text = $"{done + skipped} / {jobFiles} files · {Format.Bytes(doneBytes)} / {Format.Bytes(totalBytes)}{speedText}{etaText}{connectionsText}";

            double fraction = totalBytes > 0 ? (double)doneBytes / totalBytes : jobFiles > 0 ? (double)(done + skipped) / jobFiles : 0;
            this.ProgressBar.Value = fraction * this.ProgressBar.Maximum;
            this.Taskbar.ProgressValue = fraction;
            this.Taskbar.ProgressState = !this.running ? TaskbarItemProgressState.None
                : failed > 0 ? TaskbarItemProgressState.Error
                : TaskbarItemProgressState.Normal;
            this.Title = this.running ? $"{fraction:0%} · Archive.org Bulk Downloader" : "Archive.org Bulk Downloader";
        }

        private void DrainLog()
        {
            LogEntry? lastVisible = null;

            foreach (var entry in this.log.Drain())
            {
                this.logEntries.Add(entry);

                if (this.FilterLogEntry(entry))
                {
                    lastVisible = entry;
                }
            }

            while (this.logEntries.Count > MainWindow.MaxLogEntries)
            {
                this.logEntries.RemoveAt(0);
            }

            if (lastVisible != null && this.AutoScrollCheckBox.IsChecked == true && this.LogListView.IsVisible)
            {
                this.LogListView.ScrollIntoView(lastVisible);
            }
        }

        private void SetStatus(string message, LogLevel level = LogLevel.Info)
        {
            this.StatusTextBlock.Text = message;
            this.StatusTextBlock.ToolTip = message;
            this.StatusTextBlock.Foreground = level switch
            {
                LogLevel.Error => Brushes.Red,
                LogLevel.Warn => Brushes.DarkOrange,
                LogLevel.Success => Brushes.Green,
                _ => SystemColors.ControlTextBrush
            };
        }

        private void UpdateButtons()
        {
            bool hasInput = this.FolderTextBox.Text.Trim().Length > 0 && this.UrlTextBox.Text.Trim().Length > 0;
            bool hasQueued = this.items.Any(i => i.IsChecked && i.CurrentStatus == FileStatus.Queued);
            bool hasFailed = this.items.Any(i => i.IsChecked && i.CurrentStatus == FileStatus.Failed);

            this.ScanButton.IsEnabled = !this.running && hasInput;
            this.StartButton.IsEnabled = !this.running && hasInput;
            this.VerifyAllButton.IsEnabled = !this.running && hasInput;
            bool canResume = !this.running && this.session != null && hasQueued;
            this.PauseButton.Content = canResume ? "Resume" : "Pause";
            this.PauseButton.IsEnabled = this.running || canResume;
            this.StopButton.IsEnabled = this.running;
            this.RetryFailedButton.IsEnabled = !this.running && this.session != null && hasFailed;
            this.RetrySelectedMenuItem.IsEnabled = !this.running && this.session != null;

            this.UrlTextBox.IsEnabled =
                this.FolderTextBox.IsEnabled =
                this.PasteButton.IsEnabled =
                this.BrowseButton.IsEnabled =
                this.OptionsPanel.IsEnabled =
                !this.running;

            this.UpdateSessionButtons();
        }

        private bool FilterItem(object value)
        {
            var item = (FileItem)value;

            bool statusMatches =
                this.FilterUnchecked.IsChecked == true ? !item.IsChecked
                : this.FilterActive.IsChecked == true ? item.IsActive
                : this.FilterQueued.IsChecked == true ? item.IsChecked && item.Status == FileStatus.Queued
                : this.FilterDone.IsChecked == true ? item.Status == FileStatus.Done
                : this.FilterSkipped.IsChecked == true ? item.Status == FileStatus.Skipped
                : this.FilterFailed.IsChecked != true || item.Status == FileStatus.Failed;

            string search = this.SearchTextBox.Text.Trim();
            return statusMatches && (search.Length == 0 || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        private void FileFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (this.IsLoaded)
            {
                this.itemsView.Refresh();
            }
        }

        private IEnumerable<FileItem> SelectedItems() => this.FilesDataGrid.SelectedItems.OfType<FileItem>();

        private void OpenFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var item = this.SelectedItems().FirstOrDefault();

            if (item == null || this.session == null)
            {
                return;
            }

            string path = Path.Combine(this.session.Folder, item.Name.Replace('/', Path.DirectorySeparatorChar));

            if (System.IO.File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else if (Directory.Exists(Path.GetDirectoryName(path)))
            {
                MainWindow.OpenWithShell(Path.GetDirectoryName(path)!);
            }
        }

        private void CopyNameMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string text = string.Join(Environment.NewLine, this.SelectedItems().Select(i => i.Name));

            if (text.Length > 0)
            {
                Clipboard.SetText(text);
            }
        }

        private void CopyUrlMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (this.session == null)
            {
                return;
            }

            string collection = Uri.EscapeDataString(this.session.Collection);
            string text = string.Join(
                Environment.NewLine,
                this.SelectedItems().Select(i => $"https://archive.org/download/{collection}/{string.Join('/', i.Name.Split('/').Select(Uri.EscapeDataString))}"));

            if (text.Length > 0)
            {
                Clipboard.SetText(text);
            }
        }

        private async void RetrySelectedMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selected = this.SelectedItems().ToHashSet();

            if (this.running || selected.Count == 0)
            {
                return;
            }

            foreach (var item in selected)
            {
                item.IsChecked = true;

                if (item.CurrentStatus != FileStatus.Queued)
                {
                    item.Restore(FileStatus.Queued, null);
                }
            }

            await this.RunAsync(false, selected.Contains);
        }

        // ----- Ticking files -----

        private void SetChecked(IEnumerable<FileItem> files, bool value)
        {
            foreach (var item in files.ToList())
            {
                item.IsChecked = value;
            }

            this.UpdateButtons();
            this.Tick();
        }

        /// <summary>Ticks exactly <paramref name="keep"/> and unticks everything else.</summary>
        private void CheckOnly(IEnumerable<FileItem> keep)
        {
            var set = keep.ToHashSet();

            foreach (var item in this.items)
            {
                item.IsChecked = set.Contains(item);
            }

            this.UpdateButtons();
            this.Tick();
        }

        /// <summary>Space and the row checkbox act on all highlighted rows: tick them all unless all are ticked.</summary>
        private void ToggleHighlighted(FileItem? clicked = null)
        {
            var targets = this.SelectedItems().ToList();

            if (clicked != null && !targets.Contains(clicked))
            {
                targets = new List<FileItem> { clicked };
            }

            if (targets.Count > 0)
            {
                bool value = clicked != null ? !clicked.IsChecked : targets.Any(i => !i.IsChecked);
                this.SetChecked(targets, value);
            }
        }

        private void CheckAllButton_Click(object sender, RoutedEventArgs e) => this.SetChecked(this.items, true);

        private void CheckNoneButton_Click(object sender, RoutedEventArgs e) => this.SetChecked(this.items, false);

        private void CheckInvertButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in this.items)
            {
                item.IsChecked = !item.IsChecked;
            }

            this.UpdateButtons();
            this.Tick();
        }

        private void CheckVisibleButton_Click(object sender, RoutedEventArgs e) => this.CheckOnly(this.itemsView.OfType<FileItem>());

        private void CheckHighlightedButton_Click(object sender, RoutedEventArgs e)
        {
            var highlighted = this.SelectedItems().ToList();

            if (highlighted.Count > 0)
            {
                this.CheckOnly(highlighted);
            }
        }

        private void CheckMissingButton_Click(object sender, RoutedEventArgs e)
        {
            string folder = this.session?.Folder ?? this.FolderTextBox.Text.Trim();

            if (!Directory.Exists(folder))
            {
                return;
            }

            // Size check only, so it stays instant on big folders; Verify All does the MD5 pass.
            this.CheckOnly(this.items.Where(i =>
            {
                if (i.CurrentStatus is FileStatus.Done or FileStatus.Skipped)
                {
                    return false;
                }

                var info = new FileInfo(Path.Combine(folder, i.Name.Replace('/', Path.DirectorySeparatorChar)));
                return !info.Exists || (i.HasSize && info.Length != i.Size) || System.IO.File.Exists(info.FullName + ".part");
            }));
        }

        private void HeaderCheckBox_Click(object sender, RoutedEventArgs e)
        {
            var visible = this.itemsView.OfType<FileItem>().ToList();
            this.SetChecked(visible, visible.Any(i => !i.IsChecked));
        }

        private void CheckHighlightedMenuItem_Click(object sender, RoutedEventArgs e) => this.SetChecked(this.SelectedItems(), true);

        private void UncheckHighlightedMenuItem_Click(object sender, RoutedEventArgs e) => this.SetChecked(this.SelectedItems(), false);

        private void FilesDataGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Space)
            {
                this.ToggleHighlighted();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Handles clicks on a row checkbox here so that, with several rows highlighted, one click ticks or unticks
        /// them all and the highlight survives (the grid would otherwise collapse it to the clicked row).
        /// </summary>
        private void FilesDataGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            DependencyObject? node = e.OriginalSource as DependencyObject;

            while (node != null && node is not CheckBox && node != this.FilesDataGrid)
            {
                node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }

            if (node is not CheckBox { Tag: "RowCheck", DataContext: FileItem clicked })
            {
                return;
            }

            var highlighted = this.SelectedItems().ToList();
            this.ToggleHighlighted(clicked);
            e.Handled = true;

            if (highlighted.Contains(clicked) && highlighted.Count > 1)
            {
                // The cell still reacts to the (handled) click and selects its row; put the highlight back.
                this.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    this.FilesDataGrid.SelectedItems.Clear();

                    foreach (var item in highlighted)
                    {
                        this.FilesDataGrid.SelectedItems.Add(item);
                    }
                });
            }
        }

        // ----- Sessions tab -----

        private SessionRecord? SelectedSession => this.SessionsListView.SelectedItem as SessionRecord;

        private void RefreshSessions()
        {
            string? selectedId = this.SelectedSession?.Id;
            var records = SessionStore.LoadAll();
            this.SessionsListView.ItemsSource = records;
            this.SessionsListView.SelectedItem = records.FirstOrDefault(r => r.Id == selectedId);
            this.UpdateSessionButtons();
        }

        private void UpdateSessionButtons()
        {
            var record = this.SelectedSession;
            bool canLoad = record != null && !this.running;

            this.SessionOpenButton.IsEnabled = canLoad;
            this.SessionResumeButton.IsEnabled = canLoad && record!.HasQueued;
            this.SessionRetryButton.IsEnabled = canLoad && record!.FailedCount > 0;
            this.SessionRerunButton.IsEnabled = canLoad;
            this.SessionExportButton.IsEnabled = record != null;
            this.SessionLogButton.IsEnabled = record != null && System.IO.File.Exists(SessionStore.LogPath(record));
            this.SessionDeleteButton.IsEnabled = canLoad && record!.Id != this.session?.Id;
        }

        private void SessionsTabItem_Selected(object sender, RoutedEventArgs e)
        {
            if (!this.running)
            {
                this.RefreshSessions();
            }
        }

        private void SessionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            this.UpdateSessionButtons();
        }

        private void SessionsListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            this.SessionOpenButton_Click(sender, e);
        }

        private bool OpenSession(SessionRecord record)
        {
            if (this.running)
            {
                return false;
            }

            this.session = record;
            this.UrlTextBox.Text = record.Url;
            this.FolderTextBox.Text = record.Folder;
            this.ApplyOptions(record.Options);
            this.LoadItems(record.Restore());

            this.logEntries.Clear();
            string logPath = SessionStore.LogPath(record);

            if (System.IO.File.Exists(logPath))
            {
                try
                {
                    using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);

                    while (reader.ReadLine() is { } line)
                    {
                        if (LogEntry.Parse(line) is { } entry)
                        {
                            this.logEntries.Add(entry);
                        }
                    }
                }
                catch (IOException)
                {
                }
            }

            this.SetStatus($"Opened session {record.Id} ({record.ResultText}).");
            this.Tick();
            this.UpdateButtons();
            this.Tabs.SelectedIndex = 0;
            return true;
        }

        private void SessionOpenButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedSession is { } record)
            {
                this.OpenSession(record);
            }
        }

        private async void SessionResumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedSession is { } record && this.OpenSession(record))
            {
                await this.RunAsync(false, i => i.IsChecked && i.CurrentStatus == FileStatus.Queued);
            }
        }

        private async void SessionRetryButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedSession is { } record && this.OpenSession(record))
            {
                await this.RunAsync(false, i => i.IsChecked && i.CurrentStatus is FileStatus.Failed or FileStatus.Queued);
            }
        }

        private void SessionRerunButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedSession is not { } record || this.running)
            {
                return;
            }

            this.UrlTextBox.Text = record.Url;
            this.FolderTextBox.Text = record.Folder;
            this.ApplyOptions(record.Options);
            this.Tabs.SelectedIndex = 0;
            this.StartButton_Click(sender, e);
        }

        private void SessionExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedSession is not { } record)
            {
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = record.Id + ".csv",
                Filter = "CSV files (*.csv)|*.csv",
                DefaultExt = ".csv"
            };

            if (dialog.ShowDialog(this) == true)
            {
                SessionStore.ExportCsv(record, dialog.FileName);
            }
        }

        private void SessionLogButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedSession is { } record && System.IO.File.Exists(SessionStore.LogPath(record)))
            {
                MainWindow.OpenWithShell(SessionStore.LogPath(record));
            }
        }

        private void SessionDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.SelectedSession is not { } record
                || MessageBox.Show(this, $"Delete session {record.Id} and its log? Downloaded files are not touched.", "Delete Session", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            SessionStore.Delete(record);
            this.RefreshSessions();
        }

        private void SessionsFolderButton_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(SessionStore.Folder);
            MainWindow.OpenWithShell(SessionStore.Folder);
        }

        // ----- Log tab -----

        private bool FilterLogEntry(object value)
        {
            var entry = (LogEntry)value;

            bool levelVisible = entry.Level switch
            {
                LogLevel.Debug => this.LogDebugToggle.IsChecked == true,
                LogLevel.Warn => this.LogWarnToggle.IsChecked == true,
                LogLevel.Error => this.LogErrorToggle.IsChecked == true,
                _ => this.LogInfoToggle.IsChecked == true
            };

            string search = this.LogSearchTextBox.Text.Trim();
            return levelVisible
                && (search.Length == 0
                    || entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || entry.File.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        private void LogFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (this.IsLoaded)
            {
                this.logView.Refresh();
            }
        }

        private void LogTabItem_Selected(object sender, RoutedEventArgs e)
        {
            if (this.AutoScrollCheckBox.IsChecked == true && this.logView.Count > 0)
            {
                this.LogListView.ScrollIntoView(this.logView.GetItemAt(this.logView.Count - 1));
            }
        }

        private void LogCopyButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = this.LogListView.SelectedItems.Count > 0
                ? this.LogListView.SelectedItems.OfType<LogEntry>().OrderBy(this.logEntries.IndexOf)
                : this.logView.OfType<LogEntry>();

            string text = string.Join(Environment.NewLine, selected.Select(l => l.ToString()));

            if (text.Length > 0)
            {
                Clipboard.SetText(text);
            }
        }

        private void LogSaveButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = (this.session?.Id ?? "log") + ".log",
                Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt",
                DefaultExt = ".log"
            };

            if (dialog.ShowDialog(this) == true)
            {
                System.IO.File.WriteAllLines(dialog.FileName, this.logView.OfType<LogEntry>().Select(l => l.ToString()));
            }
        }

        private void LogClearButton_Click(object sender, RoutedEventArgs e)
        {
            this.logEntries.Clear();
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            MainWindow.OpenWithShell(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            this.SaveSettings();

            if (!this.running)
            {
                // Keep the ticks the user changed since the last run.
                if (this.session != null && this.items.Count > 0)
                {
                    this.session.Capture(this.items);
                    SessionStore.Save(this.session);
                }

                return;
            }

            if (MessageBox.Show(this, "A sync is running. Pause it and exit? Partial downloads are kept and you can resume from the Sessions tab.", "Exit", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            // Let RunAsync save the session as paused, then close from there.
            e.Cancel = true;
            this.closeAfterRun = true;
            this.SetStatus("Pausing before exit…");
            this.cancellation?.Cancel();
        }
    }
}
