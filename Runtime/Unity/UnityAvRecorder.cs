using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace GameFramework.MediaCapture.Unity
{
    [DisallowMultipleComponent]
    public sealed partial class UnityAvRecorder : MonoBehaviour
    {
        private sealed class PendingFrame
        {
            internal long Sample;
            internal string RelativePath;
            internal string AbsolutePath;
            internal Task WriteTask;
            internal CaptureSlot Slot;
        }

        private sealed class CaptureSlot
        {
            internal RenderTexture Texture;
            internal byte[] RawBuffer;
            internal volatile bool Busy;
        }

        private readonly List<CapturedFrameTime> capturedFrames = new List<CapturedFrameTime>();
        private readonly List<PendingFrame> pendingFrames = new List<PendingFrame>();
        private readonly List<CaptureSlot> captureSlots = new List<CaptureSlot>();
        private readonly object performanceLock = new object();
        private RecordingOptions options;
        private IRecordingEncoderBackend backend;
        private string sessionDirectory;
        private string framesDirectory;
        private string audioPath;
        private string framesTsvPath;
        private string partialOutputPath;
        private PcmWaveWriter audioWriter;
        private UnityAudioCaptureTap audioTap;
        private IRecordingProcessAudioBackend processAudioBackend;
        private Coroutine frameCoroutine;
        private double sessionStartDsp;
        private long sessionStartTimestamp;
        private long timestampFrequency;
        private int sampleRate;
        private long lastCaptureOrdinal = -1;
        private int captureSequence;
        private int droppedVideoFrames;
        private int missedRenderCadenceFrames;
        private int backpressureDroppedFrames;
        private Task<RecordingResult> finishingTask;
        private Texture2D screenshotTexture;
        private Exception frameWriteFailure;
        private readonly CancellationTokenSource lifetimeCancellation = new CancellationTokenSource();
        private bool usedAsyncGpuReadback;
        private int maxPipelineDepth;
        private int syncFallbackFrames;
        private int captureMainThreadSamples;
        private double captureMainThreadMilliseconds;
        private double maxCaptureMainThreadMilliseconds;
        private int readbackCopySamples;
        private double readbackCopyMilliseconds;
        private double maxReadbackCopyMilliseconds;
        private int frameEncodeSamples;
        private double frameEncodeMilliseconds;
        private double maxFrameEncodeMilliseconds;
        private int mainThreadBudgetExceededEvents;
        private long droppedAudioFrames;
        private RecordingAudioCaptureStop m_ProcessAudioStatistics;
        private CancellationTokenRegistration m_StopCancellationRegistration;

        public static UnityAvRecorder Active { get; private set; }
        public RecordingState State { get; private set; } = RecordingState.Idle;
        public string SessionDirectory => sessionDirectory;
        public RecordingResult Result { get; private set; }

        public static UnityAvRecorder StartRecording(
            RecordingOptions recordingOptions,
            IRecordingEncoderBackend encoderBackend = null)
        {
            if (recordingOptions == null) throw new ArgumentNullException(nameof(recordingOptions));
            if (!Application.isPlaying)
                throw new InvalidOperationException("Recording requires Play mode or a running Player.");
            if (Active != null && Active.State != RecordingState.Completed &&
                Active.State != RecordingState.Faulted && Active.State != RecordingState.Aborted)
            {
                throw new InvalidOperationException("A media capture session is already active.");
            }

            recordingOptions.Validate();
            var host = new GameObject("GameFramework Media Capture");
            DontDestroyOnLoad(host);
            UnityAvRecorder recorder = host.AddComponent<UnityAvRecorder>();
            try
            {
                recorder.Begin(recordingOptions, encoderBackend ?? CreateDefaultBackend(),
                    encoderBackend == null && recordingOptions.VideoBackend == RecordingVideoBackend.NativeD3D11);
                Active = recorder;
                return recorder;
            }
            catch
            {
                recorder.processAudioBackend?.AbortAudioCapture();
                recorder.m_NativeCapture?.Retire();
                recorder.ReleaseCaptureResources();
                Destroy(host);
                throw;
            }
        }

        public Task<RecordingResult> StopRecordingAsync(CancellationToken cancellationToken = default)
        {
            if (finishingTask != null)
            {
                return finishingTask;
            }

            if (State != RecordingState.Recording)
            {
                throw new InvalidOperationException("Recorder is not recording.");
            }

            State = RecordingState.Stopping;
            if (frameCoroutine != null)
            {
                StopCoroutine(frameCoroutine);
                frameCoroutine = null;
            }

            if (cancellationToken.CanBeCanceled)
            {
                m_StopCancellationRegistration = cancellationToken.Register(lifetimeCancellation.Cancel);
            }

            if (m_NativeCapture != null)
            {
                finishingTask = FinishNativeAsync(lifetimeCancellation.Token);
                return finishingTask;
            }

            if (processAudioBackend != null)
            {
                long stopTimestamp = processAudioBackend.GetClockTimestamp();
                finishingTask = StopProcessAudioAndFinishAsync(
                    stopTimestamp,
                    lifetimeCancellation.Token);
                return finishingTask;
            }

            double stopDsp = AudioSettings.dspTime;
            audioTap.End();
            long expectedAudioFrames = Math.Max(
                1,
                (long)Math.Round((stopDsp - sessionStartDsp) * sampleRate));
            finishingTask = FinishAsync(expectedAudioFrames, lifetimeCancellation.Token);
            return finishingTask;
        }

        public void Abort()
        {
            if (State != RecordingState.Recording && State != RecordingState.Stopping && State != RecordingState.Encoding)
            {
                return;
            }

            State = RecordingState.Aborted;
            lifetimeCancellation.Cancel();
            if (frameCoroutine != null)
            {
                StopCoroutine(frameCoroutine);
            }

            audioTap?.End();
            audioWriter?.Dispose();
            processAudioBackend?.AbortAudioCapture();
            if (m_NativeCapture != null)
            {
                WriteNativeManifest("aborted", "Recording was aborted before finalization.", 0);
                m_NativeCapture.Retire();
                return;
            }
            WriteDiagnosticManifest("aborted", "Recording was aborted before finalization.", 0, 0);
        }

        private void Begin(RecordingOptions recordingOptions, IRecordingEncoderBackend encoderBackend, bool useNative)
        {
            options = recordingOptions;
            backend = encoderBackend;
            string outputDirectory = Path.GetDirectoryName(options.OutputPath);
            Directory.CreateDirectory(outputDirectory);
            sessionDirectory = Path.Combine(
                outputDirectory,
                ".media-capture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            framesDirectory = Path.Combine(sessionDirectory, "frames");
            audioPath = Path.Combine(sessionDirectory, "audio.wav");
            framesTsvPath = Path.Combine(sessionDirectory, "frames.tsv");
            partialOutputPath = Path.Combine(sessionDirectory, "output.partial.mp4");
            Directory.CreateDirectory(sessionDirectory);
            if (!useNative) Directory.CreateDirectory(framesDirectory);

            processAudioBackend = backend as IRecordingProcessAudioBackend;
            if (useNative)
            {
                if (!(backend is WindowsMediaFoundationBackend))
                    throw new PlatformNotSupportedException("Native recording requires Windows D3D11 and WASAPI process audio.");
                m_NativeCapture = new NativeD3D11Capture(options, Path.Combine(sessionDirectory, "video.mp4"), 48000);
            }
            if (processAudioBackend != null)
            {
                droppedAudioFrames = -1;
                RecordingAudioCaptureStart audioCapture =
                    processAudioBackend.StartAudioCapture(sessionDirectory, audioPath);
                sampleRate = audioCapture.SampleRate;
                sessionStartTimestamp = audioCapture.Timestamp;
                timestampFrequency = audioCapture.TimestampFrequency;
                if (useNative && sampleRate != 48000)
                    throw new InvalidOperationException("Native recording expects 48000 Hz process audio.");
            }
            else
            {
                sampleRate = AudioSettings.outputSampleRate;
                if (sampleRate <= 0)
                {
                    sampleRate = 48000;
                }

                audioWriter = new PcmWaveWriter(audioPath, sampleRate, options.MaxQueuedAudioBlocks);
                AudioListener listener = FindObjectsByType<AudioListener>(FindObjectsSortMode.None)
                    .FirstOrDefault(candidate => candidate.enabled && candidate.gameObject.activeInHierarchy);
                if (listener == null)
                {
                    var listenerObject = new GameObject("GameFramework Silent Audio Listener");
                    listenerObject.transform.SetParent(transform, false);
                    listener = listenerObject.AddComponent<AudioListener>();
                }

                audioTap = listener.gameObject.AddComponent<UnityAudioCaptureTap>();
                sessionStartDsp = AudioSettings.dspTime;
                audioTap.Begin(audioWriter, sessionStartDsp, sampleRate);
            }
            State = RecordingState.Recording;
            frameCoroutine = StartCoroutine(useNative ? CaptureNativeFrames() : CaptureFrames());
        }

        private IEnumerator CaptureFrames()
        {
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                PrepareCaptureSlots(Screen.width, Screen.height);
            }

            while (State == RecordingState.Recording)
            {
                yield return new WaitForEndOfFrame();
                RetireCompletedFrames();
                long sample = GetCurrentSample();
                long ordinal = options.FrameRate.FrameAtOrBeforeSample(sample, sampleRate);
                if (ordinal <= lastCaptureOrdinal)
                {
                    continue;
                }

                if (lastCaptureOrdinal >= 0 && ordinal > lastCaptureOrdinal + 1)
                {
                    int missed = checked((int)(ordinal - lastCaptureOrdinal - 1));
                    missedRenderCadenceFrames += missed;
                    droppedVideoFrames += missed;
                }

                lastCaptureOrdinal = ordinal;
                if (pendingFrames.Count >= options.MaxPendingFrameWrites)
                {
                    backpressureDroppedFrames++;
                    droppedVideoFrames++;
                    continue;
                }

                captureSequence++;
                string extension = options.FrameImageFormat == FrameImageFormat.Png ? ".png" : ".jpg";
                string relativePath = Path.Combine("frames", captureSequence.ToString("000000") + extension);
                string absolutePath = Path.Combine(sessionDirectory, relativePath);
                pendingFrames.Add(new PendingFrame
                {
                    Sample = sample,
                    RelativePath = relativePath,
                    AbsolutePath = absolutePath
                });
                PendingFrame pending = pendingFrames[pendingFrames.Count - 1];
                maxPipelineDepth = Math.Max(maxPipelineDepth, pendingFrames.Count);
                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    CaptureAsync(pending, Screen.width, Screen.height);
                }
                else
                {
                    CaptureSynchronously(pending, Screen.width, Screen.height);
                }
            }
        }

        private void CaptureAsync(PendingFrame pending, int width, int height)
        {
            CaptureSlot slot = AcquireCaptureSlot(width, height);
            if (slot == null)
            {
                pendingFrames.Remove(pending);
                backpressureDroppedFrames++;
                droppedVideoFrames++;
                return;
            }

            usedAsyncGpuReadback = true;
            slot.Busy = true;
            pending.Slot = slot;
            Stopwatch stopwatch = Stopwatch.StartNew();
            ScreenCapture.CaptureScreenshotIntoRenderTexture(slot.Texture);
            AsyncGPUReadback.Request(
                slot.Texture,
                0,
                TextureFormat.RGBA32,
                request => CompleteReadback(pending, request, width, height));
            stopwatch.Stop();
            RecordCaptureMainThreadTime(stopwatch.Elapsed.TotalMilliseconds);
        }

        private CaptureSlot AcquireCaptureSlot(int width, int height)
        {
            for (int i = 0; i < captureSlots.Count; i++)
            {
                CaptureSlot existing = captureSlots[i];
                if (!existing.Busy && existing.Texture.width == width && existing.Texture.height == height)
                {
                    return existing;
                }
            }

            int busySlots = captureSlots.Count(slot => slot.Busy);
            if (busySlots >= options.MaxPendingGpuReadbacks)
            {
                return null;
            }

            for (int i = captureSlots.Count - 1; i >= 0; i--)
            {
                if (!captureSlots[i].Busy)
                {
                    Destroy(captureSlots[i].Texture);
                    captureSlots.RemoveAt(i);
                }
            }

            var texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = "GameFramework Media Capture Readback",
                useMipMap = false,
                autoGenerateMips = false
            };
            texture.Create();
            var created = new CaptureSlot
            {
                Texture = texture,
                RawBuffer = new byte[checked(width * height * 4)]
            };
            captureSlots.Add(created);
            return created;
        }

        private void PrepareCaptureSlots(int width, int height)
        {
            for (int i = 0; i < options.MaxPendingGpuReadbacks; i++)
            {
                CaptureSlot slot = AcquireCaptureSlot(width, height);
                if (slot == null)
                {
                    break;
                }
                slot.Busy = true;
            }

            for (int i = 0; i < captureSlots.Count; i++)
            {
                captureSlots[i].Busy = false;
            }
        }

        private unsafe void CompleteReadback(
            PendingFrame pending,
            AsyncGPUReadbackRequest request,
            int width,
            int height)
        {
            if (request.hasError)
            {
                if (pending.Slot != null)
                {
                    pending.Slot.Busy = false;
                }
                pending.WriteTask = Task.FromException(
                    new IOException("Unity AsyncGPUReadback failed for " + pending.RelativePath + "."));
                return;
            }

            Stopwatch copyStopwatch = Stopwatch.StartNew();
            var source = request.GetData<byte>();
            int byteCount = source.Length;
            byte[] raw = pending.Slot.RawBuffer;
            if (raw == null || raw.Length < byteCount)
            {
                pending.Slot.Busy = false;
                pending.WriteTask = Task.FromException(
                    new IOException("GPU readback exceeded the preallocated capture buffer."));
                return;
            }
            void* sourcePointer = NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(source);
            Marshal.Copy((IntPtr)sourcePointer, raw, 0, byteCount);
            copyStopwatch.Stop();
            RecordReadbackCopyTime(copyStopwatch.Elapsed.TotalMilliseconds);

            pending.WriteTask = Task.Run(() =>
            {
                Stopwatch encodeStopwatch = Stopwatch.StartNew();
                try
                {
                    byte[] image = EncodeFrame(
                        raw,
                        GraphicsFormat.R8G8B8A8_UNorm,
                        width,
                        height,
                        width * 4);
                    File.WriteAllBytes(pending.AbsolutePath, image);
                }
                finally
                {
                    encodeStopwatch.Stop();
                    RecordFrameEncodeTime(encodeStopwatch.Elapsed.TotalMilliseconds);
                    pending.Slot.Busy = false;
                }
            });
        }

        private void CaptureSynchronously(PendingFrame pending, int width, int height)
        {
            syncFallbackFrames++;
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (screenshotTexture == null ||
                screenshotTexture.width != width || screenshotTexture.height != height)
            {
                if (screenshotTexture != null)
                {
                    Destroy(screenshotTexture);
                }
                screenshotTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
            }

            screenshotTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            screenshotTexture.Apply(false, false);
            byte[] raw = screenshotTexture.GetRawTextureData();
            stopwatch.Stop();
            RecordCaptureMainThreadTime(stopwatch.Elapsed.TotalMilliseconds);
            pending.WriteTask = Task.Run(() =>
            {
                Stopwatch encodeStopwatch = Stopwatch.StartNew();
                try
                {
                    byte[] image = EncodeFrame(
                        raw,
                        GraphicsFormat.R8G8B8_UNorm,
                        width,
                        height,
                        width * 3);
                    File.WriteAllBytes(pending.AbsolutePath, image);
                }
                finally
                {
                    encodeStopwatch.Stop();
                    RecordFrameEncodeTime(encodeStopwatch.Elapsed.TotalMilliseconds);
                }
            });
        }

        private void RecordCaptureMainThreadTime(double milliseconds)
        {
            captureMainThreadSamples++;
            captureMainThreadMilliseconds += milliseconds;
            maxCaptureMainThreadMilliseconds = Math.Max(maxCaptureMainThreadMilliseconds, milliseconds);
            if (milliseconds > options.MainThreadStageBudgetMilliseconds)
            {
                mainThreadBudgetExceededEvents++;
            }
        }

        private void RecordReadbackCopyTime(double milliseconds)
        {
            readbackCopySamples++;
            readbackCopyMilliseconds += milliseconds;
            maxReadbackCopyMilliseconds = Math.Max(maxReadbackCopyMilliseconds, milliseconds);
            if (milliseconds > options.MainThreadStageBudgetMilliseconds)
            {
                mainThreadBudgetExceededEvents++;
            }
        }

        private byte[] EncodeFrame(
            byte[] raw,
            GraphicsFormat format,
            int width,
            int height,
            int rowBytes)
        {
            return options.FrameImageFormat == FrameImageFormat.Png
                ? ImageConversion.EncodeArrayToPNG(raw, format, (uint)width, (uint)height, (uint)rowBytes)
                : ImageConversion.EncodeArrayToJPG(
                    raw,
                    format,
                    (uint)width,
                    (uint)height,
                    (uint)rowBytes,
                    options.JpegQuality);
        }

        private void RecordFrameEncodeTime(double milliseconds)
        {
            lock (performanceLock)
            {
                frameEncodeSamples++;
                frameEncodeMilliseconds += milliseconds;
                maxFrameEncodeMilliseconds = Math.Max(maxFrameEncodeMilliseconds, milliseconds);
            }
        }

        private void RetireCompletedFrames()
        {
            for (int i = pendingFrames.Count - 1; i >= 0; i--)
            {
                PendingFrame pending = pendingFrames[i];
                if (pending.WriteTask == null || !pending.WriteTask.IsCompleted)
                {
                    continue;
                }

                if (pending.WriteTask.IsFaulted)
                {
                    frameWriteFailure = pending.WriteTask.Exception;
                    pendingFrames.RemoveAt(i);
                    continue;
                }

                capturedFrames.Add(new CapturedFrameTime(pending.Sample, pending.RelativePath));
                pendingFrames.RemoveAt(i);
            }

            capturedFrames.Sort((left, right) => left.Sample.CompareTo(right.Sample));
        }

        private async Task<RecordingResult> FinishAsync(long expectedAudioFrames, CancellationToken cancellationToken)
        {
            try
            {
                audioWriter?.Finish(expectedAudioFrames);
                await DrainPendingFramesAsync(cancellationToken);
                if (frameWriteFailure != null)
                {
                    throw new IOException("A captured frame failed to write.", frameWriteFailure);
                }
                if (pendingFrames.Count > 0)
                {
                    throw new IOException(pendingFrames.Count + " captured frame files did not finish writing.");
                }

                if (capturedFrames.Count == 0)
                {
                    throw new IOException("No video frames were captured.");
                }

                ConstantFramePlan plan = ConstantFramePlan.Build(
                    capturedFrames,
                    expectedAudioFrames,
                    sampleRate,
                    options.FrameRate);
                WriteFramePlan(plan);
                WriteDiagnosticManifest("captured", string.Empty, expectedAudioFrames, plan.DuplicateCount);

                if (!backend.IsAvailable(out string unavailableReason))
                {
                    throw new PlatformNotSupportedException(unavailableReason);
                }

                State = RecordingState.Encoding;
                if (File.Exists(partialOutputPath))
                {
                    File.Delete(partialOutputPath);
                }

                RecordingEncodeResult encodeResult = await backend.EncodeAsync(
                    new RecordingEncodeRequest
                    {
                        SessionDirectory = sessionDirectory,
                        FramesTsvPath = framesTsvPath,
                        AudioWavPath = audioPath,
                        PartialOutputPath = partialOutputPath,
                        FrameRate = options.FrameRate,
                        AudioFrames = expectedAudioFrames,
                        AudioSampleRate = sampleRate,
                        TimeoutSeconds = options.EncoderTimeoutSeconds
                    },
                    cancellationToken);
                if (!encodeResult.Success)
                {
                    throw new IOException(encodeResult.Message);
                }

                if (File.Exists(options.OutputPath))
                {
                    File.Delete(options.OutputPath);
                }
                File.Move(partialOutputPath, options.OutputPath);
                State = RecordingState.Completed;
                WriteDiagnosticManifest("completed", encodeResult.Message, expectedAudioFrames, plan.DuplicateCount);
                RecordingResult result = BuildResult(true, encodeResult.Message, expectedAudioFrames, plan);
                CleanupAfterCompletion();
                return result;
            }
            catch (OperationCanceledException exception)
            {
                State = RecordingState.Aborted;
                WriteDiagnosticManifest("aborted", exception.ToString(), expectedAudioFrames, 0);
                return BuildResult(false, exception.Message, expectedAudioFrames, null);
            }
            catch (Exception exception)
            {
                State = RecordingState.Faulted;
                WriteDiagnosticManifest("faulted", exception.ToString(), expectedAudioFrames, 0);
                return BuildResult(false, exception.Message, expectedAudioFrames, null);
            }
            finally
            {
                ReleaseCaptureResources();
            }
        }

        private void WriteFramePlan(ConstantFramePlan plan)
        {
            using (var writer = new StreamWriter(framesTsvPath, false, new UTF8Encoding(false)))
            {
                for (int ordinal = 0; ordinal < plan.OutputFrameCount; ordinal++)
                {
                    CapturedFrameTime source = capturedFrames[plan.SourceIndices[ordinal]];
                    writer.Write(ordinal.ToString(CultureInfo.InvariantCulture));
                    writer.Write('\t');
                    writer.Write(Path.Combine(sessionDirectory, source.RelativePath));
                    writer.WriteLine();
                }
            }
        }

        private void WriteDiagnosticManifest(string status, string message, long audioFrames, int duplicates)
        {
            if (string.IsNullOrEmpty(sessionDirectory) || !Directory.Exists(sessionDirectory))
            {
                return;
            }

            string escaped = (message ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
            RecordingPerformanceStats performance = CreatePerformanceStats();
            string json = "{\n" +
                "  \"status\": \"" + status + "\",\n" +
                "  \"outputPath\": \"" + options.OutputPath.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\n" +
                "  \"backend\": \"" + backend.Name.Replace("\"", "\\\"") + "\",\n" +
                "  \"frameRate\": \"" + options.FrameRate + "\",\n" +
                "  \"sampleRate\": " + sampleRate + ",\n" +
                "  \"audioFrames\": " + audioFrames + ",\n" +
                "  \"capturedFrames\": " + capturedFrames.Count + ",\n" +
                "  \"pendingFrames\": " + pendingFrames.Count + ",\n" +
                "  \"duplicateFrames\": " + duplicates + ",\n" +
                "  \"droppedVideoFrames\": " + droppedVideoFrames + ",\n" +
                "  \"missedRenderCadenceFrames\": " + performance.MissedRenderCadenceFrames + ",\n" +
                "  \"backpressureDroppedFrames\": " + performance.BackpressureDroppedFrames + ",\n" +
                "  \"droppedAudioFrames\": " + GetDroppedAudioFrames() + ",\n" +
                "  \"performance\": {\n" +
                "    \"usedAsyncGpuReadback\": " + (performance.UsedAsyncGpuReadback ? "true" : "false") + ",\n" +
                "    \"maxPipelineDepth\": " + performance.MaxPipelineDepth + ",\n" +
                "    \"averageCaptureMainThreadMilliseconds\": " + Format(performance.AverageCaptureMainThreadMilliseconds) + ",\n" +
                "    \"maxCaptureMainThreadMilliseconds\": " + Format(performance.MaxCaptureMainThreadMilliseconds) + ",\n" +
                "    \"averageReadbackCopyMilliseconds\": " + Format(performance.AverageReadbackCopyMilliseconds) + ",\n" +
                "    \"maxReadbackCopyMilliseconds\": " + Format(performance.MaxReadbackCopyMilliseconds) + ",\n" +
                "    \"averageFrameEncodeMilliseconds\": " + Format(performance.AverageFrameEncodeMilliseconds) + ",\n" +
                "    \"maxFrameEncodeMilliseconds\": " + Format(performance.MaxFrameEncodeMilliseconds) + ",\n" +
                "    \"syncFallbackFrames\": " + performance.SyncFallbackFrames + ",\n" +
                "    \"mainThreadStageBudgetMilliseconds\": " + Format(performance.MainThreadStageBudgetMilliseconds) + ",\n" +
                "    \"mainThreadBudgetExceededEvents\": " + performance.MainThreadBudgetExceededEvents + ",\n" +
                "    \"preallocatedCpuReadbackBytes\": " + performance.PreallocatedCpuReadbackBytes + ",\n" +
                "    \"estimatedGpuCaptureBytes\": " + performance.EstimatedGpuCaptureBytes + "\n" +
                "  },\n" +
                "  \"message\": \"" + escaped + "\"\n" +
                "}\n";
            File.WriteAllText(Path.Combine(sessionDirectory, "manifest.json"), json, new UTF8Encoding(false));
        }

        private RecordingResult BuildResult(
            bool success,
            string message,
            long audioFrames,
            ConstantFramePlan plan)
        {
            return Result = new RecordingResult(
                success,
                options.OutputPath,
                sessionDirectory,
                backend.Name,
                message,
                audioFrames,
                sampleRate,
                capturedFrames.Count,
                plan?.OutputFrameCount ?? 0,
                plan?.DuplicateCount ?? 0,
                droppedVideoFrames,
                GetDroppedAudioFrames(),
                CreatePerformanceStats(), m_ProcessAudioStatistics.DiscontinuityEvents,
                m_ProcessAudioStatistics.TimestampErrorPackets, m_ProcessAudioStatistics.InsertedSilenceFrames);
        }

        private static IRecordingEncoderBackend CreateDefaultBackend()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            return new WindowsMediaFoundationBackend();
#else
            return new MacOsAvFoundationBackend();
#endif
        }

        private long GetCurrentSample()
        {
            if (processAudioBackend == null)
            {
                return Math.Max(
                    0,
                    (long)Math.Round((AudioSettings.dspTime - sessionStartDsp) * sampleRate));
            }

            long elapsed = Math.Max(0, processAudioBackend.GetClockTimestamp() - sessionStartTimestamp);
            return checked((elapsed * sampleRate + timestampFrequency / 2) / timestampFrequency);
        }

        private async Task<RecordingResult> StopProcessAudioAndFinishAsync(
            long stopTimestamp,
            CancellationToken cancellationToken)
        {
            try
            {
                RecordingAudioCaptureStop audioCapture =
                    await processAudioBackend.StopAudioCaptureAsync(
                        stopTimestamp,
                        options.FrameWriteTimeoutSeconds,
                        cancellationToken);
                droppedAudioFrames = audioCapture.DroppedAudioFrames;
                m_ProcessAudioStatistics = audioCapture;
                return await FinishAsync(audioCapture.AudioFrames, cancellationToken);
            }
            catch (OperationCanceledException exception)
            {
                processAudioBackend.AbortAudioCapture();
                State = RecordingState.Aborted;
                await DrainPendingFramesAfterFailureAsync();
                WriteDiagnosticManifest("aborted", exception.ToString(), 0, 0);
                RecordingResult result = BuildResult(false, exception.Message, 0, null);
                ReleaseCaptureResources();
                return result;
            }
            catch (Exception exception)
            {
                processAudioBackend.AbortAudioCapture();
                State = RecordingState.Faulted;
                await DrainPendingFramesAfterFailureAsync();
                WriteDiagnosticManifest("faulted", exception.ToString(), 0, 0);
                RecordingResult result = BuildResult(false, exception.Message, 0, null);
                ReleaseCaptureResources();
                return result;
            }
        }

        private long GetDroppedAudioFrames()
        {
            return processAudioBackend != null ? droppedAudioFrames : audioWriter?.DroppedFrames ?? 0;
        }

        private async Task DrainPendingFramesAsync(CancellationToken cancellationToken)
        {
            long deadlineTicks = DateTime.UtcNow.AddSeconds(options.FrameWriteTimeoutSeconds).Ticks;
            while (pendingFrames.Count > 0 && DateTime.UtcNow.Ticks < deadlineTicks)
            {
                RetireCompletedFrames();
                if (pendingFrames.Count > 0)
                {
                    await Task.Delay(50, cancellationToken);
                }
            }
            RetireCompletedFrames();
        }

        private async Task DrainPendingFramesAfterFailureAsync()
        {
            try
            {
                await DrainPendingFramesAsync(CancellationToken.None);
            }
            catch
            {
            }
        }

        private void ReleaseCaptureResources()
        {
            audioWriter?.Dispose();
            audioWriter = null;
            if (audioTap != null)
            {
                Destroy(audioTap);
                audioTap = null;
            }
            if (screenshotTexture != null)
            {
                Destroy(screenshotTexture);
                screenshotTexture = null;
            }
            for (int i = 0; i < captureSlots.Count; i++)
            {
                if (captureSlots[i].Texture != null)
                {
                    Destroy(captureSlots[i].Texture);
                }
            }
            captureSlots.Clear();
        }

        private RecordingPerformanceStats CreatePerformanceStats()
        {
            lock (performanceLock)
            {
                long readbackBytes = 0;
                for (int i = 0; i < captureSlots.Count; i++)
                {
                    readbackBytes += captureSlots[i].RawBuffer?.LongLength ?? 0;
                }
                return new RecordingPerformanceStats(
                    usedAsyncGpuReadback,
                    maxPipelineDepth,
                    captureMainThreadSamples > 0
                        ? captureMainThreadMilliseconds / captureMainThreadSamples
                        : 0,
                    maxCaptureMainThreadMilliseconds,
                    readbackCopySamples > 0 ? readbackCopyMilliseconds / readbackCopySamples : 0,
                    maxReadbackCopyMilliseconds,
                    frameEncodeSamples > 0 ? frameEncodeMilliseconds / frameEncodeSamples : 0,
                    maxFrameEncodeMilliseconds,
                    syncFallbackFrames,
                    options.MainThreadStageBudgetMilliseconds,
                    mainThreadBudgetExceededEvents,
                    readbackBytes,
                    readbackBytes,
                    missedRenderCadenceFrames,
                    backpressureDroppedFrames);
            }
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private void CleanupAfterCompletion()
        {
            if (!options.KeepIntermediateFiles && Directory.Exists(sessionDirectory))
            {
                Directory.Delete(sessionDirectory, true);
            }
        }

        private void OnApplicationQuit()
        {
            if (State == RecordingState.Recording)
            {
                Abort();
            }
        }

        private void OnDestroy()
        {
            if (State == RecordingState.Recording || State == RecordingState.Stopping || State == RecordingState.Encoding)
            {
                Abort();
            }
            m_NativeCapture?.Retire();
            m_StopCancellationRegistration.Dispose();
            lifetimeCancellation.Dispose();
            if (Active == this)
            {
                Active = null;
            }
        }
    }
}
