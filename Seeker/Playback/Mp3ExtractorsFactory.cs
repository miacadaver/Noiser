using AndroidX.Media3.Extractor;

namespace Seeker.Playback
{
    internal sealed class Mp3ExtractorsFactory : Java.Lang.Object, IExtractorsFactory
    {
        private readonly bool disableSeeking;
        public Mp3ExtractorsFactory(bool disableSeeking = true) { this.disableSeeking = disableSeeking; }

        public IExtractor[] CreateExtractors()
        {
            return new IExtractor[] { new BoundedMp3Extractor(disableSeeking) };
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
