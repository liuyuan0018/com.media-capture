using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GameFramework.MediaCapture.Unity
{
    internal sealed class NativeMetalCapture : INativeVideoCapture
    {
        private const string LIBRARY = "MediaCaptureMetal";
        private const int ERROR_CAPACITY = 2048;
        internal const string BACKEND_NAME = "Metal / VideoToolbox H.264 + Unity Audio / AAC";

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeOptions
        {
            internal int AbiVersion;
            internal int Width;
            internal int Height;
            internal int FpsNumerator;
            internal int FpsDenominator;
            internal int SampleRate;
            internal int PoolSize;
            internal int Quality;
            internal int MaxLagMilliseconds;
            internal IntPtr VideoPath;
        }

        private static readonly List<NativeMetalCapture> s_Retiring = new List<NativeMetalCapture>();
        private static NativeCaptureCleanup s_Cleanup;
        private readonly byte[] m_Error = new byte[ERROR_CAPACITY];
        private ulong m_Handle;
        private IntPtr m_Callback;
        private IntPtr m_NativeTexture;
        private CommandBuffer m_RenderCommands;
        private RenderTexture m_GameViewTexture;
        private RenderTexture m_OutputTexture;
        private Material m_ColorMaterial;
        private int m_SourceWidth;
        private int m_SourceHeight;
        private bool m_Retired;

        public string BackendName => BACKEND_NAME;
        public bool HardwareEncodingConfirmed { get; private set; }
        public NativeCaptureStatus Status { get; private set; }
        public string Error { get; private set; } = string.Empty;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public long TextureBytes => Status.GpuBytes +
            (m_GameViewTexture != null ? (long)m_GameViewTexture.width * m_GameViewTexture.height * 4 : 0) +
            (m_OutputTexture != null ? (long)m_OutputTexture.width * m_OutputTexture.height * 4 : 0);

        internal NativeMetalCapture(RecordingOptions options, string videoPath, int sampleRate)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
                throw new PlatformNotSupportedException("Native recording requires macOS Metal.");
            try
            {
                Shader colorShader = Resources.Load<Shader>("GameFrameworkMediaCapture/MetalCaptureColor");
                if (colorShader == null) throw new InvalidOperationException("Metal capture color shader is missing.");
                m_ColorMaterial = new Material(colorShader) { hideFlags = HideFlags.HideAndDontSave };
                m_ColorMaterial.SetFloat("_EncodeSRGB", QualitySettings.activeColorSpace == ColorSpace.Linear ? 1f : 0f);
                m_Callback = mcmetal_get_render_callback();
                m_RenderCommands = new CommandBuffer { name = "Media Capture Metal" };
#if UNITY_EDITOR
                // 编辑器按钮回调中的 Screen 可能返回工具窗口的尺寸，直接读取 Game View 渲染尺寸。
                Vector2 gameViewSize = UnityEditor.Handles.GetMainGameViewSize();
                m_SourceWidth = Mathf.RoundToInt(gameViewSize.x);
                m_SourceHeight = Mathf.RoundToInt(gameViewSize.y);
#else
                m_SourceWidth = Screen.width;
                m_SourceHeight = Screen.height;
#endif
                int width = options.OutputWidth > 0 ? options.OutputWidth : m_SourceWidth & ~1;
                int height = options.OutputHeight > 0 ? options.OutputHeight : m_SourceHeight & ~1;
                Width = width;
                Height = height;
                if (m_SourceWidth <= 0 || m_SourceHeight <= 0 || width < 2 || height < 2)
                    throw new InvalidOperationException("Game View has no valid rendering size.");
                m_OutputTexture = CreateTexture(width, height,
                    GraphicsFormat.B8G8R8A8_UNorm, "Media Capture Encoder BGRA");
                m_NativeTexture = m_OutputTexture.GetNativeTexturePtr();
                IntPtr path = AllocateUtf8(videoPath);
                try
                {
                    var nativeOptions = new NativeOptions
                    {
                        AbiVersion = 1, Width = width, Height = height,
                        FpsNumerator = options.FrameRateNumerator, FpsDenominator = options.FrameRateDenominator,
                        SampleRate = sampleRate, PoolSize = options.GpuTexturePoolSize,
                        Quality = options.HardwareQuality, MaxLagMilliseconds = options.MaxEncodingLagMilliseconds,
                        VideoPath = path
                    };
                    m_Handle = mcmetal_create(m_NativeTexture, ref nativeOptions, m_Error, m_Error.Length);
                    if (m_Handle == 0) throw new InvalidOperationException(DecodeError());
                }
                finally { Marshal.FreeHGlobal(path); }
                HardwareEncodingConfirmed = mcmetal_hardware(m_Handle) != 0;
                RefreshStatus();
                EnsureCleanup();
            }
            catch
            {
                Retire();
                throw;
            }
#else
            throw new PlatformNotSupportedException("Native recording requires macOS Metal.");
#endif
        }

        public void Capture(long audioSample)
        {
            if (m_Retired) return;
            // 此方法在帧渲染结束后执行，此时 Screen 返回待采集画面的尺寸。
            int sourceWidth = Screen.width;
            int sourceHeight = Screen.height;
            if (sourceWidth <= 0 || sourceHeight <= 0) return;
            if (m_GameViewTexture == null || sourceWidth != m_SourceWidth || sourceHeight != m_SourceHeight)
            {
                RenderTexture replacement = CreateTexture(sourceWidth, sourceHeight,
                    GraphicsFormat.R8G8B8A8_UNorm, "Media Capture Linear Game View");
                if (m_GameViewTexture != null) UnityEngine.Object.Destroy(m_GameViewTexture);
                m_GameViewTexture = replacement;
            }
            ScreenCapture.CaptureScreenshotIntoRenderTexture(m_GameViewTexture);
            m_SourceWidth = sourceWidth;
            m_SourceHeight = sourceHeight;
            bool wasSrgbWrite = GL.sRGBWrite;
            RenderTexture previousTarget = RenderTexture.active;
            try
            {
                GL.sRGBWrite = false;
                // The screenshot in an UNorm target contains linear light in a Linear
                // project. Encode display RGB on the GPU before handing bytes to VideoToolbox.
                Graphics.Blit(m_GameViewTexture, m_OutputTexture, m_ColorMaterial);
            }
            finally
            {
                GL.sRGBWrite = wasSrgbWrite;
                RenderTexture.active = previousTarget;
            }
            IntPtr request = mcmetal_queue_frame(m_Handle, m_NativeTexture, audioSample);
            if (request != IntPtr.Zero) IssueEvent(0, request);
        }

        public void Poll()
        {
            if (m_Retired || m_Handle == 0) return;
            RefreshStatus();
        }

        public void Stop(long samples, string audioPath, string outputPath)
        {
            IntPtr audio = AllocateUtf8(audioPath);
            IntPtr output = AllocateUtf8(outputPath);
            try
            {
                if (mcmetal_stop(m_Handle, samples, audio, output) == 0)
                {
                    RefreshStatus();
                    throw new InvalidOperationException(string.IsNullOrEmpty(Error) ? "Native recorder rejected stop." : Error);
                }
            }
            finally { Marshal.FreeHGlobal(audio); Marshal.FreeHGlobal(output); }
        }

        public void Retire()
        {
            if (m_Retired) return;
            m_Retired = true;
            if (m_Handle != 0) mcmetal_abort(m_Handle);
            if (TryRelease()) return;
            s_Retiring.Add(this);
            EnsureCleanup();
        }

        internal static void PumpCleanup()
        {
            for (int i = s_Retiring.Count - 1; i >= 0; --i)
                if (s_Retiring[i].TryRelease()) s_Retiring.RemoveAt(i);
        }

        private bool TryRelease()
        {
            if (m_Handle != 0 && mcmetal_destroy(m_Handle) == 0) return false;
            m_Handle = 0;
            m_RenderCommands?.Dispose();
            m_RenderCommands = null;
            if (m_ColorMaterial != null) UnityEngine.Object.Destroy(m_ColorMaterial);
            m_ColorMaterial = null;
            if (m_OutputTexture != null) UnityEngine.Object.Destroy(m_OutputTexture);
            if (m_GameViewTexture != null) UnityEngine.Object.Destroy(m_GameViewTexture);
            m_OutputTexture = null;
            m_GameViewTexture = null;
            return true;
        }

        private static void EnsureCleanup()
        {
            if (s_Cleanup != null) return;
            var host = new GameObject("Media Capture Resource Cleanup") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(host);
            s_Cleanup = host.AddComponent<NativeCaptureCleanup>();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= PumpCleanup;
            UnityEditor.EditorApplication.update += PumpCleanup;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
#endif
        }

        internal static void Shutdown()
        {
            UnityAvRecorder.Active?.Abort();
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            mcmetal_shutdown();
#endif
            PumpCleanup();
        }

        private void RefreshStatus()
        {
            if (mcmetal_status(m_Handle, out NativeCaptureStatus status, m_Error, m_Error.Length) == 0)
                throw new InvalidOperationException("Native recording session is unavailable.");
            Status = status;
            Error = DecodeError();
        }

        private void IssueEvent(int eventId, IntPtr data)
        {
            m_RenderCommands.Clear();
            m_RenderCommands.IssuePluginEventAndData(m_Callback, eventId, data);
            Graphics.ExecuteCommandBuffer(m_RenderCommands);
        }

        private string DecodeError()
        {
            int length = Array.IndexOf(m_Error, (byte)0);
            return Encoding.UTF8.GetString(m_Error, 0, length < 0 ? m_Error.Length : length);
        }

        private static IntPtr AllocateUtf8(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return pointer;
        }

        private static RenderTexture CreateTexture(int width, int height, GraphicsFormat format, string name)
        {
            var descriptor = new RenderTextureDescriptor(width, height)
            {
                graphicsFormat = format,
                depthBufferBits = 0, msaaSamples = 1, volumeDepth = 1,
                useMipMap = false, autoGenerateMips = false
            };
            var texture = new RenderTexture(descriptor)
            { name = name, hideFlags = HideFlags.HideAndDontSave };
            if (!texture.Create())
            {
                UnityEngine.Object.Destroy(texture);
                throw new InvalidOperationException("Cannot allocate a Game View recording texture.");
            }
            return texture;
        }

        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mcmetal_hardware(ulong handle);
        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong mcmetal_create(IntPtr source, ref NativeOptions options, [Out] byte[] error, int capacity);
        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mcmetal_queue_frame(ulong handle, IntPtr source, long sample);
        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mcmetal_get_render_callback();
        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mcmetal_status(ulong handle, out NativeCaptureStatus status, [Out] byte[] error, int capacity);
        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mcmetal_stop(ulong handle, long samples, IntPtr audio, IntPtr output);
        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mcmetal_abort(ulong handle);
        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mcmetal_destroy(ulong handle);
        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mcmetal_shutdown();
    }
}
