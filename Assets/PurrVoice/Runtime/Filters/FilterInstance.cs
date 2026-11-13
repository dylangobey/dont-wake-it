using System;

namespace PurrNet.Voice
{
    public abstract class FilterInstance
    {
        /// <summary>
        /// If true, Process(ref float) will be called per-sample. Chunk-only filters (RNNoise, Ducking, NoiseGate)
        /// should keep this false to avoid 500+ redundant virtual calls per frame.
        /// </summary>
        public virtual bool SupportsPerSampleProcessing => false;

        public virtual void Process(ref float inputSample, int frequency, float strength) {}

        public virtual void Process(ArraySegment<float> inputSample, int frequency, float strength) {}
    }
}
