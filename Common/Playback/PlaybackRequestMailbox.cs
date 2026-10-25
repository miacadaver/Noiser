using System;

namespace Common.Playback
{
    /// <summary>One user-selected request, consumed once. No replay or recovery after process death.</summary>
    public sealed class PlaybackRequestMailbox<T> where T : class
    {
        private readonly object sync = new object();
        private string? key;
        private T? request;

        public string Publish(T value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            lock (sync)
            {
                key = Guid.NewGuid().ToString("N");
                request = value;
                return key;
            }
        }

        public bool TryTake(string? requestedKey, out T? value)
        {
            lock (sync)
            {
                value = null;
                if (requestedKey == null || requestedKey != key || request == null) return false;
                value = request;
                request = null;
                key = null;
                return true;
            }
        }
    }
}
