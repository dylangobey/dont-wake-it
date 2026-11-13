using UnityEngine;

namespace PurrNet.Voice
{
    [CreateAssetMenu(menuName = "PurrNet/Voice/Opus Codec Settings", fileName = "OpusCodecSettings")]
    public class OpusCodecSettings : AudioCodecSettings
    {
        [SerializeField] private VoiceQuality _quality = VoiceQuality.High;

        public VoiceQuality quality => _quality;

        public override IAudioCodec CreateCodec()
        {
            var settings = VoiceQualitySettings.Get(_quality);
            return new OpusCodec(settings);
        }
    }
}
