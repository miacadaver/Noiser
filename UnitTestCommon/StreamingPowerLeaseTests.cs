using Common.Streaming;
using NUnit.Framework;
using Soulseek;
using System;

namespace UnitTestCommon
{
    public class StreamingPowerLeaseTests
    {
        private static TimeSpan At(int seconds) => TimeSpan.FromSeconds(seconds);

        [Test]
        public void WaitingForPredecessorDoesNotProtectAnUnstartedTransfer()
        {
            Assert.That(new StreamingPowerLease().Remaining(At(0)), Is.EqualTo(TimeSpan.Zero));
        }

        [TestCase(TransferStates.Requested)]
        [TestCase(TransferStates.Initializing)]
        [TestCase(TransferStates.InProgress)]
        public void ActiveWorkHasABoundedGracePeriod(TransferStates state)
        {
            var lease = new StreamingPowerLease();
            lease.StateChanged(state, At(100));
            Assert.That(lease.Remaining(At(119)), Is.EqualTo(At(1)));
            Assert.That(lease.Remaining(At(120)), Is.EqualTo(TimeSpan.Zero));
            lease.StateChanged(state, At(130));
            Assert.That(lease.Remaining(At(130)), Is.EqualTo(TimeSpan.Zero), "duplicate state reports cannot keep a stall awake");
        }

        [TestCase(TransferStates.Queued | TransferStates.Remotely)]
        [TestCase(TransferStates.Queued | TransferStates.Locally)]
        [TestCase(TransferStates.Completed | TransferStates.Succeeded)]
        [TestCase(TransferStates.Completed | TransferStates.Errored)]
        [TestCase(TransferStates.Completed | TransferStates.Cancelled)]
        [TestCase(TransferStates.None)]
        public void InactiveStatesRevokeProtectionAndLateProgressCannotRestoreIt(TransferStates state)
        {
            var lease = new StreamingPowerLease();
            lease.StateChanged(TransferStates.InProgress, At(0));
            lease.StateChanged(state, At(1));
            Assert.That(lease.Remaining(At(1)), Is.EqualTo(TimeSpan.Zero));
            Assert.That(lease.Progress(100, At(2)), Is.False);
            Assert.That(lease.Remaining(At(2)), Is.EqualTo(TimeSpan.Zero));
        }

        [Test]
        public void OnlyNewBytesRenewProtectionAndResumeNotifiesOwnerOnce()
        {
            var lease = new StreamingPowerLease();
            lease.StateChanged(TransferStates.InProgress, At(0));
            Assert.That(lease.Progress(100, At(10)), Is.False);
            Assert.That(lease.Remaining(At(29)), Is.EqualTo(At(1)));
            lease.Progress(100, At(29));
            lease.Progress(0, At(29));
            Assert.That(lease.Remaining(At(30)), Is.EqualTo(TimeSpan.Zero));
            Assert.That(lease.Progress(200, At(40)), Is.True);
            Assert.That(lease.Progress(300, At(41)), Is.False);
            Assert.That(lease.Remaining(At(60)), Is.EqualTo(At(1)));
        }

        [Test]
        public void RecoveryReleasesDuringBackoffAndReacquiresForNewAttempt()
        {
            var lease = new StreamingPowerLease();
            lease.StateChanged(TransferStates.InProgress, At(0));
            lease.Progress(100, At(5));
            lease.StateChanged(TransferStates.Completed | TransferStates.TimedOut, At(6));
            lease.StateChanged(TransferStates.Queued, At(6));
            Assert.That(lease.Remaining(At(7)), Is.EqualTo(TimeSpan.Zero));
            lease.StateChanged(TransferStates.Requested, At(8));
            Assert.That(lease.Remaining(At(8)), Is.EqualTo(StreamingPowerLease.IdleTimeout));
            lease.StateChanged(TransferStates.Queued | TransferStates.Remotely, At(9));
            Assert.That(lease.Remaining(At(10)), Is.EqualTo(TimeSpan.Zero));
            lease.StateChanged(TransferStates.Initializing, At(100));
            Assert.That(lease.Remaining(At(100)), Is.EqualTo(StreamingPowerLease.IdleTimeout));
            lease.StateChanged(TransferStates.InProgress, At(101));
            lease.Progress(200, At(110));
            Assert.That(lease.Remaining(At(129)), Is.EqualTo(At(1)));
        }
    }
}
