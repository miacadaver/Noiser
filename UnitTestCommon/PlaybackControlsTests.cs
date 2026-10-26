using Common.Playback;
using NUnit.Framework;

namespace UnitTestCommon
{
    [TestFixture]
    public class PlaybackControlsTests
    {
        [Test]
        public void RepeatCyclesThroughExactlyOneMode()
        {
            var controls = new PlaybackControls();
            Assert.That(controls.RepeatMode, Is.EqualTo(PlaybackRepeatMode.Off));
            Assert.That(controls.Autoplay, Is.True);

            controls.CycleRepeatMode();
            Assert.That(controls.RepeatMode, Is.EqualTo(PlaybackRepeatMode.All));
            Assert.That(controls.Autoplay, Is.True);

            controls.CycleRepeatMode();
            Assert.That(controls.RepeatMode, Is.EqualTo(PlaybackRepeatMode.One));
            Assert.That(controls.Autoplay, Is.True);

            controls.CycleRepeatMode();
            Assert.That(controls.RepeatMode, Is.EqualTo(PlaybackRepeatMode.Off));
            Assert.That(controls.Autoplay, Is.True);
        }

        [Test]
        public void TurningAutoplayOffClearsRepeatSoModesStayConsistent()
        {
            var controls = new PlaybackControls();
            controls.CycleRepeatMode();
            controls.CycleRepeatMode();
            Assert.That(controls.RepeatMode, Is.EqualTo(PlaybackRepeatMode.One));

            controls.ToggleAutoplay();
            Assert.That(controls.Autoplay, Is.False);
            Assert.That(controls.RepeatMode, Is.EqualTo(PlaybackRepeatMode.Off));
        }

        [Test]
        public void SelectingRepeatTurnsAutoplayBackOn()
        {
            var controls = new PlaybackControls();
            controls.ToggleAutoplay();
            Assert.That(controls.Autoplay, Is.False);

            controls.CycleRepeatMode();
            Assert.That(controls.RepeatMode, Is.EqualTo(PlaybackRepeatMode.All));
            Assert.That(controls.Autoplay, Is.True);
        }
    }
}
