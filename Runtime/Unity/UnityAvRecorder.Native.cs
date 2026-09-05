using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace GameFramework.MediaCapture.Unity
{
    public sealed partial class UnityAvRecorder
    {
        private NativeD3D11Capture m_NativeCapture;
        private string m_NativeFailure;
        private int m_NativePeakQueue;
        private long m_NativeGpuBytes;

        public string BackendName => m_NativeCapture != null ? NativeD3D11Capture.BACKEND_NAME : backend?.Name;
        public long CapturedFrameCount => m_NativeCapture != null ? m_NativeCapture.Status.Captured : capturedFrames.Count;
        public long EncodedFrameCount => m_NativeCapture != null ? m_NativeCapture.Status.Encoded : 0;
        public long DroppedFrameCount => droppedVideoFrames + (m_NativeCapture != null ? m_NativeCapture.Status.Dropped : 0);
        public double ElapsedSeconds => sampleRate > 0 && State == RecordingState.Recording ? (double)GetCurrentSample() / sampleRate : 0;

        private IEnumerator CaptureNativeFrames()
        {
            var wait = new WaitForEndOfFrame();
            while (State == RecordingState.Recording)
            {
                yield return wait;
                try
                {
                    m_NativeCapture.Poll();
                    if (m_NativeCapture.Status.State >= 3)
                        throw new IOException(m_NativeCapture.Error);
                    long sample = GetCurrentSample();
                    long ordinal = options.FrameRate.FrameAtOrBeforeSample(sample, sampleRate);
                    if (ordinal <= lastCaptureOrdinal) continue;
                    if (lastCaptureOrdinal >= 0 && ordinal > lastCaptureOrdinal + 1)
                    {
                        int missed = checked((int)(ordinal - lastCaptureOrdinal - 1));
                        missedRenderCadenceFrames += missed;
                        droppedVideoFrames += missed;
                    }
                    lastCaptureOrdinal = ordinal;
                    var timer = Stopwatch.StartNew();
                    m_NativeCapture.Capture(sample);
                    RecordCaptureMainThreadTime(timer.Elapsed.TotalMilliseconds);
                    m_NativePeakQueue = Math.Max(m_NativePeakQueue, m_NativeCapture.Status.Queued);
                    m_NativeGpuBytes = m_NativeCapture.TextureBytes;
                }
                catch (Exception exception)
                {
                    m_NativeFailure = exception.Message;
                    Debug.LogException(exception);
                    frameCoroutine = null;
                    _ = StopRecordingAsync();
                    yield break;
                }
            }
        }

        private async Task<RecordingResult> FinishNativeAsync(CancellationToken cancellationToken)
        {
            long audioFrames = 0;
            try
            {
                long stopTimestamp = processAudioBackend.GetClockTimestamp();
                RecordingAudioCaptureStop audio = await processAudioBackend.StopAudioCaptureAsync(
                    stopTimestamp, options.EncoderTimeoutSeconds, cancellationToken);
                audioFrames = audio.AudioFrames;
                droppedAudioFrames = audio.DroppedAudioFrames;
                m_ProcessAudioStatistics = audio;
                if (!string.IsNullOrEmpty(m_NativeFailure)) throw new IOException(m_NativeFailure);
                cancellationToken.ThrowIfCancellationRequested();
                State = RecordingState.Encoding;
                m_NativeCapture.Stop(audioFrames, audioPath, partialOutputPath);
                var deadline = Stopwatch.StartNew();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    m_NativeCapture.Poll();
                    int state = m_NativeCapture.Status.State;
                    if (state == 2) break;
                    if (state >= 3) throw new IOException(m_NativeCapture.Error);
                    if (deadline.Elapsed.TotalSeconds > options.EncoderTimeoutSeconds)
                        throw new TimeoutException("Native recording did not finish within the configured timeout.");
                    await Task.Delay(10, cancellationToken);
                }
                if (!File.Exists(partialOutputPath) || new FileInfo(partialOutputPath).Length == 0)
                    throw new IOException("Native recording produced no output MP4.");
                if (File.Exists(options.OutputPath))
                {
                    if (!options.OverwriteExisting) throw new IOException("Output already exists: " + options.OutputPath);
                    File.Replace(partialOutputPath, options.OutputPath, null);
                }
                else File.Move(partialOutputPath, options.OutputPath);
                State = RecordingState.Completed;
                WriteNativeManifest("completed", string.Empty, audioFrames);
                return BuildNativeResult(true, string.Empty, audioFrames);
            }
            catch (OperationCanceledException exception)
            {
                State = RecordingState.Aborted;
                WriteNativeManifest("aborted", exception.Message, audioFrames);
                return BuildNativeResult(false, exception.Message, audioFrames);
            }
            catch (Exception exception)
            {
                State = RecordingState.Faulted;
                WriteNativeManifest("faulted", exception.ToString(), audioFrames);
                return BuildNativeResult(false, exception.Message, audioFrames);
            }
            finally
            {
                processAudioBackend?.AbortAudioCapture();
                m_NativeCapture.Retire();
                if (State == RecordingState.Completed && !options.KeepIntermediateFiles)
                {
                    // Keep diagnostics; only remove files created by this recording session.
                    foreach (string name in new[] { "video.mp4", "audio.wav", "WindowsMediaCaptureHelper.exe",
                        "windows-audio-ready.tsv", "windows-audio-stop.txt" })
                    {
                        try { File.Delete(Path.Combine(sessionDirectory, name)); }
                        catch (IOException exception) { Debug.LogWarning(exception.Message); }
                        catch (UnauthorizedAccessException exception) { Debug.LogWarning(exception.Message); }
                    }
                }
            }
        }

        private RecordingResult BuildNativeResult(bool success, string message, long audioFrames)
        {
            NativeD3D11Capture.CaptureStatus status = m_NativeCapture.Status;
            return Result = new RecordingResult(success, options.OutputPath, sessionDirectory, BackendName, message,
                audioFrames, sampleRate, (int)Math.Min(int.MaxValue, status.Captured), status.Encoded,
                (int)Math.Min(int.MaxValue, status.Duplicated), (int)Math.Min(int.MaxValue, DroppedFrameCount),
                droppedAudioFrames, new RecordingPerformanceStats(false, m_NativePeakQueue,
                    captureMainThreadSamples > 0 ? captureMainThreadMilliseconds / captureMainThreadSamples : 0,
                    maxCaptureMainThreadMilliseconds, 0, 0, 0, 0, 0, options.MainThreadStageBudgetMilliseconds,
                    mainThreadBudgetExceededEvents, 0, m_NativeGpuBytes, missedRenderCadenceFrames,
                    (int)Math.Min(int.MaxValue, status.Dropped)),
                m_ProcessAudioStatistics.DiscontinuityEvents, m_ProcessAudioStatistics.TimestampErrorPackets,
                m_ProcessAudioStatistics.InsertedSilenceFrames);
        }

        [Serializable]
        private sealed class NativeManifest
        {
            public string status;
            public string message;
            public string backend;
            public string outputPath;
            public string graphicsDevice;
            public int width;
            public int height;
            public int sampleRate;
            public long audioFrames;
            public long capturedFrames;
            public long outputFrames;
            public long duplicateFrames;
            public long droppedVideoFrames;
            public long droppedAudioFrames;
            public long audioDiscontinuityEvents;
            public long audioTimestampErrorPackets;
            public long audioInsertedSilenceFrames;
            public long gpuTextureBytes;
            public long cpuPixelReadbackBytes;
            public double averageCaptureMainThreadMilliseconds;
            public double maxCaptureMainThreadMilliseconds;
        }

        private void WriteNativeManifest(string status, string message, long audioFrames)
        {
            if (string.IsNullOrEmpty(sessionDirectory)) return;
            try
            {
                var manifest = new NativeManifest
                {
                    status = status, message = message, backend = BackendName, outputPath = options.OutputPath,
                    graphicsDevice = SystemInfo.graphicsDeviceName,
                    width = m_NativeCapture.Width,
                    height = m_NativeCapture.Height,
                    sampleRate = sampleRate, audioFrames = audioFrames,
                    capturedFrames = CapturedFrameCount, outputFrames = EncodedFrameCount,
                    duplicateFrames = m_NativeCapture.Status.Duplicated, droppedVideoFrames = DroppedFrameCount,
                    droppedAudioFrames = droppedAudioFrames,
                    audioDiscontinuityEvents = m_ProcessAudioStatistics.DiscontinuityEvents,
                    audioTimestampErrorPackets = m_ProcessAudioStatistics.TimestampErrorPackets,
                    audioInsertedSilenceFrames = m_ProcessAudioStatistics.InsertedSilenceFrames,
                    gpuTextureBytes = m_NativeGpuBytes, cpuPixelReadbackBytes = 0,
                    averageCaptureMainThreadMilliseconds = captureMainThreadSamples > 0 ?
                        captureMainThreadMilliseconds / captureMainThreadSamples : 0,
                    maxCaptureMainThreadMilliseconds = maxCaptureMainThreadMilliseconds
                };
                File.WriteAllText(Path.Combine(sessionDirectory, "manifest.json"), JsonUtility.ToJson(manifest, true));
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
