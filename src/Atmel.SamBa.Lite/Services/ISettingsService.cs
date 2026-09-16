using Anp.Atmel.SamBa.Lite.Models;


namespace Anp.Atmel.SamBa.Lite.Services
{
    public interface ISettingsService
    {
        string SettingsFilePath { get; }
        string SettingsDirectoryPath { get; }
        AppSettings LoadOrDefault();
        void Save(AppSettings settings);
    }
}
