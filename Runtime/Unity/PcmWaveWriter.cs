using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace GameFramework.MediaCapture.Unity
{
    internal sealed class PcmWaveWriter : IDisposable
    {
        private readonly struct AudioBlock
        {
            internal AudioBlock(long startFrame, float[] samples, int sampleCount, int channels)
            {
                StartFrame = startFrame;
                Samples = samples;
                SampleCount = sampleCount;
                Channels = channels;
            }

            internal long StartFrame { get; }
            internal float[] Samples { get; }
            internal int SampleCount { get; }
            internal int Channels { get; }
            internal int Frames => Channels > 0 ? SampleCount / Channels : 0;
        }

        private const int OutputChannels = 2;
        private readonly FileStream stream;
        private readonly BinaryWriter writer;
        private readonly ConcurrentQueue<AudioBlock> queue = new ConcurrentQueue<AudioBlock>();
        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private readonly Thread thread;
        private readonly int sampleRate;
        private readonly int maxQueuedBlocks;
        private volatile bool accepting = true;
        private volatile bool finished;
        private Exception failure;
        private int queuedBlocks;
        private long writtenFrames;
        private long droppedFrames;

        internal PcmWaveWriter(string path, int sampleRate, int maxQueuedBlocks)
        {
            this.sampleRate = sampleRate;
            this.maxQueuedBlocks = maxQueuedBlocks;
            stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            writer = new BinaryWriter(stream);
            WriteHeader(0);
            thread = new Thread(WriteLoop)
            {
                IsBackground = true,
                Name = "GameFramework audio capture writer"
            };
            thread.Start();
        }

        internal long DroppedFrames => Interlocked.Read(ref droppedFrames);

        internal bool TryEnqueue(long startFrame, float[] samples, int sampleCount, int channels)
        {
            if (!accepting || samples == null || sampleCount <= 0 || channels <= 0)
            {
                if (samples != null)
                {
                    ArrayPool<float>.Shared.Return(samples);
                }
                return false;
            }

            int frames = sampleCount / channels;
            if (Interlocked.Increment(ref queuedBlocks) > maxQueuedBlocks)
            {
                Interlocked.Decrement(ref queuedBlocks);
                Interlocked.Add(ref droppedFrames, frames);
                ArrayPool<float>.Shared.Return(samples);
                return false;
            }

            queue.Enqueue(new AudioBlock(startFrame, samples, sampleCount, channels));
            signal.Set();
            return true;
        }

        internal void Finish(long expectedFrames)
        {
            accepting = false;
            signal.Set();
            if (!thread.Join(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Timed out while draining captured audio.");
            }

            if (failure != null)
            {
                throw new IOException("Audio writer failed.", failure);
            }

            if (writtenFrames < expectedFrames)
            {
                WriteSilence(expectedFrames - writtenFrames);
            }

            long finalFrames = Math.Max(writtenFrames, expectedFrames);
            stream.Position = 0;
            WriteHeader(finalFrames);
            writer.Flush();
            stream.Flush(true);
            finished = true;
        }

        public void Dispose()
        {
            if (accepting)
            {
                accepting = false;
                signal.Set();
                thread.Join(TimeSpan.FromSeconds(2));
            }

            writer.Dispose();
            stream.Dispose();
            signal.Dispose();
        }

        private void WriteLoop()
        {
            try
            {
                while (accepting || !queue.IsEmpty)
                {
                    if (!queue.TryDequeue(out AudioBlock block))
                    {
                        signal.WaitOne(50);
                        continue;
                    }

                    Interlocked.Decrement(ref queuedBlocks);
                    try
                    {
                        if (block.StartFrame > writtenFrames)
                        {
                            WriteSilence(block.StartFrame - writtenFrames);
                        }

                        long skip = Math.Max(0, writtenFrames - block.StartFrame);
                        WriteBlock(block, skip);
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(block.Samples);
                    }
                }
            }
            catch (Exception exception)
            {
                failure = exception;
                accepting = false;
            }
        }

        private void WriteBlock(AudioBlock block, long skipFrames)
        {
            for (long frame = skipFrames; frame < block.Frames; frame++)
            {
                int offset = checked((int)frame * block.Channels);
                float left = block.Samples[offset];
                float right = block.Channels > 1 ? block.Samples[offset + 1] : left;
                writer.Write(ToPcm16(left));
                writer.Write(ToPcm16(right));
                writtenFrames++;
            }
        }

        private void WriteSilence(long frames)
        {
            const int silenceChunkFrames = 4096;
            byte[] zeros = new byte[silenceChunkFrames * OutputChannels * sizeof(short)];
            while (frames > 0)
            {
                int count = (int)Math.Min(frames, silenceChunkFrames);
                writer.Write(zeros, 0, count * OutputChannels * sizeof(short));
                writtenFrames += count;
                frames -= count;
            }
        }

        private void WriteHeader(long frames)
        {
            long dataBytes = checked(frames * OutputChannels * sizeof(short));
            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(checked((int)(36 + dataBytes)));
            writer.Write(new[] { 'W', 'A', 'V', 'E' });
            writer.Write(new[] { 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)OutputChannels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * OutputChannels * sizeof(short));
            writer.Write((short)(OutputChannels * sizeof(short)));
            writer.Write((short)16);
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(checked((int)dataBytes));
        }

        private static short ToPcm16(float value)
        {
            value = Math.Max(-1f, Math.Min(1f, value));
            return (short)Math.Round(value * (value < 0 ? 32768f : 32767f));
        }
    }
}
