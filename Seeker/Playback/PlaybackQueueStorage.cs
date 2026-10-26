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
        private const string PositionPreferencesFile = "noiser_playback_position";

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

        internal static void Save(Context context, PlaybackQueue queue, PlaybackRepeatMode repeatMode, bool autoplay, long position = 0)
        {
            try
            {
                string json = PlaybackQueuePersistence.Serialize(queue, repeatMode, autoplay, position);
                var editor = context.GetSharedPreferences(PreferencesFile, FileCreationMode.Private).Edit();
                if (json == null)
                {
                    editor.Remove(SnapshotKey);
                    context.GetSharedPreferences(PositionPreferencesFile, FileCreationMode.Private).Edit().Clear().Apply();
                }
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

        internal static long LoadPosition(Context context, PlaybackQueueEntry entry, long fallback)
        {
            try
            {
                var preferences = context.GetSharedPreferences(PositionPreferencesFile, FileCreationMode.Private);
                if (preferences.GetString("id", null) != entry.Id || preferences.GetString("peer", null) != entry.Username
                    || preferences.GetString("path", null) != entry.RemotePath || preferences.GetLong("size", -1) != entry.Size) return fallback;
                long position = preferences.GetLong("position", fallback);
                return PendingPlaybackResume.IsValidPosition(position) ? position : fallback;
            }
            catch (Exception exception) { Logger.Debug("Cannot restore position: " + exception.Message); return fallback; }
        }

        internal static void SavePosition(Context context, PlaybackQueueEntry entry, long position)
        {
            try
            {
                context.GetSharedPreferences(PositionPreferencesFile, FileCreationMode.Private).Edit()
                    .PutString("id", entry.Id).PutString("peer", entry.Username).PutString("path", entry.RemotePath)
                    .PutLong("size", entry.Size).PutLong("position", position).Apply();
            }
            catch (Exception exception) { Logger.Debug("Cannot save position: " + exception.Message); }
        }

        private static void Clear(Context context)
        {
            try { context.GetSharedPreferences(PreferencesFile, FileCreationMode.Private).Edit().Remove(SnapshotKey).Apply(); }
            catch (Exception exception) { Logger.Debug("Cannot clear saved queue: " + exception.Message); }
            try { context.GetSharedPreferences(PositionPreferencesFile, FileCreationMode.Private).Edit().Clear().Apply(); }
            catch (Exception exception) { Logger.Debug("Cannot clear position: " + exception.Message); }
        }
    }
}
