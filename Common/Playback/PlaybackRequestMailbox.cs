using System;
using System.Collections.Generic;

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

    /// <summary>FIFO mailbox for independent queue additions; unlike replacement requests none supersede another.</summary>
    public sealed class PlaybackAppendMailbox<T> where T : class
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, T> requests = new Dictionary<string, T>(StringComparer.Ordinal);

        public string Publish(T value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            lock (sync)
            {
                string key = Guid.NewGuid().ToString("N");
                requests.Add(key, value);
                return key;
            }
        }

        public bool TryTake(string key, out T value)
        {
            lock (sync)
            {
                if (key != null && requests.TryGetValue(key, out value))
                { requests.Remove(key); return true; }
                value = null;
                return false;
            }
        }
    }
}
