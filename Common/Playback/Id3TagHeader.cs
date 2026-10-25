using System;

namespace Common.Playback
{
    /// <summary>Allocation guard only. Media3 remains responsible for decoding ID3 frames and encodings.</summary>
    public static class Id3TagHeader
    {
        public const int HeaderLength = 10;
        public const int MaximumTagBytes = 4 * 1024 * 1024;

        // Zero means no ID3 signature; -1 means an invalid/unsupported header.
        public static int GetTotalLength(ReadOnlySpan<byte> header)
        {
            if (header.Length < HeaderLength) return -1;
            if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return 0;
            if (header[3] < 2 || header[3] > 4 || header[4] == 255) return -1;
            int reservedFlags = header[3] == 2 ? 0x3f : header[3] == 3 ? 0x1f : 0x0f;
            if ((header[5] & reservedFlags) != 0) return -1;
            for (int i = 6; i < HeaderLength; i++)
                if ((header[i] & 0x80) != 0) return -1;
            int body = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
            return HeaderLength + body;
        }
    }
}
