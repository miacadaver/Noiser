using Android.Content;
using Common;
using Common.Playback;
using Seeker.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Seeker.Playback
{
    /// <summary>Offline recovery data only. Loading this never creates a service/player or contacts a peer.</summary>
    internal static class PlaybackRecovery
    {
        private static readonly object gate = new object();
        private static PlaybackSessionStore store;
        private static SavedPlaybackSession saved;
        private static Task<bool> loading;
        private static long revision;
        private static bool subscribed;
        internal static bool UserSelectionStarted { get; private set; }
        internal static event Action Changed;

        internal static SavedPlaybackSession Saved
        {
            get
            {
                lock (gate) return saved?.Account == PreferencesState.Username ? saved : null;
            }
        }

        private static void EnsureStore(Context context)
        {
            store ??= new PlaybackSessionStore(Path.Combine(context.ApplicationContext.FilesDir.AbsolutePath, "playback-queue-v1.json"));
            if (subscribed) return;
            subscribed = true;
            PreferencesState.UsernameChanged += _ => Forget();
        }

        internal static Task<bool> LoadAsync(Context context)
        {
            lock (gate)
            {
                EnsureStore(context);
                if (loading != null) return loading;
                long expected = revision;
                string account = PreferencesState.Username;
                return loading = Task.Run(() =>
                {
                    var result = store.Load(account);
                    lock (gate)
                    {
                        // A selection, Stop, or account change wins over an older disk read.
                        if (revision != expected || account != PreferencesState.Username || result == null) return false;
                        saved = result;
                        return true;
                    }
                });
            }
        }

        internal static void BeginUserSelection()
        {
            lock (gate) { UserSelectionStarted = true; revision++; }
        }

        internal static void Remember(Context context, PlaybackQueue queue, string account)
        {
            lock (gate)
            {
                EnsureStore(context);
                if (queue.Current == null || string.IsNullOrEmpty(account) || account != PreferencesState.Username) return;
                var snapshot = SavedPlaybackSession.Capture(account, queue);
                revision++;
                try
                {
                    store.Save(snapshot);
                    saved = snapshot;
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException
                    || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException)
                {
                    Logger.Debug("Cannot checkpoint playback queue: " + error.GetType().Name);
                    // Invalid/oversized replacements must not resurrect an unrelated older queue.
                    if (error is InvalidDataException) Forget();
                }
            }
        }

        internal static void Select(Context context, string id)
        {
            var session = Saved;
            if (session == null) return;
            var queue = session.ToQueue();
            if (queue.Select(id) == null) return;
            Remember(context, queue, session.Account);
            NotifyViews();
        }

        internal static bool Append(Context context, PlaybackQueueEntry entry, bool next)
        {
            var session = Saved;
            if (session == null) return false;
            var queue = session.ToQueue();
            if (!(next ? queue.AddNext(entry) : queue.AddLast(entry))) return false;
            Remember(context, queue, session.Account);
            NotifyViews();
            return true;
        }

        internal static void Forget()
        {
            lock (gate)
            {
                revision++;
                saved = null;
                try { store?.Clear(); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { Logger.Debug("Cannot clear saved playback queue: " + error.GetType().Name); }
            }
            NotifyViews();
        }

        private static void NotifyViews()
        {
            if (Changed == null) return;
            foreach (Action observer in Changed.GetInvocationList())
            {
                try { observer(); }
                catch (Exception error) { Logger.Debug("Saved queue view ignored: " + error.GetType().Name); }
            }
        }
    }
}
