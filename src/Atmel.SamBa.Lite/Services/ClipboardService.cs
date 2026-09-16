using System.Diagnostics;
using System.Runtime.InteropServices;


namespace Anp.Atmel.SamBa.Lite.Services
{
    public sealed class ClipboardService : IClipboardService
    {
        public void SetText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch (ExternalException ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }
}
