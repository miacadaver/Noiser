using System;
using Common.Playback;
using NUnit.Framework;

namespace UnitTestCommon
{
    public class Id3TagHeaderTests
    {
        [TestCase(3, 0)]
        [TestCase(3, 1000)]
        [TestCase(4, 1000000)]
        [TestCase(4, 268435455)]
        public void SynchsafeSizeIncludesTheWholeBody(int version, int bodyLength)
        {
            Assert.That(Id3TagHeader.GetTotalLength(Header(version, bodyLength)), Is.EqualTo(bodyLength + 10));
        }

        [Test]
        public void LargeArtworkTagIsRecognizedWithoutAllocatingItsPayload()
        {
            byte[] header = Header(4, 32 * 1024 * 1024);
            Assert.That(header.Length, Is.EqualTo(10));
            Assert.That(Id3TagHeader.GetTotalLength(header), Is.GreaterThan(Id3TagHeader.MaximumTagBytes));
        }

        [Test]
        public void PartialHeaderIsInvalid()
        {
            for (int length = 0; length < 10; length++)
                Assert.That(Id3TagHeader.GetTotalLength(new byte[length]), Is.EqualTo(-1));
        }

        [Test]
        public void AudioWithoutId3ReturnsNoTag()
        {
            byte[] audio = { 255, 251, 144, 0, 0, 0, 0, 0, 0, 0 };
            Assert.That(Id3TagHeader.GetTotalLength(audio), Is.Zero);
        }

        [TestCase(6)]
        [TestCase(7)]
        [TestCase(8)]
        [TestCase(9)]
        public void NonSynchsafeSizeBytesAreRejected(int position)
        {
            var header = Header(4, 1000);
            header[position] |= 128;
            Assert.That(Id3TagHeader.GetTotalLength(header), Is.EqualTo(-1));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(5)]
        [TestCase(255)]
        public void UnsupportedVersionIsRejected(int version)
        {
            Assert.That(Id3TagHeader.GetTotalLength(Header(version, 1000)), Is.EqualTo(-1));
        }

        [Test]
        public void ReservedFlagsAreRejectedWhileValidExtendedHeadersAreAccepted()
        {
            var header = Header(3, 1000);
            header[5] = 0xc0;
            Assert.That(Id3TagHeader.GetTotalLength(header), Is.EqualTo(1010));
            header[5] = 0x10;
            Assert.That(Id3TagHeader.GetTotalLength(header), Is.EqualTo(-1));
            header[3] = 4;
            Assert.That(Id3TagHeader.GetTotalLength(header), Is.EqualTo(1010));
        }

        private static byte[] Header(int version, int bodyLength) => new byte[]
        {
            (byte)'I', (byte)'D', (byte)'3', (byte)version, 0, 0,
            (byte)((bodyLength >> 21) & 127), (byte)((bodyLength >> 14) & 127),
            (byte)((bodyLength >> 7) & 127), (byte)(bodyLength & 127)
        };
    }
}
