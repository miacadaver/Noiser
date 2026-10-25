using Seeker.Playback;

namespace Seeker
{
    public partial class MainActivity
    {
        private StreamingPlaybackController playbackUi;

        internal StreamingPlaybackController EnsurePlaybackUi()
        {
            playbackUi ??= new StreamingPlaybackController(this);
            playbackUi.Attach();
            return playbackUi;
        }

        private void RestorePlaybackUi()
        {
            var ui = EnsurePlaybackUi();
            if (Intent?.GetBooleanExtra(StreamingPlaybackService.OpenPlayerExtra, false) == true)
            {
                Intent.RemoveExtra(StreamingPlaybackService.OpenPlayerExtra);
                ui.ShowCurrent();
            }
        }

        protected override void OnStop()
        {
            playbackUi?.Detach();
            base.OnStop();
        }
    }
}
