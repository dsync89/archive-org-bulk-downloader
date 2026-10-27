namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Windows;
    using System.Windows.Media;

    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error,
        Success
    }

    public class LogEntry
    {
        private static readonly Brush DebugBrush = LogEntry.Freeze(new SolidColorBrush(Colors.Gray));
        private static readonly Brush WarnBrush = LogEntry.Freeze(new SolidColorBrush(Colors.DarkOrange));
        private static readonly Brush ErrorBrush = LogEntry.Freeze(new SolidColorBrush(Colors.Red));
        private static readonly Brush SuccessBrush = LogEntry.Freeze(new SolidColorBrush(Colors.Green));

        public LogEntry(LogLevel level, string message, string? file)
        {
            this.Time = DateTime.Now;
            this.Level = level;
            this.Message = message;
            this.File = file ?? string.Empty;
        }

        public DateTime Time { get; }

        public LogLevel Level { get; }

        public string Message { get; }

        public string File { get; }

        public Brush Brush => this.Level switch
        {
            LogLevel.Debug => LogEntry.DebugBrush,
            LogLevel.Warn => LogEntry.WarnBrush,
            LogLevel.Error => LogEntry.ErrorBrush,
            LogLevel.Success => LogEntry.SuccessBrush,
            _ => SystemColors.ControlTextBrush
        };

        public override string ToString()
        {
            return this.File.Length > 0
                ? $"{this.Time:yyyy-MM-dd HH:mm:ss}\t{this.Level}\t{this.File}\t{this.Message}"
                : $"{this.Time:yyyy-MM-dd HH:mm:ss}\t{this.Level}\t\t{this.Message}";
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
