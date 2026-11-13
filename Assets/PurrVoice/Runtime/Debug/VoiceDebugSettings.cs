using System;
using System.Buffers;
using UnityEngine;

namespace PurrNet.Voice
{
    [AddComponentMenu("PurrNet/Voice/Voice Debug Settings")]
    public class VoiceDebugSettings : MonoBehaviour
    {
        [Header("Mic Frequency Override")]
        [Tooltip("When enabled, overrides the microphone frequency reported to the encoder.")]
        public bool overrideMicFrequency;

        [Tooltip("The frequency to report instead of the real microphone frequency.")]
        public int micFrequency = 44100;

        [Header("ParrelSync Auto-Config")]
        [Tooltip("When enabled, automatically picks a frequency from the list based on clone index.")]
        public bool useCloneIndex;

        [Tooltip("Frequencies to assign per clone. Index 0 = main editor, 1 = first clone, etc.")]
        public int[] cloneFrequencies = { 48000, 44100, 16000, 8000 };

        [Header("Runtime Info (Read Only)")]
        [SerializeField] private int _detectedCloneIndex;
        [SerializeField] private int _actualMicFrequency;
        [SerializeField] private int _effectiveFrequency;
        [SerializeField] private int _codecTargetRate;

        private int _realMicFrequency;
        private double _resampleCarry;

        public int effectiveFrequency => _effectiveFrequency;
        public bool needsResample => overrideMicFrequency && _realMicFrequency > 0 && _realMicFrequency != micFrequency;

        private void Awake()
        {
            _detectedCloneIndex = GetCloneIndex();

            if (useCloneIndex && cloneFrequencies != null && cloneFrequencies.Length > 0)
            {
                int idx = Mathf.Clamp(_detectedCloneIndex, 0, cloneFrequencies.Length - 1);
                micFrequency = cloneFrequencies[idx];
                overrideMicFrequency = true;
            }
        }

        public int ResolveFrequency(int realMicFrequency)
        {
            _actualMicFrequency = realMicFrequency;
            _realMicFrequency = realMicFrequency;
            _effectiveFrequency = overrideMicFrequency ? micFrequency : realMicFrequency;
            _resampleCarry = 0d;
            return _effectiveFrequency;
        }

        public void ReportCodecTargetRate(int targetRate)
        {
            _codecTargetRate = targetRate;
        }

        public ArraySegment<float> ResampleMicData(ArraySegment<float> input, float[] rentedBuffer, out int outputCount)
        {
            if (!needsResample)
            {
                outputCount = 0;
                return input;
            }

            int inRate = _realMicFrequency;
            int outRate = micFrequency;

            int outCount = (int)((input.Count * (double)outRate / inRate) + _resampleCarry);
            _resampleCarry += (input.Count * (double)outRate / inRate) - outCount;

            if (outCount <= 0)
            {
                outputCount = 0;
                return input;
            }

            var buffer = ArrayPool<float>.Shared.Rent(outCount);
            float ratio = inRate / (float)outRate;

            for (int i = 0; i < outCount; i++)
            {
                float srcIdx = i * ratio;
                int s0 = (int)srcIdx;
                int s1 = Math.Min(s0 + 1, input.Count - 1);
                float frac = srcIdx - s0;
                buffer[i] = input.Array![input.Offset + s0] * (1f - frac)
                          + input.Array![input.Offset + s1] * frac;
            }

            outputCount = outCount;
            return new ArraySegment<float>(buffer, 0, outCount);
        }

        public void ReturnResampleBuffer(float[] buffer)
        {
            if (buffer != null)
                ArrayPool<float>.Shared.Return(buffer);
        }

        private static int GetCloneIndex()
        {
#if UNITY_EDITOR
            string projectPath = Application.dataPath.Replace("/Assets", "");
            string cloneFile = System.IO.Path.Combine(projectPath, ".clone");

            if (!System.IO.File.Exists(cloneFile))
                return 0;

            string argFile = System.IO.Path.Combine(projectPath, ".clonearg");
            if (System.IO.File.Exists(argFile))
            {
                string arg = System.IO.File.ReadAllText(argFile).Trim();
                if (int.TryParse(arg, out int idx))
                    return idx;
            }

            return 1;
#else
            return 0;
#endif
        }
    }
}
