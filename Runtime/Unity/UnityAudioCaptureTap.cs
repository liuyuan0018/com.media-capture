using System;
using System.Buffers;
using UnityEngine;

namespace GameFramework.MediaCapture.Unity
{
    [DisallowMultipleComponent]
    internal sealed class UnityAudioCaptureTap : MonoBehaviour
    {
        private PcmWaveWriter writer;
        private double sessionStartDsp;
        private int sampleRate;
        private long nextFrame;
        private long capturedFrames;
        private bool accepting;
        private bool receivedCallback;
        private double firstCallbackDsp;

        internal long CapturedFrames => capturedFrames;
        internal bool ReceivedCallback => receivedCallback;
        internal double FirstCallbackDsp => firstCallbackDsp;

        internal void Begin(PcmWaveWriter target, double startDsp, int outputSampleRate)
        {
            writer = target;
            sessionStartDsp = startDsp;
            sampleRate = outputSampleRate;
            nextFrame = 0;
            capturedFrames = 0;
            accepting = true;
            receivedCallback = false;
            firstCallbackDsp = startDsp;
        }

        internal void End()
        {
            accepting = false;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!accepting || writer == null || data == null || channels <= 0)
            {
                return;
            }

            double callbackDsp = AudioSettings.dspTime;
            if (!receivedCallback)
            {
                receivedCallback = true;
                firstCallbackDsp = callbackDsp;
                nextFrame = Math.Max(0, (long)Math.Round((callbackDsp - sessionStartDsp) * sampleRate));
            }

            float[] copy = ArrayPool<float>.Shared.Rent(data.Length);
            Array.Copy(data, copy, data.Length);
            int frames = data.Length / channels;
            writer.TryEnqueue(nextFrame, copy, data.Length, channels);
            nextFrame += frames;
            capturedFrames += frames;
        }
    }
}
