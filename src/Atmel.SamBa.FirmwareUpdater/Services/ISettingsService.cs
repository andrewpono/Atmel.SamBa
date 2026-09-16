using Anp.Atmel.SamBa.FirmwareUpdater.Models;


namespace Anp.Atmel.SamBa.FirmwareUpdater.Services
{
    public interface ISettingsService
    {
        string SettingsFilePath { get; }
        string SettingsDirectoryPath { get; }
        AppSettings LoadOrDefault();
        void Save(AppSettings settings);
    }
}
