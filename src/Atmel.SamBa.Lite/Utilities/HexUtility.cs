

namespace Anp.Atmel.SamBa.Lite.Utilities
{
    internal static class HexUtility
    {
        // Normalizes a hexadecimal string by trimming whitespace,
        // removing "0x" prefix if present, and converting to uppercase.    
        internal static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            text = text.Trim();
            if (text.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);

            return text.ToUpperInvariant();
        }

        // Tries to parse a hexadecimal string into a uint.
        // Handles optional "0x" prefix and ignores leading/trailing whitespace.
        internal static bool TryParse(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            text = Normalize(text);
            return uint.TryParse(text,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
        }
    }
}
