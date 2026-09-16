using Anp.Atmel.SamBa.Chips;
using System.Collections.Generic;
using System.Linq;

namespace Anp.Atmel.SamBa.Flash
{
    /// <summary>
    /// Flash geometry as reported by the part itself — the EEFC's GETD descriptor or the
    /// NVMCTRL's PARAM register — rather than by the device table. What a controller's
    /// <c>ReadDeviceGeometry</c> returns, and the input to the adopt-or-verify decision in
    /// <see cref="FlashController.Create"/>: adopted outright when no table row matched,
    /// compared against the row when one did.
    /// </summary>
    internal readonly struct DeviceFlashGeometry
    {
        /// <summary>Total flash pages across all planes.</summary>
        public readonly int PageCount;

        /// <summary>Page size in bytes.</summary>
        public readonly int PageSize;

        /// <summary>
        /// Flash controllers that answered, matching <see cref="ChipRecord.PlaneCount"/>'s meaning so
        /// the two can be compared: how many register blocks the flash layer drives, always 1 on
        /// NVMCTRL parts. Deliberately not the descriptor's FL_NB_PLANE, which counts banks — a
        /// single-EEFC dual-bank part such as the ATSAM3SD8 reports 2 there and is 1 here.
        /// </summary>
        public readonly int PlaneCount;

        /// <summary>Total lock regions across all planes.</summary>
        public readonly int LockRegionCount;

        internal DeviceFlashGeometry(int pageCount, int pageSize, int planeCount, int lockRegionCount)
        {
            PageCount = pageCount;
            PageSize = pageSize;
            PlaneCount = planeCount;
            LockRegionCount = lockRegionCount;
        }

        /// <summary>Flash size in bytes.</summary>
        public long FlashSize => (long)PageCount * PageSize;

        /// <summary>True when the device's account agrees with a table row on all four fields.</summary>
        public bool Matches(ChipRecord chip)
        {
            return PageCount == chip.PageCount
                && PageSize == chip.PageSize
                && PlaneCount == chip.PlaneCount
                && LockRegionCount == chip.LockRegionCount;
        }

        /// <summary>
        /// The gate a device-reported geometry must pass before anything acts on it. The criteria
        /// are the same invariants the test suite pins on every table row — sane field ranges plus
        /// the divisibility the flash layer's integer arithmetic silently relies on — because a
        /// geometry that fails them would corrupt a write whichever source it came from. A reply
        /// of zeros (a part with no descriptor, a fake with nothing scripted) fails on
        /// <see cref="PageCount"/> alone.
        /// <para>
        /// Both plane divisions are part of that: a two-controller part has its pages and its lock
        /// regions split evenly between the two (<c>EfcFamilyController.PagesPerPlane</c>,
        /// <c>LockRegionsPerPlane</c>, both plain integer halves), so an odd count of either would not
        /// merely be reported oddly — it would put every page number and lock argument above the
        /// boundary one short of the page it meant.
        /// </para>
        /// </summary>
        /// <param name="pagesPerWriteBlock">
        /// The controller generation's write-block size in pages, which the descriptor does not
        /// carry — it comes from the controller kind.
        /// </param>
        public bool IsUsable(int pagesPerWriteBlock) => !Validate(pagesPerWriteBlock).Any();

        /// <summary>
        /// Which of <see cref="IsUsable"/>'s checks this geometry fails, and why — for a caller that
        /// already knows <see cref="IsUsable"/> returned false and needs to say more than that in a
        /// log entry or exception message. Empty when the geometry is usable.
        /// </summary>
        /// <param name="pagesPerWriteBlock">Same meaning as on <see cref="IsUsable"/>.</param>
        internal string UnusableReasons(int pagesPerWriteBlock) =>
            string.Join("; ", Validate(pagesPerWriteBlock));

        /// <summary>
        /// The single source of truth behind both <see cref="IsUsable"/> and
        /// <see cref="UnusableReasons(int)"/> — every check they share lives here exactly once, so the
        /// two can never drift apart on what "usable" means. Two checks (the two modulo-by-
        /// <see cref="LockRegionCount"/>/<see cref="PlaneCount"/> pairs) are guarded by the
        /// positivity check they depend on rather than evaluated unconditionally: that guard is what
        /// the original single <c>&amp;&amp;</c> chain got for free from short-circuiting and
        /// left-to-right ordering, made explicit here so collecting every failing reason (instead of
        /// stopping at the first) cannot resurrect the <see cref="System.DivideByZeroException"/> a
        /// reply of all zeros would otherwise cause.
        /// </summary>
        private IEnumerable<string> Validate(int pagesPerWriteBlock)
        {
            const long MaxFlashBytes = 16 * 1024 * 1024;
            const int MinPageSize = 8;          // smallest PSZ the NVMCTRL encoding can express
            const int MaxPageSize = 8192;

            bool pageSizeInRange = PageSize >= MinPageSize && PageSize <= MaxPageSize;
            if (!pageSizeInRange)
                yield return $"page size {PageSize} B is outside [{MinPageSize}, {MaxPageSize}]";
            else if ((PageSize & (PageSize - 1)) != 0)
                yield return $"page size {PageSize} B is not a power of two";

            if (PageCount <= 0)
                yield return $"page count {PageCount} is not positive";
            if (FlashSize > MaxFlashBytes)
                yield return $"flash size {FlashSize} B exceeds {MaxFlashBytes} B";

            bool planeCountValid = PlaneCount == 1 || PlaneCount == 2;
            if (!planeCountValid)
                yield return $"plane count {PlaneCount} is neither 1 nor 2";

            bool lockRegionPositive = LockRegionCount > 0;
            if (!lockRegionPositive)
                yield return $"lock region count {LockRegionCount} is not positive";
            else if (PageCount % LockRegionCount != 0)
                yield return $"page count {PageCount} is not divisible by " +
                    $"lock region count {LockRegionCount}";

            if (PageCount % pagesPerWriteBlock != 0)
                yield return $"page count {PageCount} is not divisible by " +
                    $"write-block size {pagesPerWriteBlock} page(s)";
            if (planeCountValid && PageCount % PlaneCount != 0)
                yield return $"page count {PageCount} is not divisible by plane count {PlaneCount}";
            if (planeCountValid && lockRegionPositive && LockRegionCount % PlaneCount != 0)
                yield return $"lock region count {LockRegionCount} is not divisible by " +
                    $"plane count {PlaneCount}";
        }
    }
}
