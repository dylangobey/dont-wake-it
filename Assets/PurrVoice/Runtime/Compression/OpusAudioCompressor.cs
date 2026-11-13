using System;
using System.Collections.Generic;
using UnityEngine;

namespace PurrNet.Voice
{
    public static class OpusAudioCompressor
    {
        private static Dictionary<int, OpusCodec> _codecs = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _codecs = new Dictionary<int, OpusCodec>();
        }

        public static byte[] Encode(float[] input, int sampleRate, int frameSize)
        {
            if (!_codecs.TryGetValue(sampleRate, out var codec))
            {
                codec = new OpusCodec(sampleRate, 1, frameSize);
                _codecs[sampleRate] = codec;
            }

            return codec.Encode(input);
        }

        public static float[] Decode(byte[] data, int sampleRate, int frameSize)
        {
            if (!_codecs.TryGetValue(sampleRate, out var codec))
            {
                codec = new OpusCodec(sampleRate, 1, frameSize);
                _codecs[sampleRate] = codec;
            }

            return codec.Decode(data);
        }
    }
}