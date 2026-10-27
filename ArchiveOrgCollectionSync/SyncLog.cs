namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>
    /// Thread-safe log sink. Workers write here; the UI drains <see cref="Drain"/> on its timer, and every entry is also
    /// appended to the session's log file.
    /// </summary>
    public sealed class SyncLog : IDisposable
    {
        private readonly ConcurrentQueue<LogEntry> pending = new();
        private readonly object fileLock = new();
        private StreamWriter? writer;

        public void Open(string path)
        {
            lock (this.fileLock)
            {
                this.writer?.Dispose();
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
                this.writer = new StreamWriter(path, true) { AutoFlush = true };
            }
        }

        public void Close()
        {
            lock (this.fileLock)
            {
                this.writer?.Dispose();
                this.writer = null;
            }
        }

        public void Write(LogLevel level, string message, string? file = null)
        {
            var entry = new LogEntry(level, message, file);
            this.pending.Enqueue(entry);

            lock (this.fileLock)
            {
                try
                {
                    this.writer?.WriteLine(entry.ToString());
                }
                catch (IOException)
                {
                    // A failing log file must never break a sync.
                }
            }
        }

        public IEnumerable<LogEntry> Drain()
        {
            while (this.pending.TryDequeue(out var entry))
            {
                yield return entry;
            }
        }

        public void Dispose()
        {
            this.Close();
        }
    }
}
