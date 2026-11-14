namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Windows;

    /// <summary>
    /// The last step before Sync touches the disk: warnings first, then every rename, move and download in a
    /// scrolling list (never a message box, however long it gets).
    /// </summary>
    public partial class ConfirmSyncWindow
    {
        public ConfirmSyncWindow(string folder, SyncOptions options, IReadOnlyList<SyncRow> rows, int olderCount)
        {
            this.InitializeComponent();
            var ticked = rows.Where(r => r.IsChecked).ToList();
            var warnings = new List<string>();

            void Warn(IEnumerable<SyncRow> matching, Func<int, string> text)
            {
                var list = matching.ToList();

                if (list.Count > 0)
                {
                    warnings.Add("⚠ " + text(list.Count) + ": " + string.Join(", ", list.Take(3).Select(r => r.Name)) + (list.Count > 3 ? $" and {list.Count - 3} more" : string.Empty) + ".");
                }
            }

            Warn(ticked.Where(r => r.Local == LocalState.Differs && r.Kind != ChangeKind.Added), n => $"Local copies that match neither archive.org version (edited or damaged?), moved to _removed\\ and not deleted ({n})");
            Warn(ticked.Where(r => r.Kind == ChangeKind.Removed), n => $"Removed from archive.org, so you may not be able to download them again; moved to _removed\\ and not deleted ({n})");
            Warn(ticked.Where(r => r.TargetExists), n => $"A file already exists at the new name; the old copy goes to _removed\\ and the existing one is checked ({n})");
            Warn(ticked.Where(r => r.Action == SyncAction.Check), n => $"Never verified locally: the download step checks them and replaces them if they differ, with no backup. Go Back and use Hash unknown to be sure ({n})");

            if (options.DeleteExtraFiles)
            {
                Warn(rows.Where(r => !r.IsChecked && r.Kind == ChangeKind.Removed && r.Local != LocalState.Missing), n => $"\"Delete files not in the collection\" is on, so the run deletes these unticked removed files anyway ({n})");
            }

            int real = rows.Count(r => r.Kind != ChangeKind.Touched);

            if (olderCount >= 4 && real > olderCount / 2)
            {
                warnings.Add($"⚠ {real} of {olderCount} files changed. Is this still the same item? Check the URL before applying.");
            }

            long downloadBytes = ticked.Sum(r => r.DownloadBytes);

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);

                if (drive.IsReady && downloadBytes > 0)
                {
                    warnings.Add(drive.AvailableFreeSpace < downloadBytes
                        ? $"⚠ Not enough space: {drive.Name} has {Format.Bytes(drive.AvailableFreeSpace)} free, the downloads need {Format.Bytes(downloadBytes)}."
                        : $"✔ {drive.Name} has {Format.Bytes(drive.AvailableFreeSpace)} free; the downloads need {Format.Bytes(downloadBytes)} (moved copies stay on the same drive).");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // Free space is advice only.
            }

            this.WarningsItemsControl.ItemsSource = warnings;

            var actions = new List<ActionLine>();

            foreach (var row in ticked.OrderBy(r => r.Action).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                string old = row.Entry.OldName ?? row.Name;

                switch (row.Action)
                {
                    case SyncAction.Rename:
                        actions.Add(new ActionLine("rename", $"{old}  →  {row.Name}", 0));
                        break;
                    case SyncAction.MoveAway:
                        actions.Add(new ActionLine("move", $"{old}  →  _removed\\", 0));
                        break;
                    case SyncAction.Replace:
                        actions.Add(new ActionLine("move", $"{old}  →  _removed\\", 0));
                        actions.Add(new ActionLine("download", row.Name, row.Entry.B!.Size));
                        break;
                    case SyncAction.Download:
                        actions.Add(new ActionLine("download", row.Name, row.Entry.B!.Size));
                        break;
                    case SyncAction.Check:
                        actions.Add(new ActionLine("check", row.Name, row.Entry.B!.Size));
                        break;
                }
            }

            this.ActionsListView.ItemsSource = actions.OrderBy(a => a.Order).ToList();
            int moves = actions.Count(a => a.Verb is "rename" or "move");
            this.NoteTextBlock.Text = (moves > 0 ? "Renames and moves are written to _removed\\<date>\\undo.json. " : string.Empty)
                + "Downloads run in the main window with the usual Pause and Stop. Unticked rows are left exactly as they are.";
            this.ApplyButton.Content = actions.Any(a => a.Verb is "download" or "check") ? "Apply & download" : actions.Count > 0 ? "Apply" : "Save snapshot";
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
        }

        public sealed class ActionLine
        {
            public ActionLine(string verb, string text, long size)
            {
                this.Verb = verb;
                this.Text = text;
                this.SizeText = size > 0 ? Format.Bytes(size) : string.Empty;
                this.Order = verb switch { "rename" => 0, "move" => 1, "check" => 2, _ => 3 };
            }

            public string Verb { get; }

            public string Text { get; }

            public string SizeText { get; }

            public int Order { get; }
        }
    }
}
