namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Globalization;
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
            : this(DateTime.Now, level, message, file)
        {
        }

        public LogEntry(DateTime time, LogLevel level, string message, string? file)
        {
            this.Time = time;
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
            return $"{this.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}\t{this.Level}\t{this.File}\t{this.Message}";
        }

        /// <summary>Parses a line written by <see cref="ToString"/> (session log files).</summary>
        public static LogEntry? Parse(string line)
        {
            string[] parts = line.Split('\t', 4);

            if (parts.Length < 4
                || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                || !Enum.TryParse<LogLevel>(parts[1], out var level))
            {
                return null;
            }

            return new LogEntry(time, level, parts[3], parts[2]);
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
