using Anp.Atmel.SamBa.Chips;
using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Protocol;


namespace Anp.Atmel.SamBa.Flash
{
    /// <summary>
    /// NVMCTRL flash controller, block-erase generation (SAMD51/E5x).
    /// The NVM page buffer is filled with batched <c>W#</c> writes to the flash-mapped page
    /// address instead of an on-target word-copy applet.
    /// </summary>
    internal sealed class D5xNvm : NvmFamilyController
    {
        /// <summary>
        /// NVMCTRL register offsets from <see cref="NvmFamilyController.NvmctrlBaseAddress"/>. Widths
        /// are mixed here, and CTRLA/CTRLB swap roles relative to the D2x generation.
        /// </summary>
        private static class Reg
        {
            /// <summary>Configuration register: cache and write-mode bits (16-bit).</summary>
            public const byte CtrlA = 0x00;

            /// <summary>Command register: takes a keyed command (32-bit).</summary>
            public const byte CtrlB = 0x04;

            /// <summary>Interrupt flags: reports command errors (16-bit).</summary>
            public const byte IntFlag = 0x10;

            /// <summary>Status: reports the controller ready (16-bit).</summary>
            public const byte Status = 0x12;

            /// <summary>Address the next command acts on (32-bit).</summary>
            public const byte Addr = 0x14;
        }

        /// <summary>
        /// CTRLB command codes, with their datasheet mnemonics. Sparse opcodes, not flags — never
        /// combine them. Consts rather than an enum for the reason on
        /// <see cref="NvmFamilyController.WriteCommandRegister"/>.
        /// </summary>
        private static class Cmd
        {
            public const byte ErasePage = 0x00;        // EP (user page only)
            public const byte EraseBlock = 0x01;       // EB
            public const byte WritePage = 0x03;        // WP
            public const byte WriteQuadWord = 0x04;    // WQW
            public const byte SetSecurityBit = 0x16;   // SSB
            public const byte PageBufferClear = 0x15;  // PBC
        }

        /// <summary>CTRLA.CACHEDIS0/1 (bits 14-15) — set to disable both NVM read caches.</summary>
        private const int CtrlaCacheDisableMask = 0x3 << 14;

        /// <summary>
        /// CTRLA.WMODE field (bits 4-5). Cleared selects MAN, where a loaded page buffer is only
        /// committed by an explicit write command.
        /// </summary>
        private const int CtrlaWriteModeMask = 0x3 << 4;

        /// <summary>STATUS.READY — the controller can accept a command.</summary>
        private const uint StatusReadyMask = 1u << 0;

        // INTFLAG failure flags. Bit 0 (DONE) reports completion, and bits 4-5 (ECCSE, ECCDE)
        // report ECC results from reads rather than from command execution, so neither belongs here.
        private const uint IntFlagAddressErrorMask = 1u << 1;  // ADDRE
        private const uint IntFlagProgramErrorMask = 1u << 2;  // PROGE
        private const uint IntFlagLockErrorMask = 1u << 3;     // LOCKE
        private const uint IntFlagNvmErrorMask = 1u << 6;      // NVME
        private const uint IntFlagSuspendedMask = 1u << 7;     // SUSP

        /// <summary>
        /// What counts as a failed command: the four error flags, plus SUSP. Nothing here issues a
        /// suspend, so a suspended controller means the sequence is not where we think it is and is
        /// treated as a failure too. Write-one-to-clear, so the same mask tests for the failure and
        /// then clears it.
        /// </summary>
        private const uint IntFlagErrorMask =
            IntFlagAddressErrorMask | IntFlagProgramErrorMask | IntFlagLockErrorMask
            | IntFlagNvmErrorMask | IntFlagSuspendedMask;

        /// <summary>Bytes written per WQW (write-quad-word) command.</summary>
        private const int QuadWordBytes = 16;

        // The security bit lives in the Device Service Unit here, not in NVMCTRL.
        private const uint DsuBaseAddress = 0x41002000;
        private const uint DsuStatusBAddress = DsuBaseAddress + 0x2;

        /// <summary>STATUSB.PROT — the device is protected, i.e. the security bit is set.</summary>
        private const byte DsuStatusBProtMask = 0x1;

        internal D5xNvm(SambaMonitor monitor, ChipRecord chip)
            : base(monitor, chip)
        {
        }

        protected override byte EraseUnitCommand => Cmd.EraseBlock;

        protected override string EraseUnitLabel => "block erase (EB)";

        protected override byte PageBufferClearCommand => Cmd.PageBufferClear;

        protected override byte WritePageCommand => Cmd.WritePage;

        /// <summary>The user page keeps its lock bits at offset 8.</summary>
        protected override uint UserAreaLockOffset => 0x8;

        /// <summary>
        /// The user page is erased and rewritten whole. One hardware page, not one write block —
        /// it has its own erase-page command, so the 16-page block granularity does not apply to it.
        /// </summary>
        protected override int UserAreaSize => PageSize;

        protected override byte SetSecurityCommand => Cmd.SetSecurityBit;

        /// <summary>
        /// The four serial-number words, from the datasheet's Serial Number section. Same shape as the
        /// D2x generation — word 0 apart from a contiguous trio — but a different region and different
        /// offsets within it, which is why each generation writes its own addresses out.
        /// </summary>
        private static readonly uint[] SerialNumberAddresses =
            { 0x008061FC, 0x00806010, 0x00806014, 0x00806018 };

        protected override uint[] UniqueIdWordAddresses => SerialNumberAddresses;

        public override bool GetSecurity() => (Monitor.ReadByte(DsuStatusBAddress) & DsuStatusBProtMask) != 0;

        protected override bool IsReady() => (ReadReg16(Reg.Status) & StatusReadyMask) != 0;

        protected override void WriteCommandRegister(byte cmd) => WriteReg32(Reg.CtrlB, ExecuteKey | cmd);

        /// <summary>ADDR takes byte addresses on this generation, so no conversion is needed.</summary>
        protected override void WriteAddress(uint byteAddress) => WriteReg32(Reg.Addr, byteAddress);

        protected override void ThrowIfCommandError(string operation)
        {
            if ((ReadReg16(Reg.IntFlag) & IntFlagErrorMask) == 0)
                return;

            WriteReg16(Reg.IntFlag, (ushort)IntFlagErrorMask);  // clear the error bits
            throw new SamBaFlashCommandException("NVMCTRL command failed.", operation, isCommandError: true);
        }

        protected override void ConfigureManualWrite()
        {
            WriteReg16(Reg.CtrlA,
                (ushort)((ReadReg16(Reg.CtrlA) | CtrlaCacheDisableMask) & ~CtrlaWriteModeMask));
        }

        protected override void WriteUserArea(byte[] userPage)
        {
            // Erase the user page, then rewrite it a quad word at a time. Manual write mode is
            // already in force: it is configuration, set once in OnInitialize, not per-command state.
            WriteAddress(UserAreaAddress);
            Execute(Cmd.ErasePage, "user-page erase (EP)");

            for (int offset = 0; offset < userPage.Length; offset += QuadWordBytes)
            {
                Execute(Cmd.PageBufferClear, nameof(WriteUserArea));

                uint address = UserAreaAddress + (uint)offset;
                WaitReady(nameof(WriteUserArea));
                LoadLatch(address, userPage, offset, QuadWordBytes);

                WriteAddress(address);
                Execute(Cmd.WriteQuadWord, "user-page write (WQW)");
            }
        }

        private ushort ReadReg16(byte reg)
        {
            return (ushort)(Monitor.ReadByte(NvmctrlBaseAddress + reg) | (Monitor.ReadByte(NvmctrlBaseAddress + reg + 1) << 8));
        }

        private void WriteReg16(byte reg, ushort value)
        {
            Monitor.WriteByte(NvmctrlBaseAddress + reg, (byte)(value & 0xFF));
            Monitor.WriteByte(NvmctrlBaseAddress + reg + 1, (byte)(value >> 8));
        }

        private void WriteReg32(byte reg, uint value) => Monitor.WriteWord(NvmctrlBaseAddress + reg, value);
    }
}
