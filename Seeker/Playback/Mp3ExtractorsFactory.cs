using AndroidX.Media3.Extractor;

namespace Seeker.Playback
{
    internal sealed class Mp3ExtractorsFactory : Java.Lang.Object, IExtractorsFactory
    {
        private readonly bool disableSeeking;
        private readonly System.Action<Common.Playback.SeekRangeSnapshot> seekIndexChanged;
        public Mp3ExtractorsFactory(bool disableSeeking = false,
            System.Action<Common.Playback.SeekRangeSnapshot> seekIndexChanged = null)
        { this.disableSeeking = disableSeeking; this.seekIndexChanged = seekIndexChanged; }

        public IExtractor[] CreateExtractors()
        {
            return new IExtractor[] { new BoundedMp3Extractor(disableSeeking, seekIndexChanged) };
        }

        public IExtractor[] CreateExtractors(Android.Net.Uri uri,
            System.Collections.Generic.IDictionary<string, System.Collections.Generic.IList<string>> responseHeaders) => CreateExtractors();

        // Match ExtractorsFactory's default no-op configuration methods for this MP3-only factory.
        public IExtractorsFactory ExperimentalSetCodecsToParseWithinGopSampleDependencies(int codecs) => this;
        public IExtractorsFactory ExperimentalSetTextTrackTranscodingEnabled(bool enabled) => this;
        public IExtractorsFactory SetParseHagcMetadata(bool enabled) => this;
        public IExtractorsFactory SetSubtitleParserFactory(AndroidX.Media3.Extractor.Text.ISubtitleParserFactory factory) => this;
    }
}
