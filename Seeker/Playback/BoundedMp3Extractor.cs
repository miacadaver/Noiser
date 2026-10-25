using AndroidX.Media3.Extractor;
using AndroidX.Media3.Extractor.Mp3;
using Common.Playback;

namespace Seeker.Playback
{
    /// <summary>
    /// Keeps Media3's ID3 decoder, but bounds its leading-tag peek buffer before it allocates.
    /// Runs on Media3's loader, using the same data source and cancellation as audio playback.
    /// </summary>
    internal sealed class BoundedMp3Extractor : Java.Lang.Object, IExtractor
    {
        private readonly Mp3Extractor extractor = new Mp3Extractor();
        private bool inspected;
        private int leadingTagBytes;

        public BoundedMp3Extractor(bool disableSeeking)
        {
            if (disableSeeking) extractor.DisableSeeking();
        }

        // This factory is used only for explicitly selected MP3s. Read performs validation so
        // Media3 cannot allocate an unchecked ID3 payload while sniffing.
        public bool Sniff(IExtractorInput input) => true;
        public void Init(IExtractorOutput output) => extractor.Init(output);
        public void Seek(long position, long timeUs)
        {
            if (position == 0) inspected = false;
            extractor.Seek(position, timeUs);
        }
        public void Release() => extractor.Release();

        public int Read(IExtractorInput input, PositionHolder seekPosition)
        {
            if (!inspected)
            {
                InspectLeadingTags(input);
                inspected = true;
            }
            try { return extractor.Read(input, seekPosition); }
            catch (Java.Lang.RuntimeException) when (input.Position == 0 && leadingTagBytes > 0)
            {
                // Some malformed extended headers throw rather than returning empty metadata.
                // Discard only the validated, bounded tag prefix and let Media3 find audio.
                input.ResetPeekPosition();
                input.SkipFully(leadingTagBytes);
                leadingTagBytes = 0;
                return extractor.Read(input, seekPosition);
            }
        }

        private void InspectLeadingTags(IExtractorInput input)
        {
            leadingTagBytes = 0;
            if (input.Position != 0) return;
            var header = new byte[Id3TagHeader.HeaderLength];
            bool discardMetadata = false;
            int searchedBytes = 0;
            input.ResetPeekPosition();
            while (input.Length < 0 || input.PeekPosition + header.Length <= input.Length)
            {
                if (!input.PeekFully(header, 0, header.Length, true)) break;
                int length = Id3TagHeader.GetTotalLength(header);
                if (length == 0)
                {
                    int audioHeader = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                    if (MpegAudioUtil.GetFrameSize(audioHeader) > 0 || ++searchedBytes > 128 * 1024) break;
                    // Media3 also searches past leading junk. Check that prefix too, otherwise
                    // a large tag hidden just after it could bypass the allocation guard.
                    leadingTagBytes++;
                    input.ResetPeekPosition();
                    input.AdvancePeekPosition(leadingTagBytes);
                    continue;
                }
                long tagStart = input.PeekPosition - header.Length;
                bool invalid = length < 0 || (input.Length >= 0 && length > input.Length - tagStart);
                if (invalid || discardMetadata || length > Id3TagHeader.MaximumTagBytes - leadingTagBytes)
                {
                    // Skip in the extractor input's small scratch buffer, never by allocating
                    // the advertised tag length. An impossible size discards just its header.
                    input.ResetPeekPosition();
                    input.SkipFully(leadingTagBytes + (invalid ? header.Length : length));
                    leadingTagBytes = 0;
                    discardMetadata = true;
                }
                else
                {
                    input.AdvancePeekPosition(length - header.Length);
                    leadingTagBytes += length;
                }
            }
            input.ResetPeekPosition();
        }
    }
}
