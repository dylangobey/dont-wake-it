using System;
using System.Diagnostics;

namespace PurrNet.Voice
{
    /// <summary>
    /// Static utility that tracks the current level of voice playback from remote players.
    /// Used by DuckingFilter to know when to attenuate the local microphone.
    /// </summary>
    public static class VoicePlaybackMonitor
    {
        private static float _playbackRms;
        private static long _lastReportTimestamp;

        private const float SILENCE_TIMEOUT = 0.06f;
        private const int RMS_SAMPLE_STRIDE = 32;

        /// <summary>
        /// Current playback level (RMS). Returns 0 if no recent playback.
        /// Thread-safe for reads from worker threads (e.g. filter processing).
        /// </summary>
        public static float playbackLevel
        {
            get
            {
                double elapsedSeconds = (Stopwatch.GetTimestamp() - _lastReportTimestamp) / (double)Stopwatch.Frequency;
                if (elapsedSeconds > SILENCE_TIMEOUT)
                    return 0f;
                return _playbackRms;
            }
        }

        /// <summary>
        /// Call this from the playback path when voice audio samples are being played.
        /// Subsamples every Nth value for RMS to avoid iterating the full buffer.
        /// </summary>
        public static void ReportPlayback(ArraySegment<float> samples)
        {
            if (samples.Array == null || samples.Count == 0)
                return;

            float sum = 0f;
            int counted = 0;
            var arr = samples.Array;
            int off = samples.Offset;
            int count = samples.Count;

            for (int i = 0; i < count; i += RMS_SAMPLE_STRIDE)
            {
                float s = arr[off + i];
                sum += s * s;
                counted++;
            }

            float rms = (float)Math.Sqrt(sum / counted);
            _playbackRms = Math.Max(_playbackRms * 0.7f, rms);
            _lastReportTimestamp = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Resets the monitor state.
        /// </summary>
        public static void Reset()
        {
            _playbackRms = 0f;
            _lastReportTimestamp = 0;
        }
    }
}
