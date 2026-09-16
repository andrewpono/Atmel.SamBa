using Anp.Atmel.SamBa.Chips;
using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Protocol;


namespace Anp.Atmel.SamBa.Flash
{
    /// <summary>
    /// NVMCTRL flash controller, row-erase generation (SAMD21/R21/L21).
    /// The NVM page buffer is filled with batched <c>W#</c> writes to the flash-mapped page
    /// address instead of an on-target word-copy applet.
    /// </summary>
    internal sealed class D2xNvm : NvmFamilyController
    {
        /// <summary>
        /// NVMCTRL register offsets from <see cref="NvmFamilyController.NvmctrlBaseAddress"/>. All
        /// are 32-bit here; the D5x generation mixes widths and swaps the roles of CTRLA and CTRLB.
        /// </summary>
        private static class Reg
        {
            /// <summary>Command register: takes a keyed command.</summary>
            public const byte CtrlA = 0x00;

            /// <summary>Configuration register: cache and write-mode bits.</summary>
            public const byte CtrlB = 0x04;

            /// <summary>Interrupt flags: reports ready and command errors.</summary>
            public const byte IntFlag = 0x14;

            /// <summary>Status: security bit and the sticky error flags.</summary>
            public const byte Status = 0x18;

            /// <summary>Address the next command acts on.</summary>
            public const byte Addr = 0x1C;
        }

        /// <summary>
        /// CTRLA command codes, with their datasheet mnemonics. Sparse opcodes, not flags — never
        /// combine them. Consts rather than an enum for the reason on
        /// <see cref="NvmFamilyController.WriteCommandRegister"/>.
        /// </summary>
        private static class Cmd
        {
            public const byte EraseRow = 0x02;         // ER
            public const byte WritePage = 0x04;        // WP
            public const byte EraseAuxRow = 0x05;      // EAR
            public const byte WriteAuxPage = 0x06;     // WAP
            public const byte SetSecurityBit = 0x45;   // SSB
            public const byte PageBufferClear = 0x44;  // PBC
        }

        /// <summary>CTRLB.CACHEDIS — disables the NVM read cache.</summary>
        private const uint CtrlbCacheDisableMask = 1u << 18;

        /// <summary>CTRLB.MANW — page writes wait for an explicit WP command.</summary>
        private const uint CtrlbManualWriteMask = 1u << 7;

        /// <summary>STATUS.SB — the security bit is set.</summary>
        private const uint StatusSecurityMask = 1u << 8;

        // STATUS error flags. Bits 0 (PRM, power-reduction mode) and 1 (LOAD, page buffer holds
        // data) report state rather than failure, so they are not part of the mask below.
        private const uint StatusProgramErrorMask = 1u << 2;  // PROGE
        private const uint StatusLockErrorMask = 1u << 3;     // LOCKE
        private const uint StatusNvmErrorMask = 1u << 4;      // NVME

        /// <summary>
        /// Every STATUS error flag. Written back before a command so a flag left by an earlier
        /// operation cannot be read as this command's failure; the flags are write-one-to-clear.
        /// </summary>
        private const uint StatusErrorMask =
            StatusProgramErrorMask | StatusLockErrorMask | StatusNvmErrorMask;

        /// <summary>INTFLAG.READY — the controller can accept a command.</summary>
        private const uint IntFlagReadyMask = 1u << 0;

        /// <summary>INTFLAG.ERROR — the last command failed.</summary>
        private const uint IntFlagErrorMask = 1u << 1;

        /// <summary>NVMCTRL ADDR holds a 16-bit word address on D2x, so byte addresses halve.</summary>
        private const uint BytesPerAddressUnit = 2;

        internal D2xNvm(SambaMonitor monitor, ChipRecord chip)
            : base(monitor, chip)
        {
        }

        protected override byte EraseUnitCommand => Cmd.EraseRow;

        protected override string EraseUnitLabel => "row erase (ER)";

        protected override byte PageBufferClearCommand => Cmd.PageBufferClear;

        protected override byte WritePageCommand => Cmd.WritePage;

        /// <summary>The user row keeps its lock bits at offset 6.</summary>
        protected override uint UserAreaLockOffset => 0x6;

        /// <summary>The user row is exactly one write block (four 64-byte pages).</summary>
        protected override int UserAreaSize => WriteBlockSize;

        protected override byte SetSecurityCommand => Cmd.SetSecurityBit;

        /// <summary>
        /// The four serial-number words, from the datasheet's Serial Number section: word 0 on its
        /// own, then words 1-3 in a contiguous trio higher up the same read-only auxiliary space.
        /// The SAMC21, SAML21 and SAMR21 share this map with the SAMD21.
        /// </summary>
        private static readonly uint[] SerialNumberAddresses =
            { 0x0080A00C, 0x0080A040, 0x0080A044, 0x0080A048 };

        protected override uint[] UniqueIdWordAddresses => SerialNumberAddresses;

        public override bool GetSecurity() => (ReadReg(Reg.Status) & StatusSecurityMask) != 0;

        protected override bool IsReady() => (ReadReg(Reg.IntFlag) & IntFlagReadyMask) != 0;

        protected override void WriteCommandRegister(byte cmd) => WriteReg(Reg.CtrlA, ExecuteKey | cmd);

        protected override void WriteAddress(uint byteAddress) => WriteReg(Reg.Addr, ToAddressUnits(byteAddress));

        protected override void ThrowIfCommandError(string operation)
        {
            if ((ReadReg(Reg.IntFlag) & IntFlagErrorMask) == 0)
                return;

            WriteReg(Reg.IntFlag, IntFlagErrorMask);  // clear the error bit
            throw new SamBaFlashCommandException("NVMCTRL command failed.", operation, isCommandError: true);
        }

        /// <summary>
        /// STATUS keeps its error flags until written back, so clear them while the controller is
        /// idle — otherwise a flag from an earlier operation outlives it.
        /// </summary>
        protected override void ClearStaleStatus(string operation)
        {
            WaitReady(operation);
            WriteReg(Reg.Status, ReadReg(Reg.Status) | StatusErrorMask);
        }

        protected override void ConfigureManualWrite()
        {
            WriteReg(Reg.CtrlB, ReadReg(Reg.CtrlB) | CtrlbCacheDisableMask | CtrlbManualWriteMask);
        }

        protected override void WriteUserArea(byte[] userRow)
        {
            // Erase the user row, then rewrite it page by page. Manual write mode is already in
            // force: it is configuration, set once in OnInitialize, not per-command state.
            WriteAddress(UserAreaAddress);
            Execute(Cmd.EraseAuxRow, "user-row erase (EAR)");

            for (int offset = 0; offset < userRow.Length; offset += PageSize)
            {
                Execute(Cmd.PageBufferClear, nameof(WriteUserArea));

                uint address = UserAreaAddress + (uint)offset;
                WaitReady(nameof(WriteUserArea));
                LoadLatch(address, userRow, offset, PageSize);

                WriteAddress(address);
                Execute(Cmd.WriteAuxPage, "user-row write (WAP)");
            }
        }

        private static uint ToAddressUnits(uint byteAddress) => byteAddress / BytesPerAddressUnit;

        private uint ReadReg(byte reg) => Monitor.ReadWord(NvmctrlBaseAddress + reg);

        private void WriteReg(byte reg, uint value) => Monitor.WriteWord(NvmctrlBaseAddress + reg, value);
    }
}
