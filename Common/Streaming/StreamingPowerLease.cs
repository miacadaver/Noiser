using Soulseek;
using System;

namespace Common.Streaming
{
    /// <summary>A bounded transfer-only lease. Audio playback owns its separate wake protection.</summary>
    public sealed class StreamingPowerLease
    {
        public static TimeSpan IdleTimeout => TimeSpan.FromSeconds(20);
        private readonly object sync = new object();
        private TransferStates state;
        private bool eligible;
        private long received;
        private TimeSpan expires;

        public void StateChanged(TransferStates next, TimeSpan now)
        {
            lock (sync)
            {
                bool active = (next & (TransferStates.Requested | TransferStates.Initializing | TransferStates.InProgress)) != 0
                    && (next & (TransferStates.Queued | TransferStates.Completed)) == 0;
                if (active && next != state) expires = now + IdleTimeout;
                eligible = active;
                state = next;
            }
        }

        /// <returns>True when fresh bytes renew an expired lease and the owner should check its locks.</returns>
        public bool Progress(long available, TimeSpan now)
        {
            lock (sync)
            {
                if (available <= received) return false;
                received = available;
                if (!eligible) return false;
                bool expired = now >= expires;
                expires = now + IdleTimeout;
                return expired;
            }
        }

        public TimeSpan Remaining(TimeSpan now)
        {
            lock (sync) return eligible && expires > now ? expires - now : TimeSpan.Zero;
        }
    }
}
