using Seeker.Playback;
using Seeker.Helpers;
using System;
using Android.Content;
using AndroidX.Lifecycle;

namespace Seeker
{
    public partial class MainActivity
    {
        private StreamingPlaybackController playbackUi;
        private bool recoveryLoading;
        private bool recoveryNavigationHandled;

        protected override void OnPostResume()
        {
            base.OnPostResume();
            RestoreSavedQueueAsync();
        }

        internal StreamingPlaybackController EnsurePlaybackUi()
        {
            playbackUi ??= new StreamingPlaybackController(this);
            playbackUi.Attach();
            return playbackUi;
        }

        private void RestorePlaybackUi()
        {
            var ui = EnsurePlaybackUi();
            RestoreSavedQueueAsync();
            if (Intent?.GetBooleanExtra(StreamingPlaybackService.OpenPlayerExtra, false) == true)
            {
                Intent.RemoveExtra(StreamingPlaybackService.OpenPlayerExtra);
                ui.ShowCurrent();
            }
        }

        private async void RestoreSavedQueueAsync()
        {
            if (recoveryLoading || recoveryNavigationHandled) return;
            recoveryLoading = true;
            try
            {
                bool restored = await PlaybackRecovery.LoadAsync(this);
                if (IsFinishing || IsDestroyed || !Lifecycle.CurrentState.IsAtLeast(AndroidX.Lifecycle.Lifecycle.State.Started)) return;
                recoveryNavigationHandled = true;
                playbackUi?.RefreshRecoveredQueue();
                var saved = PlaybackRecovery.Saved;
                if (!restored || saved == null || PlaybackRecovery.UserSelectionStarted || StreamingPlaybackService.Current?.Playback != null) return;
                bool opened = BrowseFragment.TryRestorePlaybackFolder(saved);
                // Do not override a notification, deep link, restored tab, or a browse
                // response the user requested while the private file was being read.
                if (opened && Intent?.Action == Android.Content.Intent.ActionMain && pager.CurrentItem == 0)
                    pager.SetCurrentItem(3, false);
            }
            catch (Exception error)
            {
                recoveryNavigationHandled = true;
                Logger.Debug("Saved playback UI ignored: " + error.GetType().Name);
            }
            finally { recoveryLoading = false; }
        }

        protected override void OnStop()
        {
            playbackUi?.Detach();
            base.OnStop();
        }
    }
}
