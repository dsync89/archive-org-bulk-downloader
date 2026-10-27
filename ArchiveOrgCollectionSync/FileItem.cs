namespace ArchiveOrgCollectionSync
{
    using System;
    using System.ComponentModel;
    using System.Runtime.CompilerServices;
    using System.Threading;

    public enum FileStatus
    {
        Queued,
        Checking,
        Downloading,
        Verifying,
        Done,
        Skipped,
        Failed
    }

    /// <summary>
    /// One row of the file list. The download engine updates the state fields from any thread; the UI thread calls
    /// <see cref="Refresh"/> on a timer to publish them, so workers never block on the dispatcher.
    /// </summary>
    public sealed class FileItem : INotifyPropertyChanged
    {
        private long bytesDone;
        private int state;
        private int connections;
        private volatile string? detail;
        private long lastBytes;
        private double speed;

        public FileItem(File file)
        {
            this.Name = file.Name;
            this.Size = file.Size;
            this.HasSize = file.SizeSpecified;
            this.Md5 = file.Md5;
            this.Status = FileStatus.Queued;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; }

        public long Size { get; }

        /// <summary>Gets a value indicating whether the file list gave a size (it doesn't for &lt;collection&gt;_files.xml).</summary>
        public bool HasSize { get; }

        public string Md5 { get; }

        public string SizeText => this.HasSize ? Format.Bytes(this.Size) : string.Empty;

        /// <summary>Gets the engine-side status (any thread); <see cref="Status"/> is the UI snapshot.</summary>
        public FileStatus CurrentStatus => (FileStatus)Volatile.Read(ref this.state);

        public string? CurrentDetail => this.detail;

        public long BytesDone => Interlocked.Read(ref this.bytesDone);

        /// <summary>Gets or sets a value indicating whether the server ignored a Range request for this file.</summary>
        public bool ForceSingleStream { get; set; }

        public FileStatus Status { get; private set; }

        public double Percent { get; private set; }

        public string ProgressText { get; private set; } = "queued";

        public string SpeedText { get; private set; } = string.Empty;

        public string EtaText { get; private set; } = string.Empty;

        public double Speed => this.speed;

        public string StatusGlyph => this.Status switch
        {
            FileStatus.Queued => "·",
            FileStatus.Checking => "…",
            FileStatus.Downloading => "↓",
            FileStatus.Verifying => "🔍",
            FileStatus.Done => "✔",
            FileStatus.Skipped => "↷",
            FileStatus.Failed => "✖",
            _ => string.Empty
        };

        public bool IsActive => this.Status is FileStatus.Checking or FileStatus.Downloading or FileStatus.Verifying;

        public bool IsFinal => this.Status is FileStatus.Done or FileStatus.Skipped or FileStatus.Failed;

        /// <summary>Restores a row from session history (UI thread, before any engine run).</summary>
        public void Restore(FileStatus status, string? message)
        {
            this.SetState(status, message);
            this.SetBytes(status is FileStatus.Done or FileStatus.Skipped ? this.Size : 0);
            this.Refresh(0);
        }

        internal void SetState(FileStatus status, string? message = null)
        {
            this.detail = message;
            Volatile.Write(ref this.state, (int)status);
        }

        internal void SetBytes(long value)
        {
            Interlocked.Exchange(ref this.bytesDone, value);
        }

        internal void AddBytes(long value)
        {
            Interlocked.Add(ref this.bytesDone, value);
        }

        internal void SetConnections(int value)
        {
            Volatile.Write(ref this.connections, value);
        }

        /// <summary>Publishes the engine state to the bound properties. UI thread only.</summary>
        public void Refresh(double elapsedSeconds)
        {
            var status = this.CurrentStatus;
            long bytes = this.BytesDone;

            if (status == FileStatus.Downloading && elapsedSeconds > 0)
            {
                double instant = Math.Max(0, bytes - this.lastBytes) / elapsedSeconds;
                this.speed = this.speed == 0 ? instant : (this.speed * 0.7) + (instant * 0.3);
            }
            else
            {
                this.speed = 0;
            }

            this.lastBytes = bytes;

            double percent = this.Size > 0 ? Math.Min(100, bytes * 100.0 / this.Size) : (status is FileStatus.Done ? 100 : 0);
            int conns = Volatile.Read(ref this.connections);

            string progress = this.detail ?? status switch
            {
                FileStatus.Queued => "queued",
                FileStatus.Checking => "checking…",
                FileStatus.Downloading => conns > 1 ? $"{percent:0}%  ×{conns}" : $"{percent:0}%",
                FileStatus.Verifying => "verifying md5…",
                FileStatus.Done => "done",
                FileStatus.Skipped => "skipped",
                _ => "failed"
            };

            string speedText = status == FileStatus.Downloading ? Format.Speed(this.speed) : string.Empty;
            string etaText = status == FileStatus.Downloading && this.speed > 1
                ? Format.Duration(TimeSpan.FromSeconds((this.Size - bytes) / this.speed))
                : string.Empty;

            if (status != this.Status)
            {
                this.Status = status;
                this.OnPropertyChanged(nameof(this.Status));
                this.OnPropertyChanged(nameof(this.StatusGlyph));
                this.OnPropertyChanged(nameof(this.IsActive));
                this.OnPropertyChanged(nameof(this.IsFinal));
            }

            this.Set(percent, this.Percent, v => this.Percent = v, nameof(this.Percent));
            this.Set(progress, this.ProgressText, v => this.ProgressText = v, nameof(this.ProgressText));
            this.Set(speedText, this.SpeedText, v => this.SpeedText = v, nameof(this.SpeedText));
            this.Set(etaText, this.EtaText, v => this.EtaText = v, nameof(this.EtaText));
        }

        private void Set<T>(T value, T current, Action<T> assign, string name)
        {
            if (Equals(value, current))
            {
                return;
            }

            assign(value);
            this.OnPropertyChanged(name);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
