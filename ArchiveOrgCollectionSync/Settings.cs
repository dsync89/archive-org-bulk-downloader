namespace ArchiveOrgCollectionSync
{
    using System;
    using System.IO;
    using System.Text.Json;

    /// <summary>Last-used URL, folder and options, kept in %LocalAppData%\ArchiveOrgCollectionSync\settings.json.</summary>
    public sealed class Settings
    {
        public static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArchiveOrgCollectionSync");

        private static readonly string FilePath = Path.Combine(Settings.AppDataFolder, "settings.json");

        public string Url { get; set; } = string.Empty;

        public string Folder { get; set; } = string.Empty;

        public SyncOptions Options { get; set; } = new();

        public static Settings Load()
        {
            try
            {
                return System.IO.File.Exists(Settings.FilePath)
                    ? JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(Settings.FilePath)) ?? new Settings()
                    : new Settings();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new Settings();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Settings.AppDataFolder);
                System.IO.File.WriteAllText(Settings.FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Settings are a convenience; failing to save them is not worth interrupting the user.
            }
        }
    }
}
