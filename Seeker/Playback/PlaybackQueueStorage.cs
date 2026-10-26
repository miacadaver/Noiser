using Android.Content;
using Common.Playback;
using Seeker.Helpers;
using System;
using System.IO;

namespace Seeker.Playback
{
    /// <summary>Dedicated private preferences; Apply updates memory immediately and writes asynchronously.</summary>
    internal static class PlaybackQueueStorage
    {
        private const string PreferencesFile = "noiser_playback_queue";
        private const string SnapshotKey = "queue_v1";

        internal static bool HasSavedQueue(Context context)
        {
            try { return context.GetSharedPreferences(PreferencesFile, FileCreationMode.Private).Contains(SnapshotKey); }
            catch (Exception exception) { Logger.Debug("Cannot inspect saved queue: " + exception.Message); return false; }
        }

        internal static SavedPlaybackQueue Load(Context context)
        {
            try
            {
                var preferences = context.GetSharedPreferences(PreferencesFile, FileCreationMode.Private);
                string json = preferences.GetString(SnapshotKey, null);
                if (PlaybackQueuePersistence.TryRestore(json, out var saved)) return saved;
                if (json != null) preferences.Edit().Remove(SnapshotKey).Apply();
            }
            catch (Exception exception)
            {
                Clear(context);
                Logger.Debug("Cannot restore saved queue: " + exception.Message);
            }
            return null;
        }

        internal static void Save(Context context, PlaybackQueue queue, PlaybackRepeatMode repeatMode, bool autoplay)
        {
            try
            {
                string json = PlaybackQueuePersistence.Serialize(queue, repeatMode, autoplay);
                var editor = context.GetSharedPreferences(PreferencesFile, FileCreationMode.Private).Edit();
                if (json == null) editor.Remove(SnapshotKey);
                else editor.PutString(SnapshotKey, json);
                editor.Apply();
            }
            catch (InvalidDataException exception)
            {
                // Do not resurrect an older queue when the replacement exceeds persistence limits.
                Clear(context);
                Logger.Debug("Cannot save queue: " + exception.Message);
            }
            catch (Exception exception) { Logger.Debug("Cannot save queue: " + exception.Message); }
        }

        private static void Clear(Context context)
        {
            try { context.GetSharedPreferences(PreferencesFile, FileCreationMode.Private).Edit().Remove(SnapshotKey).Apply(); }
            catch (Exception exception) { Logger.Debug("Cannot clear saved queue: " + exception.Message); }
        }
    }
}
