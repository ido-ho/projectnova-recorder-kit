using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    public class FfmpegArgsTests
    {
        [Test]
        public void BuildFfmpegArgs_AssemblesFramesToMp4()
        {
            var args = ScreenCaptureDriver.BuildFfmpegArgs("/tmp/frames", "Recordings/clip_fallback.mp4", 60);
            Assert.AreEqual(
                "-y -framerate 60 -i /tmp/frames/frame_%05d.png -c:v libx264 -pix_fmt yuv420p Recordings/clip_fallback.mp4",
                args);
        }

        [Test]
        public void FramePath_IsZeroPadded()
        {
            Assert.AreEqual("/tmp/frames/frame_00042.png",
                ScreenCaptureDriver.FramePath("/tmp/frames", 42));
        }
    }
}
