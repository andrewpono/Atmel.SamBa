using Anp.Atmel.SamBa.Chips;
using System;
using System.Collections.Generic;
using System.Linq;


namespace Anp.Atmel.SamBa
{
    /// <summary>The chip types baked into this library's device table.</summary>
    public static class SupportedChips
    {
        // Built once and shared. Safe to share because nothing in it can change: the device table is
        // immutable, so is every SamBaChipInfo built from one of its rows, and the list itself is
        // handed out read-only — which a cached list has to be. A caller casting a bare array back
        // and writing to it would not be hurting only itself, it would rewrite what every later
        // caller sees.
        private static readonly IReadOnlyList<SamBaChipInfo> _chips = Build();

        /// <summary>
        /// Returns the distinct chip types this library recognizes — everything
        /// <see cref="SamBaChipInfo"/> describes apart from the four identification words, which no
        /// probe produced here. Package/revision variants that share a name (several device-table
        /// entries differing only by identification word) are collapsed into a single entry. The same
        /// list every call.
        /// <para>
        /// Ordered by family and then by name. Family order is <see cref="SamBaChipFamily"/>'s
        /// declaration order — grouped by flash controller, alphabetical inside each group — and not
        /// alphabetical by family name. Names are compared ordinally, so the order does not shift
        /// with the machine's locale.
        /// </para>
        /// <para>
        /// This is the device table, not the limit of what can be programmed: a part that answers
        /// CHIPID or the DSU but matches no row here is still identified by family fallback and
        /// programmed from the geometry its own flash controller reports — it simply cannot be reset
        /// afterwards. Every entry is a piece of silicon, though: the one emulated-bootloader row the
        /// device table carried is disabled, so nothing listed here is a program impersonating a part.
        /// </para>
        /// </summary>
        /// <returns>
        /// One entry per distinct chip name, ordered as described above. The same read-only instance
        /// every call — shared safely because neither the list nor anything in it can change.
        /// </returns>
        public static IReadOnlyList<SamBaChipInfo> Get() => _chips;

        private static IReadOnlyList<SamBaChipInfo> Build()
        {
            // Which row of a same-name group is taken does not matter: rows sharing a name agree on
            // every field but the identification word, which ChipTableTests pins so that a later
            // variant added with different geometry cannot quietly be published under this name.
            SamBaChipInfo[] chips = ChipTable.Rows
                .GroupBy(row => row.Name)
                .Select(group => new SamBaChipInfo(group.First()))
                .OrderBy(chip => chip.Family)
                .ThenBy(chip => chip.Name, StringComparer.Ordinal)
                .ToArray();

            return Array.AsReadOnly(chips);
        }
    }
}
