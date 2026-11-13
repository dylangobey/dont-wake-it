using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Voice.FMODIntegration
{
    /// <summary>
    /// These rolloff modes correspond to FMOD's 3D rolloff settings.
    /// </summary>
    public enum VoiceRolloffMode : uint
    {
        Inverse = FMOD.MODE._3D_INVERSEROLLOFF,
        Linear = FMOD.MODE._3D_LINEARROLLOFF,
        LinearSquared = FMOD.MODE._3D_LINEARSQUAREROLLOFF,
        InverseTapered = FMOD.MODE._3D_INVERSETAPEREDROLLOFF,
    }

    /// <summary>
    /// Output provider that routes PurrVoice audio through FMOD instead of Unity's AudioSource.
    /// Drop this on a GameObject in place of AudioSourceVoiceProvider.
    /// </summary>
    public class FMODOutputProvider : OutputProvider
    {
        [Tooltip("FMOD channel priority. 0 = highest (never culled), 256 = lowest. " +
                 "Set low to ensure voice channels aren't culled when other sounds play.")]
        [Range(0, 256)]
        [SerializeField] private int _channelPriority;

        [Tooltip("FMOD internal decode buffer size in milliseconds. " +
                 "Higher values prevent audio clicks but add latency.")]
        [SerializeField, PurrLock, Range(20, 500)]
        private int _decodeBufferMs = 100;

        [Tooltip("Amount of audio to buffer before playback starts, in milliseconds. " +
                 "Higher values prevent underruns but add latency.")]
        [SerializeField, PurrLock, Range(50, 1000)]
        private int _preBufferMs = 200;

        [Tooltip("Playback volume for this voice output.")]
        [SerializeField, Range(0f, 1f)]
        private float _volume = 1f;

        [Tooltip("Enable FMOD 3D positioning for this voice output.")]
        [SerializeField]
        private bool _spatialize = true;

        [Tooltip("Transform to track for FMOD 3D positioning. Defaults to this GameObject if unset.")]
        [SerializeField]
        private Transform _trackingTransform;

        [Tooltip("Distance at which the voice is full volume in FMOD 3D mode.")]
        [SerializeField, Min(0f)]
        private float _minDistance = 5f;

        [Tooltip("Distance at which the voice stops attenuating in FMOD 3D mode.")]
        [SerializeField, Min(0f)]
        private float _maxDistance = 55f;

        [Tooltip("FMOD 3D rolloff model to use when spatial playback is enabled.")]
        [SerializeField]
        private VoiceRolloffMode _rolloffMode = VoiceRolloffMode.InverseTapered;

        private FMODVoiceOutput _output;

        public override IVoiceOutput output => _output;

        /// <summary>
        /// FMOD channel priority. 0 = highest (never culled), 256 = lowest.
        /// Can be changed at runtime.
        /// </summary>
        public int channelPriority
        {
            get => _output?.channelPriority ?? _channelPriority;
            set
            {
                _channelPriority = value;
                if (_output != null)
                    _output.channelPriority = value;
            }
        }

        public float volume
        {
            get => _output?.volume ?? _volume;
            set
            {
                _volume = Mathf.Clamp01(value);
                if (_output != null)
                    _output.volume = _volume;
            }
        }

        public bool spatialize
        {
            get => _output?.spatialize ?? _spatialize;
            set
            {
                _spatialize = value;
                if (_output != null)
                    _output.spatialize = value;
            }
        }

        public Transform trackingTransform
        {
            get => _trackingTransform;
            set
            {
                _trackingTransform = value;
                if (_output != null)
                    _output.trackingTransform = value ? value : transform;
            }
        }

        public float minDistance
        {
            get => _output?.minDistance ?? _minDistance;
            set
            {
                _minDistance = Mathf.Max(0f, value);
                if (_output != null)
                    _output.minDistance = _minDistance;
            }
        }

        public float maxDistance
        {
            get => _output?.maxDistance ?? _maxDistance;
            set
            {
                _maxDistance = Mathf.Max(0f, value);
                if (_output != null)
                    _output.maxDistance = _maxDistance;
            }
        }

        public VoiceRolloffMode rolloffMode
        {
            get => _output?.rolloffMode ?? _rolloffMode;
            set
            {
                _rolloffMode = value;
                if (_output != null)
                    _output.rolloffMode = value;
            }
        }

        public override void Init(IAudioInputSource inputSource, ProcessSamplesDelegate processSamples = null,
            params FilterLevel[] levels)
        {
            _output = new FMODVoiceOutput();
            _output.channelPriority = _channelPriority;
            _output.decodeBufferMs = _decodeBufferMs;
            _output.preBufferMs = _preBufferMs;
            _output.volume = _volume;
            _output.spatialize = _spatialize;
            _output.trackingTransform = _trackingTransform ? _trackingTransform : transform;
            _output.minDistance = _minDistance;
            _output.maxDistance = _maxDistance;
            _output.rolloffMode = _rolloffMode;
            _output.Init(inputSource, processSamples, levels);
            isInitialized = true;
        }

        public override void SetInput(IAudioInputSource input)
        {
            _output?.SetInput(input);
        }

        private void OnDestroy()
        {
            _output?.Dispose();
        }

        private void LateUpdate()
        {
            _output?.UpdateTracking();
        }
    }
}
