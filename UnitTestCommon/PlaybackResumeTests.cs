using Common.Playback;
using NUnit.Framework;
using System;
using System.Text.Json.Nodes;

namespace UnitTestCommon
{
    public class PlaybackResumeTests
    {
        [Test]
        public void PositionRoundTripsAndOldQueueSchemaStartsAtZero()
        {
            var queue = new PlaybackQueue();
            queue.AddLast(new PlaybackQueueEntry("peer", "song.mp3", 100));
            string json = PlaybackQueuePersistence.Serialize(queue, PlaybackRepeatMode.Off, true, 123456)!;
            Assert.That(PlaybackQueuePersistence.TryRestore(json, out var saved), Is.True);
            Assert.That(saved!.PositionMilliseconds, Is.EqualTo(123456));
            var legacy = JsonNode.Parse(json)!;
            legacy["Version"] = 1;
            Assert.That(PlaybackQueuePersistence.TryRestore(legacy.ToJsonString(), out saved), Is.True);
            Assert.That(saved!.PositionMilliseconds, Is.Zero);
        }

        [TestCase(-1L)]
        [TestCase(long.MaxValue)]
        public void InvalidPositionsCannotBeWrittenOrRestored(long position)
        {
            var queue = new PlaybackQueue();
            queue.AddLast(new PlaybackQueueEntry("peer", "song.mp3", 100));
            Assert.Throws<ArgumentOutOfRangeException>(() => PlaybackQueuePersistence.Serialize(queue, PlaybackRepeatMode.Off, true, position));
            var json = JsonNode.Parse(PlaybackQueuePersistence.Serialize(queue, PlaybackRepeatMode.Off, true)!)!;
            json["PositionMilliseconds"] = position;
            Assert.That(PlaybackQueuePersistence.TryRestore(json.ToJsonString(), out _), Is.False);
        }

        [Test]
        public void ZeroPositionNeedsNoResumeGate()
        {
            var resume = new PendingPlaybackResume(0);
            Assert.That(resume.IsPending, Is.False);
            Assert.That(resume.NextSeek(new SeekRangeSnapshot(60000000, -1, true), -1), Is.Null);
        }

        [Test]
        public void SavedTargetWaitsForValidatedAudioAndAdvancesThePausedParserInSteps()
        {
            var resume = new PendingPlaybackResume(120000);
            Assert.That(resume.NextSeek(new SeekRangeSnapshot(0, -1, false), -1), Is.Null);
            Assert.That(resume.IsPending, Is.True);
            Assert.That(resume.NextSeek(new SeekRangeSnapshot(59000000, -1, true), -1), Is.EqualTo(59000));
            resume.SeekSucceeded(59000);
            Assert.That(resume.TargetMilliseconds, Is.EqualTo(120000));
            Assert.That(resume.IsPending, Is.True);
            Assert.That(resume.NextSeek(new SeekRangeSnapshot(59000000, -1, true), -1), Is.Null);
            Assert.That(resume.NextSeek(new SeekRangeSnapshot(118000000, -1, true), -1), Is.EqualTo(118000));
            resume.SeekSucceeded(118000);
            Assert.That(resume.NextSeek(new SeekRangeSnapshot(160000000, -1, true), -1), Is.EqualTo(120000));
            Assert.That(resume.IsPending, Is.True, "requesting a seek does not mean the player accepted it");
            resume.SeekSucceeded(120000);
            Assert.That(resume.IsPending, Is.False);
        }

        [Test]
        public void DurationPastEndFallsBackToBeginningWithoutWaitingForUnreachableAudio()
        {
            var resume = new PendingPlaybackResume(120000);
            Assert.That(resume.NextSeek(new SeekRangeSnapshot(59000000, 90000000, true), 90000), Is.Zero);
            resume.SeekSucceeded(0);
            Assert.That(resume.IsPending, Is.False);
            Assert.That(resume.TargetMilliseconds, Is.Zero);
        }

        [Test]
        public void EndOfShortUnseekableFileCanStillResetToBeginning()
        {
            var resume = new PendingPlaybackResume(1000);
            Assert.That(resume.NextSeek(new SeekRangeSnapshot(0, 500000, false), 500), Is.Zero);
            resume.SeekSucceeded(0);
            Assert.That(resume.IsPending, Is.False);
        }

        [Test]
        public void FinalSubsecondTargetDoesNotWaitForAWholeStagingStep()
        {
            var resume = new PendingPlaybackResume(500);
            Assert.That(resume.NextSeek(new SeekRangeSnapshot(1000000, -1, true), -1), Is.EqualTo(500));
        }

        [Test]
        public void SmallIndexUpdatesDoNotCauseRepeatedDecoderFlushes()
        {
            var resume = new PendingPlaybackResume(240000);
            Assert.That(resume.NextSeek(new SeekRangeSnapshot(2000000,-1,true),-1), Is.Null);
            Assert.That(resume.NextSeek(new SeekRangeSnapshot(29000000,-1,true),-1), Is.Null);
            Assert.That(resume.NextSeek(new SeekRangeSnapshot(30000000,-1,true),-1), Is.EqualTo(30000));
            resume.SeekSucceeded(30000);
            Assert.That(resume.NextSeek(new SeekRangeSnapshot(32000000,-1,true),-1), Is.Null);
            Assert.That(resume.NextSeek(new SeekRangeSnapshot(60000000,-1,true),-1), Is.EqualTo(60000));
        }
    }
}
