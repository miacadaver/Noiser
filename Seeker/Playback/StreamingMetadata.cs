using Android.Graphics;
using AndroidX.Media3.Common;
using Common.Playback;

namespace Seeker.Playback
{
    internal static class StreamingMetadata
    {
        // Called on a worker. Media3 has already decoded ID3v2 text encodings and preferred cover art.
        public static TrackMetadataValues Read(MediaMetadata metadata)
        {
            var encoded = metadata.ArtworkData;
            byte[] artwork = null;
            if (encoded?.Count > 0 && encoded.Count <= TrackMetadata.MaximumArtworkBytes)
            {
                artwork = new byte[encoded.Count];
                encoded.CopyTo(artwork, 0);
            }
            int? year = metadata.ReleaseYear?.IntValue();
            if (year == null || year < 1000 || year > 9999) year = metadata.RecordingYear?.IntValue();
            return new TrackMetadataValues
            {
                Title = metadata.Title?.ToString(),
                Artist = metadata.Artist?.ToString(),
                Album = metadata.AlbumTitle?.ToString(),
                AlbumArtist = metadata.AlbumArtist?.ToString(),
                TrackNumber = metadata.TrackNumber?.IntValue(),
                ReleaseYear = year,
                ArtworkData = artwork
            };
        }

        public static Bitmap DecodeArtwork(byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > TrackMetadata.MaximumArtworkBytes) return null;
            try
            {
                using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeByteArray(data, 0, data.Length, bounds)?.Dispose();
                if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0 || bounds.OutWidth > 32768
                    || bounds.OutHeight > 32768 || (long)bounds.OutWidth * bounds.OutHeight > 64000000) return null;
                int sample = 1;
                while ((bounds.OutWidth + sample - 1) / sample > 512 || (bounds.OutHeight + sample - 1) / sample > 512)
                    sample *= 2;
                using var options = new BitmapFactory.Options { InSampleSize = sample };
                return BitmapFactory.DecodeByteArray(data, 0, data.Length, options);
            }
            catch (System.Exception) { return null; }
        }
    }
}
