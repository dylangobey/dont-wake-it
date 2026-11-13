using PurrNet.Logging;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Voice
{
    public class AudioSourceVoiceProvider : OutputProvider
    {
        [SerializeField, PurrLock] private AudioSource _audioSource;

        [Tooltip("Amount of audio to buffer before playback starts, in seconds. " +
                 "Higher values prevent underruns but add latency.")]
        [SerializeField, PurrLock, Range(0.05f, 1f)]
        private float _preBufferSeconds = 0.2f;

        [Tooltip("Total pre-allocated buffer capacity in seconds. " +
                 "Must be larger than the pre-buffer. Only affects memory allocation.")]
        [SerializeField, PurrLock, Range(0.5f, 5f)]
        private float _bufferCapacitySeconds = 1f;

        private StreamedAudioClip _output;

        public override IVoiceOutput output => _output;

        public override void Init(IAudioInputSource inputSource, ProcessSamplesDelegate processSamples = null, params FilterLevel[] levels)
        {
            if (!_audioSource)
                PurrLogger.LogError($"AudioSourceVoiceProvider has no AudioSource assigned. Audio will be received but not played.", this);

            _output = new StreamedAudioClip();
            _output.preBufferSeconds = _preBufferSeconds;
            _output.bufferCapacitySeconds = _bufferCapacitySeconds;
            _output.Init(inputSource, processSamples, levels);
            _output.SetAudioSource(_audioSource);
            isInitialized = true;
        }

        /// <summary>
        /// Assigns an AudioSource at runtime. Can be called before or after Init.
        /// </summary>
        public void SetAudioSource(AudioSource audioSource)
        {
            _audioSource = audioSource;
            if (_output != null)
                _output.SetAudioSource(audioSource);
        }

        public override void SetInput(IAudioInputSource input)
        {
            _output.SetInput(input);
        }

        private void OnDestroy()
        {
            _output?.Dispose();
        }

#if UNITY_EDITOR
        private void Reset()
        {
            if (!_audioSource)
                _audioSource = GetComponentInChildren<AudioSource>();

            if (_audioSource)
                _audioSource.dopplerLevel = 0;
        }
#endif
    }
}
