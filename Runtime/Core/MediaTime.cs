using System;
using System.Collections.Generic;

namespace GameFramework.MediaCapture
{
    public readonly struct FrameRate : IEquatable<FrameRate>
    {
        public FrameRate(int numerator, int denominator = 1)
        {
            if (numerator <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(numerator));
            }

            if (denominator <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(denominator));
            }

            Numerator = numerator;
            Denominator = denominator;
        }

        public int Numerator { get; }
        public int Denominator { get; }
        public double FramesPerSecond => (double)Numerator / Denominator;

        public long SampleAtFrame(long frameOrdinal, int sampleRate)
        {
            if (frameOrdinal < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameOrdinal));
            }

            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            long scaled = checked(frameOrdinal * sampleRate * (long)Denominator);
            return checked((scaled + Numerator - 1) / Numerator);
        }

        public long FrameAtOrBeforeSample(long sample, int sampleRate)
        {
            if (sample < 0)
            {
                return 0;
            }

            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            return checked(sample * Numerator / (sampleRate * (long)Denominator));
        }

        public long FrameCountForSamples(long sampleCount, int sampleRate)
        {
            if (sampleCount <= 0)
            {
                return 1;
            }

            long divisor = checked(sampleRate * (long)Denominator);
            long scaled = checked(sampleCount * Numerator);
            return Math.Max(1, checked((scaled + divisor - 1) / divisor));
        }

        public bool Equals(FrameRate other) =>
            Numerator == other.Numerator && Denominator == other.Denominator;

        public override bool Equals(object obj) => obj is FrameRate other && Equals(other);
        public override int GetHashCode() => (Numerator * 397) ^ Denominator;
        public override string ToString() => Denominator == 1
            ? Numerator.ToString()
            : Numerator + "/" + Denominator;
    }

    public readonly struct CapturedFrameTime
    {
        public CapturedFrameTime(long sample, string relativePath)
        {
            Sample = sample < 0 ? 0 : sample;
            RelativePath = relativePath ?? throw new ArgumentNullException(nameof(relativePath));
        }

        public long Sample { get; }
        public string RelativePath { get; }
    }

    public sealed class ConstantFramePlan
    {
        private ConstantFramePlan(int[] sourceIndices, int duplicateCount)
        {
            SourceIndices = sourceIndices;
            DuplicateCount = duplicateCount;
        }

        public IReadOnlyList<int> SourceIndices { get; }
        public int OutputFrameCount => SourceIndices.Count;
        public int DuplicateCount { get; }

        public static ConstantFramePlan Build(
            IReadOnlyList<CapturedFrameTime> sources,
            long durationSamples,
            int sampleRate,
            FrameRate frameRate)
        {
            if (sources == null || sources.Count == 0)
            {
                throw new ArgumentException("At least one captured frame is required.", nameof(sources));
            }

            for (int i = 1; i < sources.Count; i++)
            {
                if (sources[i].Sample < sources[i - 1].Sample)
                {
                    throw new ArgumentException("Captured frames must be ordered by sample timestamp.", nameof(sources));
                }
            }

            int outputCount = checked((int)frameRate.FrameCountForSamples(durationSamples, sampleRate));
            int[] sourceIndices = new int[outputCount];
            int sourceIndex = 0;
            int duplicates = 0;
            for (int outputIndex = 0; outputIndex < outputCount; outputIndex++)
            {
                long targetSample = frameRate.SampleAtFrame(outputIndex, sampleRate);
                while (sourceIndex + 1 < sources.Count && sources[sourceIndex + 1].Sample <= targetSample)
                {
                    sourceIndex++;
                }

                sourceIndices[outputIndex] = sourceIndex;
                if (outputIndex > 0 && sourceIndices[outputIndex - 1] == sourceIndex)
                {
                    duplicates++;
                }
            }

            return new ConstantFramePlan(sourceIndices, duplicates);
        }
    }

    public readonly struct AudioAlignment
    {
        public AudioAlignment(long expectedFrames, long leadingSilenceFrames, long trailingSilenceFrames)
        {
            ExpectedFrames = expectedFrames;
            LeadingSilenceFrames = leadingSilenceFrames;
            TrailingSilenceFrames = trailingSilenceFrames;
        }

        public long ExpectedFrames { get; }
        public long LeadingSilenceFrames { get; }
        public long TrailingSilenceFrames { get; }

        public static AudioAlignment Calculate(
            double sessionStartDsp,
            double sessionStopDsp,
            double firstAudioDsp,
            long capturedFrames,
            int sampleRate)
        {
            if (sessionStopDsp < sessionStartDsp)
            {
                throw new ArgumentOutOfRangeException(nameof(sessionStopDsp));
            }

            long expected = Math.Max(0, (long)Math.Round((sessionStopDsp - sessionStartDsp) * sampleRate));
            long leading = Math.Max(0, (long)Math.Round((firstAudioDsp - sessionStartDsp) * sampleRate));
            long trailing = Math.Max(0, expected - leading - Math.Max(0, capturedFrames));
            return new AudioAlignment(expected, leading, trailing);
        }
    }
}
