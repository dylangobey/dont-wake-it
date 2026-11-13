using System;
using UnityEngine;

namespace PurrNet.Voice
{
    public partial class PurrVoicePlayer
    {
#if UNITY_EDITOR
        [HideInInspector] public AudioVisualizer micInputVisualizer;
        [HideInInspector] public AudioVisualizer senderProcessedVisualizer;
        [HideInInspector] public AudioVisualizer networkSentVisualizer;
        [HideInInspector] public AudioVisualizer serverProcessedVisualizer;
        [HideInInspector] public AudioVisualizer receivedVisualizer;
        [HideInInspector] public AudioVisualizer streamedAudioVisualizerStart;
        [HideInInspector] public AudioVisualizer streamedAudioVisualizerEnd;

        public static int activeDebugWindows;

        public bool enableDebugVisualization => _purrVoicePlayerDebug != null;
        private PurrVoicePlayerDebug _purrVoicePlayerDebug;

        private bool ShouldDebug => activeDebugWindows > 0 && enableDebugVisualization;
#endif

        private void DebugAwake(int frequency)
        {
#if UNITY_EDITOR
            TryGetComponent(out _purrVoicePlayerDebug);

            if (!enableDebugVisualization)
                return;

            micInputVisualizer = new AudioVisualizer(_purrVoicePlayerDebug.timeWindow, frequency);
            senderProcessedVisualizer = new AudioVisualizer(_purrVoicePlayerDebug.timeWindow, frequency);
            networkSentVisualizer = new AudioVisualizer(_purrVoicePlayerDebug.timeWindow, frequency);
            serverProcessedVisualizer = new AudioVisualizer(_purrVoicePlayerDebug.timeWindow, frequency);
            receivedVisualizer = new AudioVisualizer(_purrVoicePlayerDebug.timeWindow, frequency);
            streamedAudioVisualizerStart = new AudioVisualizer(_purrVoicePlayerDebug.timeWindow, frequency);
            streamedAudioVisualizerEnd = new AudioVisualizer(_purrVoicePlayerDebug.timeWindow, frequency);

            output.onStartPlayingSample += DebugStreamedAudio;
            output.onEndPlayingSample += DebugStreamedAudioEnd;
#endif
        }

        private void DebugMicrophoneDataPreProcessing(ArraySegment<float> samples)
        {
#if UNITY_EDITOR
            if (ShouldDebug && isOwner)
                micInputVisualizer?.AddSamples(samples);
#endif
        }

        private void DebugMicrophoneDataPostProcessing(ArraySegment<float> samples)
        {
#if UNITY_EDITOR
            if (ShouldDebug && isOwner)
                senderProcessedVisualizer?.AddSamples(samples);
#endif
        }

        internal void DebugNetworkSentData(ArraySegment<float> samples)
        {
#if UNITY_EDITOR
            if (ShouldDebug && isOwner)
                networkSentVisualizer?.AddSamples(samples);
#endif
        }

        internal void DebugServerProcessed(ArraySegment<float> samples)
        {
#if UNITY_EDITOR
            if (ShouldDebug && isServer)
                serverProcessedVisualizer?.AddSamples(samples);
#endif
        }

        internal void DebugReceived(ArraySegment<float> samples)
        {
#if UNITY_EDITOR
            if (ShouldDebug && !isOwner)
                receivedVisualizer?.AddSamples(samples);
#endif
        }

        private void DebugStreamedAudio(ArraySegment<float> samples)
        {
#if UNITY_EDITOR
            if (ShouldDebug)
                streamedAudioVisualizerStart?.AddSamples(samples);
#endif
        }

        private void DebugStreamedAudioEnd(ArraySegment<float> samples)
        {
#if UNITY_EDITOR
            if (ShouldDebug)
                streamedAudioVisualizerEnd?.AddSamples(samples);
#endif
        }
    }
}
