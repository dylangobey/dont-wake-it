namespace PurrNet.Voice
{
    public enum VoiceQuality
    {
        Low,
        Medium,
        High,
        Ultra
    }

    public readonly struct VoiceQualitySettings
    {
        public readonly int sampleRate;
        public readonly int bitrate;
        public readonly int complexity;
        public readonly bool fec;
        public readonly bool dtx;

        public VoiceQualitySettings(int sampleRate, int bitrate, int complexity, bool fec, bool dtx)
        {
            this.sampleRate = sampleRate;
            this.bitrate = bitrate;
            this.complexity = complexity;
            this.fec = fec;
            this.dtx = dtx;
        }

        public int FrameSize => sampleRate / 50; // 20ms frames

        public static VoiceQualitySettings Get(VoiceQuality quality)
        {
            switch (quality)
            {
                case VoiceQuality.Low:    return new VoiceQualitySettings(8000,  12000, 3,  false, true);
                case VoiceQuality.Medium: return new VoiceQualitySettings(16000, 24000, 5,  true,  true);
                case VoiceQuality.High:   return new VoiceQualitySettings(24000, 40000, 7,  true,  false);
                case VoiceQuality.Ultra:  return new VoiceQualitySettings(48000, 64000, 10, true,  false);
                default:                  return Get(VoiceQuality.High);
            }
        }
    }
}
