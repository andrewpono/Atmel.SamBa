using Anp.Atmel.SamBa.Events;
using Anp.Atmel.SamBa.Exceptions;
using System;

namespace Anp.Atmel.SamBa.Flash
{
    /// <summary>
    /// High-level flash workflows over a <see cref="FlashController"/>: erase → block-wise write
    /// (with read-modify-write for partial blocks) → verify (read the region back and compare it
    /// against the image) → options last. Progress is reported through a callback supplied by the
    /// owning device.
    /// </summary>
    internal sealed class FlashProgrammer
    {
        private readonly FlashController _flash;
        private readonly Action<SamBaProgressEventArgs> _progress;

        /// <summary>
        /// Works through <paramref name="flash"/> alone, monitor included: every read and write of
        /// flash goes through the controller, so a controller that has to reach flash some other way
        /// than a plain block read — the word-by-word route the EEFC takes, whose ROM answers block
        /// reads with zeros — changes this class's behaviour without this class knowing there was a
        /// choice.
        /// </summary>
        internal FlashProgrammer(FlashController flash, Action<SamBaProgressEventArgs> progress)
        {
            _flash = flash;
            _progress = progress ?? (_ => { });
        }

        /// <summary>Erases all flash from <paramref name="offset"/> and disables per-block auto-erase.</summary>
        public void EraseAll(uint offset)
        {
            _progress(SamBaProgressEventArgs.Indeterminate("Erasing flash", ProgressStage.Erasing));
            _flash.EraseAll(offset);
            // Flash is blank now; per-block auto-erase would only slow the write down.
            _flash.SetAutoErase(false);
        }

        /// <summary>
        /// Writes <paramref name="data"/> to flash at <paramref name="offset"/> one write block at a
        /// time.
        /// </summary>
        /// <param name="data">Raw firmware image.</param>
        /// <param name="offset">
        /// Byte offset into flash; must be a multiple of <c>FlashController.WriteBlockSize</c>, since
        /// a block is the smallest thing that can be written without disturbing its neighbours.
        /// </param>
        /// <param name="assumeErased">
        /// True when the target range is known blank (after <see cref="EraseAll"/> or with
        /// auto-erasing writes): a partial final block is padded with 0xFF. Otherwise the
        /// existing block content is read back and merged (Samba Lite's read-modify-write).
        /// </param>
        public void Write(byte[] data, uint offset, bool assumeErased)
        {
            int blockSize = _flash.WriteBlockSize;
            ValidateRange(data, offset);

            // Merging into flash that is not blank means programming over old content, so the erase
            // has to come from the write itself. Auto-erase starts on, but an EraseAll turns it off
            // and that outlives the call — a second write on the same open device would otherwise
            // program without erasing. Set before the guard below, which judges the mode in force.
            if (!assumeErased)
                _flash.SetAutoErase(true);

            _flash.EnsureWriteSupported(offset, data.Length);

            int firstBlock = (int)(offset / blockSize);
            int numBlocks = (data.Length + blockSize - 1) / blockSize;

            _progress(SamBaProgressEventArgs.Indeterminate(
                $"Writing {numBlocks} blocks of {blockSize} bytes starting at address " +
                $"0x{_flash.FlashAddress + offset:X8}",
                ProgressStage.Writing));

            var blockBuffer = new byte[blockSize];
            for (int blockNum = 0; blockNum < numBlocks; blockNum++)
            {
                int dataOffset = blockNum * blockSize;
                int count = Math.Min(blockSize, data.Length - dataOffset);

                if (count == blockSize)
                {
                    Buffer.BlockCopy(data, dataOffset, blockBuffer, 0, blockSize);
                }
                else if (assumeErased)
                {
                    _progress(SamBaProgressEventArgs.Indeterminate(
                        $"Padding block {firstBlock + blockNum}: writing {count} B, padding the trailing {blockSize - count} B with 0xFF",
                        ProgressStage.Writing));
                    Buffer.BlockCopy(data, dataOffset, blockBuffer, 0, count);
                    for (int i = count; i < blockSize; i++)
                        blockBuffer[i] = 0xFF;  // erased flash reads as 0xFF
                }
                else
                {
                    // Partial block into non-blank flash: merge with the current content.
                    _progress(SamBaProgressEventArgs.Indeterminate(
                        $"Read-modify-write on block {firstBlock + blockNum}: writing {count} B, preserving the trailing {blockSize - count} B of existing flash",
                        ProgressStage.Writing));
                    _flash.ReadBlock(firstBlock + blockNum, blockBuffer, 0);
                    Buffer.BlockCopy(data, dataOffset, blockBuffer, 0, count);
                }

                _flash.WriteBlock(firstBlock + blockNum, blockBuffer, 0);
                _progress(new SamBaProgressEventArgs(
                    blockNum + 1, numBlocks, stage: ProgressStage.Writing, units: "blocks"));
            }
        }

        /// <summary>
        /// Writes <paramref name="data"/> to flash starting at byte <paramref name="offset"/>, which
        /// need not be aligned to anything. Each affected write block is erased before it is written
        /// (auto-erase), and any block only partially covered by <paramref name="data"/> — a
        /// non-aligned first block or a short final one — is read back and merged so the surrounding
        /// flash is preserved. For full-image programming use <see cref="Write"/> after an
        /// <see cref="EraseAll"/> instead (bulk erase, no per-block erase, and no 16 KB EEFC
        /// auto-erase limit).
        /// </summary>
        /// <remarks>
        /// Working in whole blocks is what makes the preservation promise true on every family. A
        /// block is the smallest span a controller can replace, so merging at page granularity would
        /// leave NVMCTRL parts — where one erase clears 4 or 16 pages — writing a page while blanking
        /// the rest of its row or block.
        /// </remarks>
        public void WriteBytes(byte[] data, uint offset)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length == 0)
                return;

            int blockSize = _flash.WriteBlockSize;
            ValidateFitsInFlash(data, offset);

            // A standalone byte write must erase what it programs; keep it self-sufficient.
            _flash.SetAutoErase(true);
            _flash.EnsureWriteSupported(offset, data.Length);

            long endOffset = (long)offset + data.Length;   // exclusive
            int firstBlock = (int)(offset / blockSize);
            int lastBlock = (int)((endOffset - 1) / blockSize);
            int numBlocks = lastBlock - firstBlock + 1;

            var blockBuffer = new byte[blockSize];
            for (int b = 0; b < numBlocks; b++)
            {
                int block = firstBlock + b;
                long blockBase = (long)block * blockSize;            // this block's byte offset in flash
                long copyStart = Math.Max(offset, blockBase);
                long copyEnd = Math.Min(endOffset, blockBase + blockSize);
                int copyLen = (int)(copyEnd - copyStart);
                int blockInner = (int)(copyStart - blockBase);       // destination within the block
                int dataInner = (int)(copyStart - offset);           // source within data

                if (copyLen == blockSize)
                {
                    Buffer.BlockCopy(data, dataInner, blockBuffer, 0, blockSize);
                }
                else
                {
                    // Partially-covered block: preserve the flash around the written slice.
                    _progress(SamBaProgressEventArgs.Indeterminate(
                        $"Read-modify-write on block {block}: writing {copyLen} B at block offset 0x{blockInner:X}, " +
                        $"preserving the surrounding {blockSize - copyLen} B of existing flash",
                        ProgressStage.Writing));
                    _flash.ReadBlock(block, blockBuffer, 0);
                    Buffer.BlockCopy(data, dataInner, blockBuffer, blockInner, copyLen);
                }

                _flash.WriteBlock(block, blockBuffer, 0);
                _progress(new SamBaProgressEventArgs(
                    b + 1, numBlocks, stage: ProgressStage.Writing, units: "blocks"));
            }
        }

        /// <summary>
        /// Verifies flash content against <paramref name="data"/> by reading the written region
        /// back in a single stream and comparing the two images. Read through
        /// <see cref="FlashController.ReadRange"/> like every other flash read here — the USB read
        /// quirks (chunking, the multiple-of-64 transfer trim) are handled under it.
        /// </summary>
        /// <exception cref="SamBaVerificationException">Content differs.</exception>
        public void Verify(byte[] data, uint offset)
        {
            // No alignment requirement: this only reads, and a read starts wherever it is asked to —
            // so an image written by WriteBytes at an arbitrary offset can still be verified.
            ValidateFitsInFlash(data, offset);
            if (data.Length == 0)
                return;

            _progress(SamBaProgressEventArgs.Indeterminate("Verifying flash", ProgressStage.Verifying));

            var readback = new byte[data.Length];
            _progress(SamBaProgressEventArgs.Indeterminate($"Reading {data.Length} flash bytes at offset {offset}", ProgressStage.Verifying));
            _flash.ReadRange(offset, readback, 0, data.Length);

            _progress(SamBaProgressEventArgs.Indeterminate("Comparing readback to source data", ProgressStage.Verifying));
            Compare(data, readback, offset);

            _progress(SamBaProgressEventArgs.Indeterminate("Flash verification passed", ProgressStage.Verifying));
        }

        private void Compare(byte[] data, byte[] readback, uint offset)
        {
            for (int i = 0; i < data.Length; i++)
            {
                if (readback[i] == data[i])
                    continue;

                uint address = _flash.FlashAddress + offset + (uint)i;

                string message = $"Verification failed at offset {i} (address 0x{address:X8}): " +
                    $"expected 0x{data[i]:X2}, read 0x{readback[i]:X2}.";

                // Parts whose monitor cannot read flash are already read word-by-word
                // (FlashController.ReadRequiresWords), so on a supported part this now points at
                // the write; the read-defect half of the hint stays for parts not yet so flagged.
                if (IsAllZeros(readback) && !IsAllZeros(data))
                {
                    message += " The region read back as all zeros: either the write did not take " +
                        "effect, or this part's monitor cannot read flash directly and the flash " +
                        "content is correct even though it cannot be verified this way.";
                }

                throw new SamBaVerificationException(message, i, address);
            }
        }

        // Only ever runs on the verify failure path, so the per-element delegate call costs nothing
        // that matters and the intent reads straight off the line.
        private static bool IsAllZeros(byte[] buffer) => Array.TrueForAll(buffer, b => b == 0);

        /// <summary>
        /// Runs the checks <see cref="Write"/> makes on the way in — block-aligned offset, image
        /// fits in flash from there — without touching the device. Lets a caller that erases before
        /// writing reject a bad image while the flash is still intact: <see cref="EraseAll"/> only
        /// validates the offset it is given, so an image too long for the space left after it would
        /// otherwise be caught by <see cref="Write"/> once the erase had already blanked the part.
        /// </summary>
        public void ValidateImage(byte[] data, uint offset) => ValidateRange(data, offset);

        /// <summary>
        /// Validates a block-aligned write of the whole of <paramref name="data"/> at
        /// <paramref name="offset"/>. Takes the array rather than its length so it cannot be called
        /// with the offset and the length the wrong way round.
        /// </summary>
        private void ValidateRange(byte[] data, uint offset)
        {
            if (offset % (uint)_flash.WriteBlockSize != 0)
                throw new ArgumentOutOfRangeException(
                    nameof(offset), $"Offset must be a multiple of the {_flash.WriteBlockSize}-byte write block.");

            ValidateFitsInFlash(data, offset);
        }

        /// <summary>
        /// Throws when <paramref name="data"/> written at <paramref name="offset"/> would run past
        /// the end of flash. The whole of <see cref="ValidateRange"/> that also applies to the two
        /// paths with nothing to align: <see cref="WriteBytes"/> and <see cref="Verify"/>.
        /// </summary>
        private void ValidateFitsInFlash(byte[] data, uint offset)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (offset + (ulong)data.Length > (ulong)_flash.FlashSize)
                throw new ArgumentOutOfRangeException(
                    nameof(data), $"Data ({data.Length} bytes at offset 0x{offset:X}) exceeds the {_flash.FlashSize}-byte flash.");
        }
    }
}
