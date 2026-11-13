using System;

namespace PurrNet.Voice
{
    public interface IAudioCodec : IDisposable
    {
        int TargetSampleRate { get; }

        int FrameSize { get; }

        int Encode(float[] input, int offset, int count, byte[] output);

        int Decode(byte[] data, int offset, int count, float[] output);
    }
}
