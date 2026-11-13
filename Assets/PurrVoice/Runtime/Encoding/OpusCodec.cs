using Concentus.Enums;
using System;
using System.Buffers;
using Concentus;

namespace PurrNet.Voice
{
    public class OpusCodec : IAudioCodec
    {
        public const int MaxEncodedBytes = 4000;

        private readonly IOpusEncoder _encoder;
        private readonly IOpusDecoder _decoder;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _frameSize;

        public int SampleRate => _sampleRate;
        public int TargetSampleRate => _sampleRate;
        public int FrameSize => _frameSize;

        public OpusCodec(int sampleRate, int channels, int frameSize)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _frameSize = frameSize;
            _encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
            _decoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
        }

        public OpusCodec(VoiceQualitySettings settings) : this(settings.sampleRate, 1, settings.FrameSize)
        {
            _encoder.Bitrate = settings.bitrate;
            _encoder.Complexity = settings.complexity;
            _encoder.UseVBR = true;
            _encoder.UseConstrainedVBR = true;
            _encoder.UseInbandFEC = settings.fec;
            _encoder.UseDTX = settings.dtx;
        }

        /// <summary>
        /// Encode into a caller-provided buffer. Zero allocations.
        /// </summary>
        /// <param name="input">Input float samples.</param>
        /// <param name="outputBuffer">Output buffer, must be at least MaxEncodedBytes.</param>
        /// <returns>Number of bytes written to outputBuffer.</returns>
        public int Encode(float[] input, byte[] outputBuffer)
        {
            return Encode(input, 0, input.Length, outputBuffer);
        }

        /// <summary>
        /// Encode a slice into a caller-provided buffer. Zero allocations.
        /// </summary>
        public int Encode(float[] input, int offset, int count, byte[] outputBuffer)
        {
            var shortInput = ArrayPool<short>.Shared.Rent(count);
            try
            {
                for (int i = 0; i < count; i++)
                    shortInput[i] = (short)(Math.Clamp(input[offset + i], -1f, 1f) * short.MaxValue);

                return _encoder.Encode(shortInput.AsSpan(0, count), _frameSize, outputBuffer.AsSpan(), outputBuffer.Length);
            }
            finally
            {
                ArrayPool<short>.Shared.Return(shortInput);
            }
        }

        /// <summary>
        /// Decode into a caller-provided buffer. Zero allocations.
        /// </summary>
        /// <param name="data">Encoded bytes.</param>
        /// <param name="outputBuffer">Output buffer for float samples, must be at least frameSize * channels.</param>
        /// <returns>Number of samples written to outputBuffer.</returns>
        public int Decode(byte[] data, float[] outputBuffer)
        {
            return Decode(data, 0, data?.Length ?? 0, outputBuffer);
        }

        /// <summary>
        /// Decode a slice into a caller-provided buffer. Zero allocations.
        /// </summary>
        public int Decode(byte[] data, int offset, int count, float[] outputBuffer)
        {
            if (data == null || count <= 0) return 0;

            var shortOutput = ArrayPool<short>.Shared.Rent(_frameSize * _channels);
            try
            {
                int len = _decoder.Decode(data.AsSpan(offset, count), shortOutput.AsSpan(), _frameSize, false);

                for (int i = 0; i < len; i++)
                    outputBuffer[i] = shortOutput[i] / (float)short.MaxValue;

                return len;
            }
            finally
            {
                ArrayPool<short>.Shared.Return(shortOutput);
            }
        }

        /// <summary>
        /// Allocating Encode overload for callers that need a new byte[]. Prefer Encode(input, outputBuffer) for GC-free path.
        /// </summary>
        public byte[] Encode(float[] input)
        {
            var outputBuffer = ArrayPool<byte>.Shared.Rent(MaxEncodedBytes);
            try
            {
                int len = Encode(input, outputBuffer);
                var result = new byte[len];
                Array.Copy(outputBuffer, 0, result, 0, len);
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(outputBuffer);
            }
        }

        /// <summary>
        /// Allocating Decode overload for callers that need a new float[]. Prefer Decode(data, outputBuffer) for GC-free path.
        /// </summary>
        public float[] Decode(byte[] data)
        {
            var tmp = ArrayPool<float>.Shared.Rent(_frameSize * _channels);
            try
            {
                int len = Decode(data, tmp);
                var result = new float[len];
                Array.Copy(tmp, 0, result, 0, len);
                return result;
            }
            finally
            {
                ArrayPool<float>.Shared.Return(tmp);
            }
        }

        public void Dispose()
        {
            (_encoder as IDisposable)?.Dispose();
            (_decoder as IDisposable)?.Dispose();
        }
    }
}
