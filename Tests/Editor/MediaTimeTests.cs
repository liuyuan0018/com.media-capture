using NUnit.Framework;

namespace GameFramework.MediaCapture.Tests
{
    public sealed class MediaTimeTests
    {
        [Test]
        public void RationalFrameRateDoesNotAccumulateFloatingPointDrift()
        {
            FrameRate rate = new FrameRate(30000, 1001);
            long sample = rate.SampleAtFrame(107892, 48000);

            Assert.That(sample, Is.EqualTo(172799828));
            Assert.That(rate.FrameAtOrBeforeSample(sample, 48000), Is.EqualTo(107892));
        }

        [Test]
        public void ConstantFramePlanDuplicatesLastAvailableFrameAcrossGaps()
        {
            var sources = new[]
            {
                new CapturedFrameTime(0, "a.png"),
                new CapturedFrameTime(8000, "b.png"),
                new CapturedFrameTime(16000, "c.png")
            };

            ConstantFramePlan plan = ConstantFramePlan.Build(sources, 24000, 48000, new FrameRate(12));

            Assert.That(plan.SourceIndices, Is.EqualTo(new[] { 0, 0, 1, 1, 2, 2 }));
            Assert.That(plan.DuplicateCount, Is.EqualTo(3));
        }

        [Test]
        public void AudioAlignmentPadsBeginningAndEndAgainstDspClock()
        {
            AudioAlignment alignment = AudioAlignment.Calculate(10.0, 12.0, 10.01, 95000, 48000);

            Assert.That(alignment.ExpectedFrames, Is.EqualTo(96000));
            Assert.That(alignment.LeadingSilenceFrames, Is.EqualTo(480));
            Assert.That(alignment.TrailingSilenceFrames, Is.EqualTo(520));
        }

        [Test]
        public void SilentSceneStillGetsOneVideoFrameAndFullAudioDuration()
        {
            FrameRate rate = new FrameRate(24);
            Assert.That(rate.FrameCountForSamples(0, 48000), Is.EqualTo(1));

            AudioAlignment alignment = AudioAlignment.Calculate(3.0, 5.0, 5.0, 0, 48000);
            Assert.That(alignment.ExpectedFrames, Is.EqualTo(96000));
            Assert.That(alignment.TrailingSilenceFrames, Is.EqualTo(0));
            Assert.That(alignment.LeadingSilenceFrames, Is.EqualTo(96000));
        }
    }
}
