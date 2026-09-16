using System;


namespace Anp.Atmel.SamBa.Events
{
    /// <summary>
    /// Progress for both determinate and indeterminate work: a value with an optional maximum, an
    /// optional unit label, and an optional stage and message. Named for the library rather than
    /// <c>ProgressChangedEventArgs</c>, which the BCL already uses.
    /// <para>
    /// Unlike the arrival and removal args in this namespace, the constructors here are public on
    /// purpose. A consumer folding several devices into one progress stream, or exercising its own
    /// handler in a test, has to be able to build these — and a synthesized report can mislead
    /// nobody but its author, since the library only ever writes them and never reads them back.
    /// </para>
    /// </summary>
    public sealed class SamBaProgressEventArgs : EventArgs
    {
        /// <summary>
        /// Human-readable stage name (e.g. "Erasing", "Writing", "Verifying").
        /// Optional; empty string if not provided.
        /// </summary>
        public string Stage { get; }

        /// <summary>
        /// Human-readable status message (e.g. "128 of 1024 pages written").
        /// Optional; empty string if not provided.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Progress value. Interpretation depends on <see cref="Units"/> (bytes, pages, etc.).
        /// </summary>
        public long Value { get; }

        /// <summary>
        /// Progress maximum (total). If null or &lt;= 0, progress is considered indeterminate.
        /// </summary>
        public long? Maximum { get; }

        /// <summary>
        /// Optional unit label for <see cref="Value"/> and <see cref="Maximum"/> (e.g. "bytes", "pages").
        /// Empty string if not provided.
        /// </summary>
        public string Units { get; }

        /// <summary>
        /// Optional caller-supplied state, carried through untouched. The library never sets or reads
        /// it: it exists for a consumer that builds its own progress reports and needs to correlate
        /// them with something of its own (an operation id, a UI handle). Events the library raises
        /// need no tag for that — their <c>sender</c> is the <see cref="SamBaDevice"/> reporting.
        /// </summary>
        public object Tag { get; }

        /// <summary>
        /// When the snapshot was created, for rate and ETA calculations outside the library. A wall
        /// clock reading, not a monotonic one: a clock adjustment between two events can leave the
        /// interval zero or negative, so guard anything that divides by it.
        /// </summary>
        public DateTime TimestampUtc { get; }

        /// <summary>
        /// True when progress cannot be determined (no usable maximum provided).
        /// </summary>
        public bool IsIndeterminate => !Maximum.HasValue || Maximum.Value <= 0;

        /// <summary>
        /// Fraction complete in range [0..1], or null if indeterminate. Clamped: a <see cref="Value"/>
        /// past <see cref="Maximum"/> reads as 1 rather than overshooting, so this can sit at 1 while
        /// further events still arrive.
        /// </summary>
        public double? Fraction
        {
            get
            {
                if (IsIndeterminate)
                    return null;

                // Clamped because nothing validates Value against Maximum: a caller is free to
                // report past the end, and a fraction above 1 would propagate into every consumer.
                double f = Value / (double)Maximum.Value;
                if (f < 0d) return 0d;
                if (f > 1d) return 1d;
                return f;
            }
        }

        /// <summary>
        /// Percentage complete in range [0..100], or null if indeterminate. Truncated, so 100 arrives
        /// only on completion; clamped the same way <see cref="Fraction"/> is.
        /// </summary>
        public int? Percentage
        {
            get
            {
                double? f = Fraction;
                if (!f.HasValue)
                    return null;

                // Truncated, not rounded: monotonic, and it reads 100 only on completion instead of
                // from 99.5% onwards. No clamping needed here — Fraction already returns [0..1].
                return (int)(f.Value * 100d);
            }
        }

        /// <summary>
        /// Creates determinate progress.
        /// </summary>
        /// <param name="value">Progress so far, counted in whatever <paramref name="units"/> names.</param>
        /// <param name="maximum">
        /// The total to measure against. Zero or less still yields an indeterminate instance, since
        /// there is then nothing to measure against — see <see cref="IsIndeterminate"/>.
        /// </param>
        /// <param name="message">Status message; null becomes an empty string.</param>
        /// <param name="stage">Stage name; null becomes an empty string.</param>
        /// <param name="units">Unit label for the two counts; null becomes an empty string.</param>
        /// <param name="tag">Caller state, carried through untouched — see <see cref="Tag"/>.</param>
        public SamBaProgressEventArgs(
            long value,
            long maximum,
            string message = null,
            string stage = null,
            string units = null,
            object tag = null)
            : this(value, (long?)maximum, message, stage, units, tag)
        {
        }

        /// <summary>
        /// Creates determinate or indeterminate progress (if maximum is null or &lt;= 0).
        /// </summary>
        /// <param name="value">Progress so far, counted in whatever <paramref name="units"/> names.</param>
        /// <param name="maximum">
        /// The total to measure against, or null when there is none. Null, zero or less makes the
        /// instance indeterminate — see <see cref="IsIndeterminate"/>.
        /// </param>
        /// <param name="message">Status message; null becomes an empty string.</param>
        /// <param name="stage">Stage name; null becomes an empty string.</param>
        /// <param name="units">Unit label for the two counts; null becomes an empty string.</param>
        /// <param name="tag">Caller state, carried through untouched — see <see cref="Tag"/>.</param>
        public SamBaProgressEventArgs(
            long value,
            long? maximum,
            string message = null,
            string stage = null,
            string units = null,
            object tag = null)
        {
            Value = value;
            Maximum = maximum;
            Message = message ?? string.Empty;
            Stage = stage ?? string.Empty;
            Units = units ?? string.Empty;
            Tag = tag;
            TimestampUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Convenience factory for indeterminate progress updates: reports <see cref="Value"/> 0 with
        /// no maximum and no units, so only the stage and message carry anything.
        /// </summary>
        /// <param name="message">Status message; null becomes an empty string.</param>
        /// <param name="stage">Stage name; null becomes an empty string.</param>
        /// <param name="tag">Caller state, carried through untouched — see <see cref="Tag"/>.</param>
        public static SamBaProgressEventArgs Indeterminate(string message, string stage = null, object tag = null)
            => new SamBaProgressEventArgs(0, null, message, stage, null, tag);

        /// <summary>
        /// A one-line summary for logs: the stage and message when indeterminate, otherwise the
        /// counts, units and percentage followed by whichever of the two are set. Empty when an
        /// indeterminate instance carries neither — there is nothing left to render.
        /// Diagnostic text; the format is not stable and nothing parses it back.
        /// </summary>
        public override string ToString()
        {
            // Built by joining only the parts that are present, so an absent stage or message leaves
            // no dangling separator behind — "5/10 (50%) -" was the old result for a bare count.
            string label;
            if (string.IsNullOrEmpty(Stage))
                label = Message;
            else if (string.IsNullOrEmpty(Message))
                label = Stage;
            else
                label = Stage + ": " + Message;

            if (IsIndeterminate)
                return label;

            string counts = string.IsNullOrEmpty(Units)
                ? $"{Value}/{Maximum}"
                : $"{Value}/{Maximum} {Units}";

            return label.Length == 0
                ? $"{counts} ({Percentage}%)"
                : $"{counts} ({Percentage}%) - {label}";
        }
    }
}
