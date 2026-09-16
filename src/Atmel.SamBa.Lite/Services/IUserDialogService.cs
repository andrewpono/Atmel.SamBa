

namespace Anp.Atmel.SamBa.Lite.Services
{
    public interface IUserDialogService
    {
        string BrowseFirmwareFile();
        string BrowseOpenBinaryFile(string title);
        string BrowseSaveLogFile(string suggestedFileName);
        string BrowseSaveBinaryFile(string suggestedFileName);
        string BrowseSaveTextFile(string suggestedFileName);

        bool Confirm(string title, string message);
        void ShowInfo(string title, string message);
        void ShowError(string title, string message);
    }
}
