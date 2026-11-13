using UnityEngine;

namespace PurrNet.Voice
{
    [CreateAssetMenu(fileName = "PitchShiftFilter", menuName = "PurrNet/Voice Chat/Filters/Pitch Shift Filter")]
    public class PitchShiftFilter : PurrAudioFilter
    {
        [Range(-12f, 12f)]
        [Tooltip("Pitch shift in semitones. Positive = higher, negative = lower.")]
        public float semitones;

        public override FilterInstance CreateInstance()
        {
            return new PitchShiftFilterInstance(this);
        }
    }
}
