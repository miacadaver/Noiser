using Common.Streaming;
using NUnit.Framework;
using Soulseek;
using System;
using System.IO;
using System.Net.Sockets;

namespace UnitTestCommon
{
    public class StreamingRetryPolicyTests
    {
        [Test]
        public void BackoffStopsAfterThreeReconnects()
        {
            var failure = new ConnectionException("Remote connection closed");
            for (int retries = 0; retries < 3; retries++)
            {
                Assert.That(StreamingRetryPolicy.TryGetDelay(failure, retries, out var delay), Is.True);
                Assert.That(delay.TotalSeconds, Is.EqualTo(new[] { 2, 4, 8 }[retries]));
            }
            Assert.That(StreamingRetryPolicy.TryGetDelay(failure, 3, out _), Is.False);
        }

        [TestCase(SocketError.NetworkDown)]
        [TestCase(SocketError.NetworkUnreachable)]
        [TestCase(SocketError.NetworkReset)]
        [TestCase(SocketError.ConnectionAborted)]
        [TestCase(SocketError.ConnectionReset)]
        [TestCase(SocketError.TimedOut)]
        [TestCase(SocketError.HostUnreachable)]
        [TestCase(SocketError.NotConnected)]
        public void TemporarySocketFailuresCanRecoverWhenWrapped(SocketError error)
        {
            var failure = new IOException("Peer connection failed", new SocketException((int)error));
            Assert.That(StreamingRetryPolicy.TryGetDelay(failure, 0, out _), Is.True);
        }

        [Test]
        public void PermanentFailuresAndCancellationDoNotRetry()
        {
            Exception[] failures =
            {
                new TransferRejectedException("File not shared"),
                new TransferRejectedException("Banned"),
                new UserOfflineException("User offline"),
                new TransferSizeMismatchException("Size changed", 20, 10),
                new IOException("No space left on device"),
                new IOException("Malformed audio"),
                new UnauthorizedAccessException("Permission denied"),
                new OperationCanceledException(),
                new SocketException((int)SocketError.AccessDenied),
                new InvalidOperationException("Client must be logged in")
            };
            foreach (var failure in failures)
                Assert.That(StreamingRetryPolicy.TryGetDelay(failure, 0, out _), Is.False, failure.ToString());
        }

        [Test]
        public void ServerLoginWaitIsAllowedOnlyDuringExistingRecovery()
        {
            var failure = new InvalidOperationException("Client must be logged in");
            Assert.That(StreamingRetryPolicy.TryGetDelay(failure, 0, out _), Is.False);
            Assert.That(StreamingRetryPolicy.TryGetDelay(failure, 1, out _), Is.True);
            Assert.That(StreamingRetryPolicy.TryGetDelay(failure, 3, out _), Is.False);
        }
    }
}
