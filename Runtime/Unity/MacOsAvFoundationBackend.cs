using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace GameFramework.MediaCapture.Unity
{
    public sealed class MacOsAvFoundationBackend : IRecordingEncoderBackend
    {
        public string Name => "macOS AVFoundation (H.264/AAC)";

        public bool IsAvailable(out string reason)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            if (!File.Exists("/usr/bin/xcrun"))
            {
                reason = "xcrun is unavailable; install Apple Command Line Tools.";
                return false;
            }

            if (Resources.Load<TextAsset>("GameFrameworkMediaCapture/MacOsAvFoundationEncoder") == null)
            {
                reason = "The packaged AVFoundation helper resource is missing.";
                return false;
            }

            reason = string.Empty;
            return true;
#else
            reason = "AVFoundation backend is only available in the macOS Editor and macOS Player.";
            return false;
#endif
        }

        public Task<RecordingEncodeResult> EncodeAsync(
            RecordingEncodeRequest request,
            CancellationToken cancellationToken)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            TextAsset helper = Resources.Load<TextAsset>("GameFrameworkMediaCapture/MacOsAvFoundationEncoder");
            if (helper == null)
            {
                return Task.FromResult(Failure("The packaged AVFoundation helper resource is missing."));
            }

            string helperPath = Path.Combine(request.SessionDirectory, "macos-avfoundation-encoder.swift");
            File.WriteAllText(helperPath, helper.text, new UTF8Encoding(false));
            return Task.Run(
                () => RunEncoder(helperPath, request, cancellationToken),
                cancellationToken);
#else
            return Task.FromResult(Failure("AVFoundation backend is unsupported on this platform."));
#endif
        }

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        private RecordingEncodeResult RunEncoder(
            string helperPath,
            RecordingEncodeRequest request,
            CancellationToken cancellationToken)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/xcrun",
                Arguments = "swift " + Quote(helperPath) + " " +
                    Quote(request.FramesTsvPath) + " " +
                    Quote(request.AudioWavPath) + " " +
                    Quote(request.PartialOutputPath) + " " +
                    request.FrameRate.Numerator + " " +
                    request.FrameRate.Denominator + " " +
                    request.AudioFrames + " " +
                    request.AudioSampleRate,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (_, args) =>
                {
                    if (args.Data != null) stdout.AppendLine(args.Data);
                };
                process.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data != null) stderr.AppendLine(args.Data);
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                Stopwatch stopwatch = Stopwatch.StartNew();
                while (!process.WaitForExit(100))
                {
                    if (cancellationToken.IsCancellationRequested ||
                        stopwatch.Elapsed > TimeSpan.FromSeconds(Math.Max(1, request.TimeoutSeconds)))
                    {
                        try { process.Kill(); } catch { }
                        return Failure(cancellationToken.IsCancellationRequested
                            ? "AVFoundation encoding was cancelled."
                            : "AVFoundation encoding timed out.");
                    }
                }

                process.WaitForExit();
                if (process.ExitCode != 0 || !File.Exists(request.PartialOutputPath))
                {
                    return Failure(
                        "AVFoundation helper failed with exit code " + process.ExitCode + ". " +
                        stderr.ToString().Trim());
                }

                return new RecordingEncodeResult
                {
                    Success = true,
                    Backend = Name,
                    Message = stdout.ToString().Trim()
                };
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
#endif

        private RecordingEncodeResult Failure(string message)
        {
            return new RecordingEncodeResult { Success = false, Backend = Name, Message = message };
        }
    }
}
