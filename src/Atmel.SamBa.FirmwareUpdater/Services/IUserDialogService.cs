

namespace Anp.Atmel.SamBa.FirmwareUpdater.Services
{
    public interface IUserDialogService
    {
        string BrowseFirmwareFile();
        string BrowseSaveLogFile(string suggestedFileName);
        string BrowseSaveBinaryFile(string suggestedFileName);

        bool Confirm(string title, string message);
        void ShowInfo(string title, string message);
        void ShowError(string title, string message);
    }
}
