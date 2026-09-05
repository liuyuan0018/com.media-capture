using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace GameFramework.MediaCapture.Unity
{
    public sealed class WindowsMediaFoundationBackend : IRecordingEncoderBackend, IRecordingProcessAudioBackend
    {
        private const string HelperResourcePath =
            "GameFrameworkMediaCapture/WindowsMediaCaptureHelper";
        private const string HelperFileName = "WindowsMediaCaptureHelper.exe";
        private const int HelperStartupTimeoutMilliseconds = 10000;

        private Process captureProcess;
        private string helperPath;
        private string readyPath;
        private string stopPath;
        private string m_AudioStatisticsPath;
        private readonly object m_ProcessLock = new object();
        private Process m_AudioWaitProcess;
        private readonly StringBuilder captureOutput = new StringBuilder();
        private readonly StringBuilder captureError = new StringBuilder();
        private RecordingAudioCaptureStart audioStart;

        public string Name => "Windows WASAPI Process Loopback + Media Foundation (H.264/AAC)";

        public bool IsAvailable(out string reason)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (Environment.OSVersion.Version.Build < 20348)
            {
                reason = "WASAPI process loopback requires Windows build 20348 or newer.";
                return false;
            }

            if (Resources.Load<TextAsset>(HelperResourcePath) == null)
            {
                reason = "The packaged Windows media capture helper is missing. Build Native~/WindowsMediaCaptureHelper.vcxproj.";
                return false;
            }

            reason = string.Empty;
            return true;
#else
            reason = "The Windows Media Foundation backend is only available in the Windows Editor and Windows Player.";
            return false;
#endif
        }

        public RecordingAudioCaptureStart StartAudioCapture(string sessionDirectory, string audioWavPath)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (captureProcess != null)
            {
                throw new InvalidOperationException("Windows process audio capture is already active.");
            }

            if (!IsAvailable(out string unavailableReason))
            {
                throw new PlatformNotSupportedException(unavailableReason);
            }

            helperPath = ExtractHelper(sessionDirectory);
            m_AudioStatisticsPath = audioWavPath + ".stats.json";
            readyPath = Path.Combine(sessionDirectory, "windows-audio-ready.tsv");
            stopPath = Path.Combine(sessionDirectory, "windows-audio-stop.txt");
            File.Delete(readyPath);
            File.Delete(stopPath);

            var startInfo = CreateStartInfo(
                helperPath,
                "capture --pid " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                " --audio " + Quote(audioWavPath) +
                " --ready " + Quote(readyPath) +
                " --stop " + Quote(stopPath));
            try
            {
                captureProcess = StartProcess(startInfo, captureOutput, captureError);

                Stopwatch timeout = Stopwatch.StartNew();
                while (!File.Exists(readyPath))
                {
                    if (captureProcess.HasExited)
                    {
                        throw new IOException(
                            "Windows process audio capture exited during startup: " + ReadProcessMessage());
                    }

                    if (timeout.ElapsedMilliseconds >= HelperStartupTimeoutMilliseconds)
                    {
                        throw new TimeoutException("Timed out while starting Windows process audio capture.");
                    }

                    Thread.Sleep(20);
                }

                string[] values = File.ReadAllText(readyPath, Encoding.UTF8).Trim().Split('\t');
                if (values.Length != 3 ||
                    !long.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long timestamp) ||
                    !long.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long frequency) ||
                    !int.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sampleRate) ||
                    timestamp <= 0 || frequency <= 0 || sampleRate <= 0)
                {
                    throw new IOException("Windows process audio capture returned invalid clock metadata.");
                }

                audioStart = new RecordingAudioCaptureStart(timestamp, frequency, sampleRate);
                return audioStart;
            }
            catch
            {
                AbortAudioCapture();
                throw;
            }
#else
            throw new PlatformNotSupportedException(
                "Windows process audio capture is unavailable on this platform.");
#endif
        }

        public long GetClockTimestamp()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (!QueryPerformanceCounter(out long timestamp))
            {
                throw new InvalidOperationException(
                    "QueryPerformanceCounter failed with Win32 error " + Marshal.GetLastWin32Error() + ".");
            }

            return timestamp;
#else
            throw new PlatformNotSupportedException(
                "The Windows performance counter is unavailable on this platform.");
#endif
        }

        public Task<RecordingAudioCaptureStop> StopAudioCaptureAsync(
            long stopTimestamp,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (captureProcess == null)
            {
                throw new InvalidOperationException("Windows process audio capture is not active.");
            }

            File.WriteAllText(
                stopPath,
                stopTimestamp.ToString(CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
            Process process = captureProcess;
            lock (m_ProcessLock) m_AudioWaitProcess = process;
            return Task.Run(() => WaitForAudioCapture(process, stopTimestamp, timeoutSeconds, cancellationToken));
#else
            throw new PlatformNotSupportedException(
                "Windows process audio capture is unavailable on this platform.");
#endif
        }

        public void AbortAudioCapture()
        {
            lock (m_ProcessLock)
            {
                if (captureProcess == null)
                {
                    return;
                }

                try
                {
                    if (!captureProcess.HasExited)
                    {
                        captureProcess.Kill();
                    }
                }
                catch
                {
                }
                finally
                {
                    if (captureProcess != m_AudioWaitProcess)
                    {
                        captureProcess.Dispose();
                        captureProcess = null;
                    }
                }
            }
        }

        public Task<RecordingEncodeResult> EncodeAsync(
            RecordingEncodeRequest request,
            CancellationToken cancellationToken)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            return Task.Run(
                () => Encode(request, cancellationToken),
                cancellationToken);
#else
            return Task.FromResult(Failure("Windows Media Foundation is unsupported on this platform."));
#endif
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private RecordingAudioCaptureStop WaitForAudioCapture(
            Process process,
            long stopTimestamp,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            try
            {
                Stopwatch timeout = Stopwatch.StartNew();
                while (!process.WaitForExit(50))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (timeout.Elapsed > TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)))
                    {
                        throw new TimeoutException("Windows process audio capture did not stop in time.");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                int exitCode = process.ExitCode;
                process.WaitForExit();
                string message = ReadProcessMessage();
                if (exitCode != 0)
                {
                    throw new IOException("Windows process audio capture failed: " + message);
                }

                long elapsed = Math.Max(1, stopTimestamp - audioStart.Timestamp);
                long frames = Math.Max(
                    1,
                    checked((elapsed * audioStart.SampleRate + audioStart.TimestampFrequency / 2) /
                        audioStart.TimestampFrequency));
                var statistics = JsonUtility.FromJson<ProcessAudioStatistics>(File.ReadAllText(m_AudioStatisticsPath));
                if (statistics == null || statistics.version != 1 || statistics.audioFrames != frames ||
                    statistics.discontinuityEvents < 0 || statistics.timestampErrorPackets < 0 || statistics.insertedSilenceFrames < 0)
                    throw new IOException("Windows process audio capture returned invalid statistics.");
                // WASAPI reports discontinuities, not an exact count of lost samples. Silence can also mean an idle source.
                return new RecordingAudioCaptureStop(frames, -1, statistics.discontinuityEvents,
                    statistics.timestampErrorPackets, statistics.insertedSilenceFrames);
            }
            catch
            {
                AbortAudioCapture();
                throw;
            }
            finally
            {
                lock (m_ProcessLock)
                {
                    if (captureProcess == process) captureProcess = null;
                    if (m_AudioWaitProcess == process) m_AudioWaitProcess = null;
                    process.Dispose();
                }
            }
        }

        [Serializable]
        private sealed class ProcessAudioStatistics
        {
            public int version;
            public long audioFrames;
            public long discontinuityEvents;
            public long timestampErrorPackets;
            public long insertedSilenceFrames;
        }

        private RecordingEncodeResult Encode(
            RecordingEncodeRequest request,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(helperPath) || !File.Exists(helperPath))
            {
                helperPath = ExtractHelper(request.SessionDirectory);
            }

            var output = new StringBuilder();
            var error = new StringBuilder();
            var startInfo = CreateStartInfo(
                helperPath,
                "encode --frames " + Quote(request.FramesTsvPath) +
                " --audio " + Quote(request.AudioWavPath) +
                " --output " + Quote(request.PartialOutputPath) +
                " --fps-num " + request.FrameRate.Numerator.ToString(CultureInfo.InvariantCulture) +
                " --fps-den " + request.FrameRate.Denominator.ToString(CultureInfo.InvariantCulture));
            using (Process process = StartProcess(startInfo, output, error))
            {
                Stopwatch timeout = Stopwatch.StartNew();
                while (!process.WaitForExit(50))
                {
                    if (cancellationToken.IsCancellationRequested ||
                        timeout.Elapsed > TimeSpan.FromSeconds(Math.Max(1, request.TimeoutSeconds)))
                    {
                        try { process.Kill(); } catch { }
                        return Failure(cancellationToken.IsCancellationRequested
                            ? "Windows Media Foundation encoding was cancelled."
                            : "Windows Media Foundation encoding timed out.");
                    }
                }

                process.WaitForExit();
                string message = JoinOutput(output, error);
                if (process.ExitCode != 0 || !File.Exists(request.PartialOutputPath))
                {
                    return Failure("Windows media helper failed with exit code " + process.ExitCode + ". " + message);
                }

                return new RecordingEncodeResult { Success = true, Backend = Name, Message = message };
            }
        }

        private static ProcessStartInfo CreateStartInfo(string fileName, string arguments)
        {
            return new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
        }

        private static Process StartProcess(
            ProcessStartInfo startInfo,
            StringBuilder output,
            StringBuilder error)
        {
            var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data != null) lock (output) output.AppendLine(args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data != null) lock (error) error.AppendLine(args.Data);
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        private static string ExtractHelper(string sessionDirectory)
        {
            TextAsset helper = Resources.Load<TextAsset>(HelperResourcePath);
            if (helper == null)
            {
                throw new FileNotFoundException(
                    "The packaged Windows media capture helper is missing. Build Native~/WindowsMediaCaptureHelper.vcxproj.");
            }

            string path = Path.Combine(sessionDirectory, HelperFileName);
            File.WriteAllBytes(path, helper.bytes);
            return path;
        }

        private string ReadProcessMessage()
        {
            return JoinOutput(captureOutput, captureError);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryPerformanceCounter(out long performanceCount);

        private static string JoinOutput(StringBuilder output, StringBuilder error)
        {
            lock (output)
            lock (error)
            {
                return (output.ToString() + Environment.NewLine + error).Trim();
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
#endif

        private RecordingEncodeResult Failure(string message)
        {
            return new RecordingEncodeResult { Success = false, Backend = Name, Message = message };
        }
    }
}
