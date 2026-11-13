using UnityEngine;

namespace PurrNet.Voice
{
    public abstract class AudioCodecSettings : ScriptableObject
    {
        public abstract IAudioCodec CreateCodec();
    }
}
