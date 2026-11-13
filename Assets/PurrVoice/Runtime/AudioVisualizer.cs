using System;
using System.Collections.Generic;
using UnityEngine;

namespace PurrNet.Voice
{
    public class AudioVisualizer
    {
        private Queue<float> _sampleHistory = new Queue<float>();
        private int _maxSamples;
        private int _frequency;
        private int _sampleCounter;
        private int _downsampleRate;

        public float amplitudeScale { get; set; } = 1f;
        public float timeScale { get; set; } = 1f;

        public AudioVisualizer(float timeWindow = 3f, int frequency = 48000, int targetVisualizationSamples = 300)
        {
            _frequency = frequency;

            int totalSamplesInWindow = Mathf.RoundToInt(timeWindow * frequency);
            _downsampleRate = Mathf.Max(1, totalSamplesInWindow / targetVisualizationSamples);
            _maxSamples = totalSamplesInWindow / _downsampleRate;
        }

        public void AddSamples(ArraySegment<float> samples)
        {
            var arr = samples.Array;
            int off = samples.Offset;
            int count = samples.Count;
            int counter = _sampleCounter;
            int rate = _downsampleRate;
            float scale = amplitudeScale;
            int maxSamples = GetAdjustedMaxSamples();

            for (int i = 0; i < count; i++)
            {
                counter++;
                if (counter % rate == 0)
                {
                    _sampleHistory.Enqueue(arr[off + i] * scale);
                    if (_sampleHistory.Count > maxSamples)
                        _sampleHistory.Dequeue();
                }
            }

            _sampleCounter = counter;
        }

        private int GetAdjustedMaxSamples()
        {
            return Mathf.RoundToInt(_maxSamples * timeScale);
        }

        public float[] GetSamples()
        {
            return _sampleHistory.ToArray();
        }

        public float GetMaxAmplitude()
        {
            float max = 0f;
            var history = _sampleHistory.ToArray();
            for (int i = 0; i < history.Length; i++)
            {
                float abs = history[i] < 0 ? -history[i] : history[i];
                if (abs > max) max = abs;
            }
            return max;
        }

        public float GetRMSAmplitude()
        {
            if (_sampleHistory.Count == 0) return 0f;

            var history = _sampleHistory.ToArray();
            float sum = 0f;
            for (int i = 0; i < history.Length; i++)
            {
                sum += history[i] * history[i];
            }
            return Mathf.Sqrt(sum / history.Length);
        }

        public float GetCurrentTimeWindow()
        {
            return (_sampleHistory.Count * _downsampleRate) / (float)_frequency;
        }

        public void Clear()
        {
            _sampleHistory.Clear();
            _sampleCounter = 0;
        }
    }
}
