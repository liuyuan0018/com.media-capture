using UnityEngine;

namespace GameFramework.MediaCapture.Unity
{
    [AddComponentMenu("")]
    internal sealed class NativeCaptureCleanup : MonoBehaviour
    {
        private void Update() => NativeD3D11Capture.PumpCleanup();
        private void OnApplicationQuit() => NativeD3D11Capture.Shutdown();
    }
}
