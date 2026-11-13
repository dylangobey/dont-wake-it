using System;
using PurrNet.Voice;
using UnityEngine;

public class NoiseGateFilterInstance : FilterInstance
{
    private readonly NoiseGateFilter _def;
    private float _gate;
    private float _prevGain;

    public NoiseGateFilterInstance(NoiseGateFilter def) => _def = def;

    public override void Process(ArraySegment<float> inputSamples, int frequency, float strength)
    {
        float[] arr = inputSamples.Array;
        int off = inputSamples.Offset;
        int count = inputSamples.Count;

        // RMS: parallel sum of squares when beneficial
        double sumSq = 0;
        if (VoiceThreading.IsMultithreadingSupported && count >= VoiceThreading.ParallelThreshold)
        {
            object lockObj = new object();
            System.Threading.Tasks.Parallel.For(0, count, () => 0.0, (i, state, local) =>
            {
                float s = arr[off + i];
                return local + (double)(s * s);
            }, local => { lock (lockObj) { sumSq += local; } });
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                float s = arr[off + i];
                sumSq += s * s;
            }
        }

        float rms = Mathf.Sqrt((float)(sumSq / count));
        float db = 20f * Mathf.Log10(Mathf.Max(rms, 1e-10f));
        bool gateOpen = db > _def.noiseGateThreshold;

        float deltaTime = count / (float)frequency;

        if (gateOpen)
        {
            float attackTime = Mathf.Max(_def.gateAttackTime, 0.0001f);
            _gate = Mathf.Clamp01(_gate + (deltaTime / attackTime));
        }
        else
        {
            float releaseTime = Mathf.Max(_def.gateReleaseTime, 0.0001f);
            _gate = Mathf.Clamp01(_gate - (deltaTime / releaseTime));
        }

        float gain = _gate * strength;
        float prevGain = _prevGain;
        _prevGain = gain;

        if (Math.Abs(gain - prevGain) < 1e-6f)
        {
            VoiceThreading.For(0, count, i => arr[off + i] *= gain);
        }
        else
        {
            float invCount = 1f / count;
            for (int i = 0; i < count; i++)
            {
                float t = (i + 1) * invCount;
                arr[off + i] *= prevGain + (gain - prevGain) * t;
            }
        }
    }
}


