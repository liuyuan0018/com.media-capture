using System;
using System.Runtime.InteropServices;

namespace GameFramework.MediaCapture.Unity
{
    // Binary layout shared with the native backends. Preserve the existing Windows ABI.
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeCaptureStatus
    {
        internal int State, Queued;
        internal long Captured, Encoded, Duplicated, Dropped, GpuBytes, MaxLagFrames;
    }

    internal interface INativeVideoCapture
    {
        string BackendName { get; }
        bool HardwareEncodingConfirmed { get; }
        NativeCaptureStatus Status { get; }
        string Error { get; }
        int Width { get; }
        int Height { get; }
        long TextureBytes { get; }
        void Capture(long audioSample);
        void Poll();
        void Stop(long samples, string audioPath, string outputPath);
        void Retire();
    }
}
