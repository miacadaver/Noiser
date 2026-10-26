using Seeker;
using System;
using System.Net.Sockets;

namespace Common.Streaming
{
    /// <summary>Bounded recovery for one live stream; never restarts or replaces its buffer.</summary>
    public static class StreamingRetryPolicy
    {
        public const int MaximumRetries = 3;
        public static TimeSpan RecoveryReadTimeout => TimeSpan.FromSeconds(90);

        public static bool TryGetDelay(Exception exception, int retriesUsed, out TimeSpan delay)
        {
            delay = default;
            if (retriesUsed < 0 || retriesUsed >= MaximumRetries) return false;
            var cause = DownloadFailureClassifier.GetCause(exception);
            if (cause is OperationCanceledException) return false;
            var kind = DownloadFailureClassifier.Classify(exception);
            bool transient = kind == DownloadFailureKind.TimedOut || kind == DownloadFailureKind.CannotConnect
                || kind == DownloadFailureKind.ReportedFailed || kind == DownloadFailureKind.ConnectionClosed
                || kind == DownloadFailureKind.NetworkDown;
            // An existing network recovery may briefly outlive the app's server reconnection.
            // Initial attempts without a login still fail normally; this never initiates a login.
            if (kind == DownloadFailureKind.NotLoggedIn && retriesUsed > 0) transient = true;
            if (kind == DownloadFailureKind.Unknown)
            {
                for (var current = cause; current != null; current = current.InnerException)
                    if (current is SocketException socket && IsTransient(socket.SocketErrorCode))
                    { transient = true; break; }
            }
            if (!transient) return false;
            delay = TimeSpan.FromSeconds(2 << retriesUsed);
            return true;
        }

        private static bool IsTransient(SocketError error) => error == SocketError.NetworkDown
            || error == SocketError.NetworkUnreachable || error == SocketError.NetworkReset
            || error == SocketError.ConnectionAborted || error == SocketError.ConnectionReset
            || error == SocketError.TimedOut || error == SocketError.HostUnreachable
            || error == SocketError.NotConnected;
    }
}
