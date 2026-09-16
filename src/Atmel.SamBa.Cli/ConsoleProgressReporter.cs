using Anp.Atmel.SamBa.Events;
using System;


namespace Anp.Atmel.SamBa.Cli
{
    /// <summary>
    /// Writes <see cref="SamBaDevice.ProgressChanged"/> reports to the console. An indeterminate
    /// report prints one line each time its stage or message changes — same de-duplication as
    /// before, so a tight update loop does not flood the output. A determinate report instead
    /// redraws a bossac-style bar (<c>[====    ] NN% (v/max units)</c>) in place on one line with
    /// carriage returns, finalized with a trailing newline once its stage changes or it completes,
    /// so whatever prints next starts on a clean line.
    /// </summary>
    internal sealed class ConsoleProgressReporter
    {
        private const int BarWidth = 30;

        private readonly object _gate = new object();
        private int? _lastPercent = null;
        private string _lastStage = null;
        private string _lastMessage = null;
        private bool _lastWasIndeterminate = false;
        private bool _barOpen = false;

        public void OnProgress(object sender, SamBaProgressEventArgs e)
        {
            if (e == null)
                return;

            lock (_gate)
            {
                if (e.IsIndeterminate)
                    WriteIndeterminate(e);
                else
                    WriteBar(e);
            }
        }

        private void WriteIndeterminate(SamBaProgressEventArgs e)
        {
            CloseBar();

            bool stageChanged = !string.Equals(e.Stage, _lastStage, StringComparison.Ordinal);
            bool messageChanged = !string.Equals(e.Message, _lastMessage, StringComparison.Ordinal);
            bool modeChanged = !_lastWasIndeterminate;

            if (!stageChanged && !messageChanged && !modeChanged)
                return;

            _lastStage = e.Stage;
            _lastMessage = e.Message;
            _lastPercent = null;
            _lastWasIndeterminate = true;

            string stagePrefix = string.IsNullOrWhiteSpace(e.Stage) ? string.Empty : "[" + e.Stage + "] ";
            Console.WriteLine(stagePrefix + e.Message);
        }

        private void WriteBar(SamBaProgressEventArgs e)
        {
            int percent = e.Percentage.GetValueOrDefault();
            bool stageChanged = !string.Equals(e.Stage, _lastStage, StringComparison.Ordinal);

            // Nothing visible would change: same bar, same percent, already drawn.
            if (!stageChanged && !_lastWasIndeterminate && _lastPercent == percent)
                return;

            // A new stage starts its own bar below whatever the previous stage left behind.
            if (stageChanged)
                CloseBar();

            _lastStage = e.Stage;
            _lastMessage = e.Message;
            _lastPercent = percent;
            _lastWasIndeterminate = false;
            _barOpen = true;

            int filled = Math.Min(BarWidth, BarWidth * percent / 100);
            string bar = new string('=', filled) + new string(' ', BarWidth - filled);
            string counts = string.IsNullOrEmpty(e.Units)
                ? $"{e.Value}/{e.Maximum}"
                : $"{e.Value}/{e.Maximum} {e.Units}";

            Console.Write($"\r[{bar}] {percent.ToString().PadLeft(3)}% ({counts})");

            if (e.Maximum.HasValue && e.Value >= e.Maximum.Value)
                CloseBar();
        }

        /// <summary>Ends the in-place bar line with a newline, if one is open, so the next write
        /// (another progress line, or the app's own output) does not land on top of it.</summary>
        private void CloseBar()
        {
            if (!_barOpen)
                return;

            Console.WriteLine();
            _barOpen = false;
        }
    }
}
