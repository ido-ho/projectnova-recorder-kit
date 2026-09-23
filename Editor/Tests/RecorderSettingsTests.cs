#if NOVA_UNITY_RECORDER_TESTS
using NUnit.Framework;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;

namespace ProjectNova.RecorderKit.Tests
{
    public class RecorderSettingsTests
    {
        [Test]
        public void BuildSettings_ProducesVerticalHDManualMovie()
        {
            var (controller, movie) = UnityRecorderDriver.BuildSettings("test_clip");
            Assert.AreEqual(60f, controller.FrameRate);
            Assert.IsTrue(controller.CapFrameRate);
            var input = (GameViewInputSettings)movie.ImageInputSettings;
            Assert.AreEqual(1080, input.OutputWidth);
            Assert.AreEqual(1920, input.OutputHeight);
            Assert.IsTrue(movie.CaptureAudio);
            Assert.AreEqual("test_clip_<Take>", movie.FileNameGenerator.FileName);
        }
    }
}
#endif
