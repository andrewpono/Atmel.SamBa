using Anp.Atmel.SamBa.Lite.Models;
using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Xml.Serialization;


namespace Anp.Atmel.SamBa.Lite.Services
{
    public sealed class SettingsService : ISettingsService
    {
        private const string SettingsFileName = "AppSettings.xml";

        // Cache a single serializer instance for the whole process.
        // There will be an Exception thrown here: System.IO.FileNotFoundException in mscorlib.dll”
        // on an app startup. It's a first‑chance exception that XmlSerializer throws and catches
        // internally while it probes for a pre-generated serializer assembly.
        // If it can’t find that DLL, it throws FileNotFoundException, catches it,
        // and then falls back to runtime serializer generation. Visible in output window, harmless.
        private static readonly Lazy<XmlSerializer> AppSettingsSerializer =
            new Lazy<XmlSerializer>(() => new XmlSerializer(typeof(AppSettings)), isThreadSafe: true);

        private static XmlSerializer Serializer => AppSettingsSerializer.Value;

        /// <summary>
        /// Settings file path. By default, it's located in the user-specific application data directory.
        /// </summary>
        public string SettingsFilePath { get; }

        /// <summary>
        /// Settings directory path. By default, it's the user-specific application data directory. 
        /// If that cannot be determined, it falls back to the application's base directory.
        /// </summary>
        public string SettingsDirectoryPath { get; }

        /// <summary>
        /// Creates a new instance of SettingsService. 
        /// It initializes the settings file path to a user-specific location.
        /// </summary>
        public SettingsService()
        {
            SettingsDirectoryPath = GetDefaultSettingsPath() ?? AppDomain.CurrentDomain.BaseDirectory;
            SettingsFilePath = Path.Combine(SettingsDirectoryPath, SettingsFileName);
        }

        private static string GetDefaultSettingsPath()
        {
            try
            {
                var userConfig = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
                var settingsDir = Path.GetDirectoryName(userConfig.FilePath);

                if (settingsDir != null && !Directory.Exists(settingsDir))
                    Directory.CreateDirectory(settingsDir);

                return settingsDir;
            }
            catch (Exception)
            {
                return null;
            }

        }

        /// <summary>
        /// Loads settings from the XML file. If the file doesn't exist, 
        /// is empty, or an error occurs during loading,
        /// returns a new instance of AppSettings with default values.
        /// </summary>
        /// <returns>Settings object</returns>
        public AppSettings LoadOrDefault()
        {
            try
            {
                if (string.IsNullOrEmpty(SettingsFilePath) || !File.Exists(SettingsFilePath))
                    return new AppSettings();

                using (var fs = File.OpenRead(SettingsFilePath))
                {
                    return (AppSettings)Serializer.Deserialize(fs) ?? new AppSettings();
                }
            }
            catch
            {
                return new AppSettings();
            }
        }

        /// <summary>
        /// Saves the provided settings to the XML file. If the settings object is null,
        /// creates a new instance with default values and saves that.
        /// </summary>
        /// <param name="settings">Settings object</param>
        public void Save(AppSettings settings)
        {
            if (string.IsNullOrEmpty(SettingsFilePath))
                return;

            settings = settings ?? new AppSettings();

            try
            {
                Directory.CreateDirectory(SettingsDirectoryPath);
                using (var fs = File.Create(SettingsFilePath))
                {
                    Serializer.Serialize(fs, settings);
                }
            }
            catch (Exception ex)
            {
                // best effort
                Debug.WriteLine($"Error saving settings: {ex}");
            }
        }
    }
}
