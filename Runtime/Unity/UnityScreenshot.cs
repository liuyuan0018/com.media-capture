using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace GameFramework.MediaCapture.Unity
{
    /// <summary>One-shot PNG capture of the final Game View, including overlay UI.</summary>
    public sealed class UnityScreenshot : MonoBehaviour
    {
        TaskCompletionSource<string> completion;

        /// <summary>Call on the Unity main thread in Play mode. Completes after the PNG is written.</summary>
        public static Task<string> CaptureAsync(string outputPath)
        {
            if (!Application.isPlaying) throw new InvalidOperationException("Screenshot capture requires Play mode.");
            if (string.IsNullOrWhiteSpace(outputPath) || !Path.IsPathRooted(outputPath) ||
                !string.Equals(Path.GetExtension(outputPath), ".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("An absolute PNG output path is required.", nameof(outputPath));
            if (File.Exists(outputPath)) throw new IOException("Screenshot output already exists: " + outputPath);
            var host = new GameObject("MediaCapture Screenshot");
            DontDestroyOnLoad(host);
            var capture = host.AddComponent<UnityScreenshot>();
            capture.completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.StartCoroutine(capture.Capture(outputPath));
            return capture.completion.Task;
        }

        IEnumerator Capture(string path)
        {
            yield return new WaitForEndOfFrame();
            Texture2D texture = null;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                if (!texture || texture.width <= 0 || texture.height <= 0)
                    throw new InvalidOperationException("Unity returned an empty framebuffer.");
                byte[] png = ImageConversion.EncodeToPNG(texture);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(temporary, png);
                File.Move(temporary, path);
                completion.TrySetResult(path);
            }
            catch (Exception error) { completion.TrySetException(error); }
            finally
            {
                if (texture) Destroy(texture);
                if (File.Exists(temporary)) File.Delete(temporary);
                Destroy(gameObject);
            }
        }

        void OnDestroy()
        {
            completion?.TrySetCanceled();
        }
    }
}
