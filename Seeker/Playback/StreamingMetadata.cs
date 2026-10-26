using Android.Graphics;
using AndroidX.Media3.Common;
using Common.Playback;
using System.IO;

namespace Seeker.Playback
{
    internal static class StreamingMetadata
    {
        public static MediaMetadata BuildSessionMetadata(TrackMetadata track, byte[] thumbnail)
        {
            // The foreground notification exists before extraction finishes. Withhold all identifying
            // fields so controllers cannot capture a provisional title/artist with an empty album.
            if (!track.IsTagReadComplete) return new MediaMetadata.Builder().Build();
            var builder = new MediaMetadata.Builder().SetTitle(track.Title).SetArtist(track.Artist)
                .SetAlbumTitle(track.EmbeddedAlbum).SetAlbumArtist(track.AlbumArtist);
            if (track.TrackNumber.HasValue) builder.SetTrackNumber(Java.Lang.Integer.ValueOf(track.TrackNumber.Value));
            if (track.ReleaseYear.HasValue) builder.SetReleaseYear(Java.Lang.Integer.ValueOf(track.ReleaseYear.Value));
            if (track.DurationMilliseconds.HasValue) builder.SetDurationMs(Java.Lang.Long.ValueOf(track.DurationMilliseconds.Value));
            if (thumbnail != null) builder.SetArtworkData(thumbnail, Java.Lang.Integer.ValueOf(MediaMetadata.PictureTypeFrontCover));
            return builder.Build();
        }

        // Small enough for media-session IPC; original multi-megabyte APIC data stays in-process.
        public static byte[] EncodeSessionArtwork(Bitmap bitmap)
        {
            if (bitmap == null) return null;
            try
            {
                double scale = System.Math.Min(1d, 256d / System.Math.Max(bitmap.Width, bitmap.Height));
                Bitmap small = scale < 1d ? Bitmap.CreateScaledBitmap(bitmap, System.Math.Max(1, (int)(bitmap.Width * scale)),
                    System.Math.Max(1, (int)(bitmap.Height * scale)), true) : null;
                try
                {
                    using var stream = new MemoryStream();
                    if (!(small ?? bitmap).Compress(Bitmap.CompressFormat.Jpeg, 85, stream) || stream.Length > 128 * 1024) return null;
                    return stream.ToArray();
                }
                finally { small?.Dispose(); }
            }
            catch (System.Exception) { return null; }
        }

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
