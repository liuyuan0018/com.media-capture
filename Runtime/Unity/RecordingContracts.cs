using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace GameFramework.MediaCapture.Unity
{
    public enum RecordingState
    {
        Idle,
        Recording,
        Stopping,
        Encoding,
        Completed,
        Faulted,
        Aborted
    }

    public enum FrameImageFormat
    {
        Jpeg,
        Png
    }

    public enum RecordingVideoBackend
    {
        NativeD3D11,
        ImageSequence
    }

    [Serializable]
    public sealed class RecordingOptions
    {
        public string OutputPath;
        public RecordingVideoBackend VideoBackend = RecordingVideoBackend.NativeD3D11;
        public int OutputWidth;
        public int OutputHeight;
        public int HardwareQuality = 20;
        public int GpuTexturePoolSize = 8;
        public int MaxEncodingLagMilliseconds = 2000;
        public int FrameRateNumerator = 24;
        public int FrameRateDenominator = 1;
        public int MaxPendingFrameWrites = 4;
        public int MaxPendingGpuReadbacks = 3;
        public FrameImageFormat FrameImageFormat = FrameImageFormat.Jpeg;
        public int JpegQuality = 90;
        public int MaxQueuedAudioBlocks = 256;
        public float MainThreadStageBudgetMilliseconds = 2f;
        public int FrameWriteTimeoutSeconds = 20;
        public int EncoderTimeoutSeconds = 300;
        public bool KeepIntermediateFiles;
        public bool OverwriteExisting;

        public FrameRate FrameRate => new FrameRate(FrameRateNumerator, FrameRateDenominator);

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(OutputPath) || !Path.IsPathRooted(OutputPath))
            {
                throw new ArgumentException("OutputPath must be an absolute path.", nameof(OutputPath));
            }

            if (!string.Equals(Path.GetExtension(OutputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The default backend requires an .mp4 output path.", nameof(OutputPath));
            }

            _ = FrameRate;
            if (OutputWidth < 0 || OutputHeight < 0 || (OutputWidth == 0) != (OutputHeight == 0) ||
                OutputWidth % 2 != 0 || OutputHeight % 2 != 0 ||
                HardwareQuality < 0 || HardwareQuality > 51 || GpuTexturePoolSize < 4 || GpuTexturePoolSize > 32 ||
                MaxEncodingLagMilliseconds < 100 || EncoderTimeoutSeconds <= 0 || FrameWriteTimeoutSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException("Invalid recording dimensions, hardware encoding options or timeout.");
            }
            if (MaxPendingFrameWrites <= 0 || MaxPendingGpuReadbacks <= 0 || MaxQueuedAudioBlocks <= 0 ||
                MainThreadStageBudgetMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException("Queue budgets must be positive.");
            }

            if (JpegQuality < 1 || JpegQuality > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(JpegQuality));
            }

            if (File.Exists(OutputPath) && !OverwriteExisting)
            {
                throw new IOException("Output already exists: " + OutputPath);
            }
        }
    }

    public sealed class RecordingResult
    {
        internal RecordingResult(
            bool success,
            string outputPath,
            string sessionDirectory,
            string backend,
            string message,
            long audioFrames,
            int sampleRate,
            int capturedFrames,
            long outputFrames,
            int duplicateFrames,
            int droppedVideoFrames,
            long droppedAudioFrames,
            RecordingPerformanceStats performance,
            long audioDiscontinuityEvents = 0,
            long audioTimestampErrorPackets = 0,
            long audioInsertedSilenceFrames = 0)
        {
            Success = success;
            OutputPath = outputPath;
            SessionDirectory = sessionDirectory;
            Backend = backend;
            Message = message;
            AudioFrames = audioFrames;
            SampleRate = sampleRate;
            CapturedFrames = capturedFrames;
            OutputFrames = outputFrames;
            DuplicateFrames = duplicateFrames;
            DroppedVideoFrames = droppedVideoFrames;
            DroppedAudioFrames = droppedAudioFrames;
            Performance = performance;
            AudioDiscontinuityEvents = audioDiscontinuityEvents;
            AudioTimestampErrorPackets = audioTimestampErrorPackets;
            AudioInsertedSilenceFrames = audioInsertedSilenceFrames;
        }

        public bool Success { get; }
        public string OutputPath { get; }
        public string SessionDirectory { get; }
        public string Backend { get; }
        public string Message { get; }
        public long AudioFrames { get; }
        public int SampleRate { get; }
        public int CapturedFrames { get; }
        public long OutputFrames { get; }
        public int DuplicateFrames { get; }
        public int DroppedVideoFrames { get; }
        public long DroppedAudioFrames { get; }
        public bool DroppedAudioFramesKnown => DroppedAudioFrames >= 0;
        public long AudioDiscontinuityEvents { get; }
        public long AudioTimestampErrorPackets { get; }
        public long AudioInsertedSilenceFrames { get; }
        public RecordingPerformanceStats Performance { get; }
        public double DurationSeconds => SampleRate > 0 ? (double)AudioFrames / SampleRate : 0;
    }

    public sealed class RecordingPerformanceStats
    {
        internal RecordingPerformanceStats(
            bool usedAsyncGpuReadback,
            int maxPipelineDepth,
            double averageCaptureMainThreadMilliseconds,
            double maxCaptureMainThreadMilliseconds,
            double averageReadbackCopyMilliseconds,
            double maxReadbackCopyMilliseconds,
            double averageFrameEncodeMilliseconds,
            double maxFrameEncodeMilliseconds,
            int syncFallbackFrames,
            double mainThreadStageBudgetMilliseconds,
            int mainThreadBudgetExceededEvents,
            long preallocatedCpuReadbackBytes,
            long estimatedGpuCaptureBytes,
            int missedRenderCadenceFrames,
            int backpressureDroppedFrames)
        {
            UsedAsyncGpuReadback = usedAsyncGpuReadback;
            MaxPipelineDepth = maxPipelineDepth;
            AverageCaptureMainThreadMilliseconds = averageCaptureMainThreadMilliseconds;
            MaxCaptureMainThreadMilliseconds = maxCaptureMainThreadMilliseconds;
            AverageReadbackCopyMilliseconds = averageReadbackCopyMilliseconds;
            MaxReadbackCopyMilliseconds = maxReadbackCopyMilliseconds;
            AverageFrameEncodeMilliseconds = averageFrameEncodeMilliseconds;
            MaxFrameEncodeMilliseconds = maxFrameEncodeMilliseconds;
            SyncFallbackFrames = syncFallbackFrames;
            MainThreadStageBudgetMilliseconds = mainThreadStageBudgetMilliseconds;
            MainThreadBudgetExceededEvents = mainThreadBudgetExceededEvents;
            PreallocatedCpuReadbackBytes = preallocatedCpuReadbackBytes;
            EstimatedGpuCaptureBytes = estimatedGpuCaptureBytes;
            MissedRenderCadenceFrames = missedRenderCadenceFrames;
            BackpressureDroppedFrames = backpressureDroppedFrames;
        }

        public bool UsedAsyncGpuReadback { get; }
        public int MaxPipelineDepth { get; }
        public double AverageCaptureMainThreadMilliseconds { get; }
        public double MaxCaptureMainThreadMilliseconds { get; }
        public double AverageReadbackCopyMilliseconds { get; }
        public double MaxReadbackCopyMilliseconds { get; }
        public double AverageFrameEncodeMilliseconds { get; }
        public double MaxFrameEncodeMilliseconds { get; }
        public int SyncFallbackFrames { get; }
        public double MainThreadStageBudgetMilliseconds { get; }
        public int MainThreadBudgetExceededEvents { get; }
        public long PreallocatedCpuReadbackBytes { get; }
        public long EstimatedGpuCaptureBytes { get; }
        public int MissedRenderCadenceFrames { get; }
        public int BackpressureDroppedFrames { get; }
    }

    public sealed class RecordingEncodeRequest
    {
        public string SessionDirectory;
        public string FramesTsvPath;
        public string AudioWavPath;
        public string PartialOutputPath;
        public FrameRate FrameRate;
        public long AudioFrames;
        public int AudioSampleRate;
        public int TimeoutSeconds;
    }

    public sealed class RecordingEncodeResult
    {
        public bool Success;
        public string Backend;
        public string Message;
    }

    public readonly struct RecordingAudioCaptureStart
    {
        public RecordingAudioCaptureStart(long timestamp, long timestampFrequency, int sampleRate)
        {
            Timestamp = timestamp;
            TimestampFrequency = timestampFrequency;
            SampleRate = sampleRate;
        }

        public long Timestamp { get; }
        public long TimestampFrequency { get; }
        public int SampleRate { get; }
    }

    public readonly struct RecordingAudioCaptureStop
    {
        public RecordingAudioCaptureStop(long audioFrames, long droppedAudioFrames,
            long discontinuityEvents = 0, long timestampErrorPackets = 0, long insertedSilenceFrames = 0)
        {
            AudioFrames = audioFrames;
            DroppedAudioFrames = droppedAudioFrames;
            DiscontinuityEvents = discontinuityEvents;
            TimestampErrorPackets = timestampErrorPackets;
            InsertedSilenceFrames = insertedSilenceFrames;
        }

        public long AudioFrames { get; }
        public long DroppedAudioFrames { get; }
        public long DiscontinuityEvents { get; }
        public long TimestampErrorPackets { get; }
        public long InsertedSilenceFrames { get; }
    }

    public interface IRecordingEncoderBackend
    {
        string Name { get; }
        bool IsAvailable(out string reason);
        Task<RecordingEncodeResult> EncodeAsync(RecordingEncodeRequest request, CancellationToken cancellationToken);
    }

    public interface IRecordingProcessAudioBackend
    {
        RecordingAudioCaptureStart StartAudioCapture(string sessionDirectory, string audioWavPath);
        long GetClockTimestamp();
        Task<RecordingAudioCaptureStop> StopAudioCaptureAsync(
            long stopTimestamp,
            int timeoutSeconds,
            CancellationToken cancellationToken);
        void AbortAudioCapture();
    }
}
