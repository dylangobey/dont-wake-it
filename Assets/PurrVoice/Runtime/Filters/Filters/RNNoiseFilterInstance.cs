using System;
using System.Buffers;
using UnityEngine;

namespace PurrNet.Voice
{
    public class RNNoiseFilterInstance : FilterInstance
    {
        private readonly RNNoiseFilter _def;
        private readonly IntPtr _state;

        private readonly float[] _inFrame = new float[RNNoiseNative.FRAME_SIZE];
        private readonly float[] _readyFrame = new float[RNNoiseNative.FRAME_SIZE];

        private int _inPos;
        private float _lastVad;

        private const float SCALE_UP = 32768f;
        private const float SCALE_DOWN = 1f / 32768f;

        /// <summary>
        /// Voice activity probability from the last processed frame (0.0 to 1.0).
        /// Useful for UI indicators or adaptive behavior.
        /// </summary>
        public float lastVad => _lastVad;

        public RNNoiseFilterInstance(RNNoiseFilter def)
        {
            _def = def;
            _state = RNNoiseNative.Create();

            if (_state == IntPtr.Zero)
                Debug.LogWarning("[PurrVoice] Failed to create RNNoise state. Noise suppression will be disabled.");
        }

        ~RNNoiseFilterInstance()
        {
            RNNoiseNative.Destroy(_state);
        }

        public override void Process(ArraySegment<float> inputSamples, int frequency, float strength)
        {
            if (_state == IntPtr.Zero || strength <= 0f)
                return;

            if (frequency == RNNoiseNative.SAMPLE_RATE)
                ProcessDirect(inputSamples, strength);
            else
                ProcessResampled(inputSamples, frequency, strength);
        }

        private void ProcessDirect(ArraySegment<float> samples, float strength)
        {
            float[] arr = samples.Array;
            int off = samples.Offset;
            int count = samples.Count;

            for (int i = 0; i < count; i++)
            {
                float output = _readyFrame[_inPos];
                _inFrame[_inPos] = arr[off + i] * SCALE_UP;
                _inPos++;

                if (_inPos >= RNNoiseNative.FRAME_SIZE)
                {
                    _lastVad = RNNoiseNative.ProcessFrame(_state, _readyFrame, _inFrame);

                    if (_lastVad < _def.vadThreshold)
                        Array.Clear(_readyFrame, 0, RNNoiseNative.FRAME_SIZE);
                    else
                    {
                        for (int j = 0; j < RNNoiseNative.FRAME_SIZE; j++)
                            _readyFrame[j] *= SCALE_DOWN;
                    }

                    _inPos = 0;
                    output = _readyFrame[_inPos];
                }

                arr[off + i] = strength >= 1f
                    ? output
                    : Mathf.Lerp(arr[off + i], output, strength);
            }
        }

        private void ProcessResampled(ArraySegment<float> inputSamples, int frequency, float strength)
        {
            float[] arr = inputSamples.Array;
            int off = inputSamples.Offset;
            int inCount = inputSamples.Count;

            int resampledCount = (int)Math.Ceiling(inCount * (double)RNNoiseNative.SAMPLE_RATE / frequency);

            var resampled = ArrayPool<float>.Shared.Rent(resampledCount);
            var processed = ArrayPool<float>.Shared.Rent(resampledCount);

            try
            {
                float srcRatio = frequency / (float)RNNoiseNative.SAMPLE_RATE;
                for (int i = 0; i < resampledCount; i++)
                {
                    float srcPos = i * srcRatio;
                    int s0 = (int)srcPos;
                    int s1 = Math.Min(s0 + 1, inCount - 1);
                    float frac = srcPos - s0;
                    resampled[i] = arr[off + s0] * (1f - frac) + arr[off + s1] * frac;
                }

                for (int i = 0; i < resampledCount; i++)
                {
                    processed[i] = _readyFrame[_inPos];
                    _inFrame[_inPos] = resampled[i] * SCALE_UP;
                    _inPos++;

                    if (_inPos >= RNNoiseNative.FRAME_SIZE)
                    {
                        _lastVad = RNNoiseNative.ProcessFrame(_state, _readyFrame, _inFrame);

                        if (_lastVad < _def.vadThreshold)
                            Array.Clear(_readyFrame, 0, RNNoiseNative.FRAME_SIZE);
                        else
                        {
                            for (int j = 0; j < RNNoiseNative.FRAME_SIZE; j++)
                                _readyFrame[j] *= SCALE_DOWN;
                        }

                        _inPos = 0;
                        processed[i] = _readyFrame[_inPos];
                    }
                }

                float dstRatio = RNNoiseNative.SAMPLE_RATE / (float)frequency;
                for (int i = 0; i < inCount; i++)
                {
                    float srcPos = i * dstRatio;
                    int s0 = (int)srcPos;
                    int s1 = Math.Min(s0 + 1, resampledCount - 1);
                    float frac = srcPos - s0;
                    float clean = processed[s0] * (1f - frac) + processed[s1] * frac;
                    arr[off + i] = Mathf.Lerp(arr[off + i], clean, strength);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(resampled);
                ArrayPool<float>.Shared.Return(processed);
            }
        }
    }
}
