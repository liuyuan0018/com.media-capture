using System;
using System.Collections;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

namespace GameFramework.MediaCapture.Unity
{
    internal static class RecordingCommandLineBootstrap
    {
        private const string OutputArgument = "-gameFrameworkRecord";
        private const string DurationArgument = "-gameFrameworkRecordSeconds";
        private const string FrameRateArgument = "-gameFrameworkRecordFps";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartFromCommandLine()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            string outputPath = ReadValue(arguments, OutputArgument);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return;
            }

            float duration = ReadFloat(arguments, DurationArgument, 30f);
            int frameRate = Mathf.Max(1, Mathf.RoundToInt(ReadFloat(arguments, FrameRateArgument, 24f)));
            try
            {
                UnityAvRecorder recorder = UnityAvRecorder.StartRecording(new RecordingOptions
                {
                    OutputPath = outputPath,
                    FrameRateNumerator = frameRate,
                    KeepIntermediateFiles = true
                });
                recorder.StartCoroutine(StopAfter(recorder, duration));
            }
            catch (Exception exception)
            {
                Debug.LogError("GameFramework media capture failed to start: " + exception);
            }
        }

        private static IEnumerator StopAfter(UnityAvRecorder recorder, float duration)
        {
            yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, duration));
            Task<RecordingResult> task = recorder.StopRecordingAsync();
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsFaulted)
            {
                Debug.LogError("GameFramework media capture finalization failed: " + task.Exception);
                yield break;
            }

            RecordingResult result = task.Result;
            if (result.Success)
            {
                Debug.Log("GameFramework media capture complete: " + result.OutputPath);
            }
            else
            {
                Debug.LogError(
                    "GameFramework media capture failed; source bundle retained at " +
                    result.SessionDirectory + ": " + result.Message);
            }
        }

        private static string ReadValue(string[] arguments, string name)
        {
            for (int i = 0; i + 1 < arguments.Length; i++)
            {
                if (string.Equals(arguments[i], name, StringComparison.Ordinal))
                {
                    return arguments[i + 1];
                }
            }

            return null;
        }

        private static float ReadFloat(string[] arguments, string name, float fallback)
        {
            string value = ReadValue(arguments, name);
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : fallback;
        }
    }
}
