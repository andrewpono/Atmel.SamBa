using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;


namespace Anp.Atmel.SamBa.Lite.Utilities
{
    internal static class TitleBarHelper
    {
        private const int DwmwaCaptionColor = 35;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref uint value, int size);

        /// <summary>
        /// Sets the title bar color. No-op on older Windows versions that don't support it.
        /// Color format is 0x00BBGGRR (not ARGB).
        /// </summary>
        internal static void SetCaptionColor(Window window, byte r, byte g, byte b)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                    return;

                uint color = (uint)(r | (g << 8) | (b << 16));
                DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref color, sizeof(uint));
            }
            catch
            {
                // Unsupported OS version — silently ignore.
            }
        }
    }
}
