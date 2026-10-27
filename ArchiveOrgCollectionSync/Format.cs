namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Globalization;

    public static class Format
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

        public static string Bytes(long bytes)
        {
            double value = bytes;
            int unit = 0;

            while (value >= 1024 && unit < Format.Units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0
                ? $"{bytes} B"
                : value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + Format.Units[unit];
        }

        public static string Speed(double bytesPerSecond)
        {
            return bytesPerSecond < 1 ? string.Empty : Format.Bytes((long)bytesPerSecond) + "/s";
        }

        public static string Duration(TimeSpan? duration)
        {
            if (duration == null || duration.Value < TimeSpan.Zero || duration.Value > TimeSpan.FromDays(30))
            {
                return string.Empty;
            }

            var d = duration.Value;
            return d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}";
        }
    }
}
