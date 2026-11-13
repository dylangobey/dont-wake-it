using PurrNet.Logging;
using PurrVoice;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditorInternal;
#endif

namespace PurrNet.Voice
{
    /// <summary>
    /// <see cref="OutputProvider"/> that clones a voice stream into every
    /// <see cref="AudioSource"/> assigned in the Inspector.
    /// Internally wraps each AudioSource in a <see cref="StreamedAudioClip"/>
    /// and then groups them under a single <see cref="MultiVoiceOutput"/>.
    /// </summary>
    public class MultiAudioSourceVoiceProvider : OutputProvider
    {
        // AudioSources that will receive duplicated voice audio.
        [SerializeField] private List<AudioSource> _audioSources = new();

        private List<StreamedAudioClip> _clips;
        private MultiVoiceOutput _output;

        // Cached init parameters for adding sources after Init
        private IAudioInputSource _cachedInput;
        private ProcessSamplesDelegate _cachedDsp;
        private FilterLevel[] _cachedLevels;

        /// <inheritdoc/>
        public override IVoiceOutput output => _output;

        /// <summary>
        /// Builds one <see cref="StreamedAudioClip"/> per AudioSource,
        /// ensures each has a <see cref="PurrAudioReader"/>, and finally
        /// wraps them all in a <see cref="MultiVoiceOutput"/>.
        /// </summary>
        public override void Init(IAudioInputSource input,
                                  ProcessSamplesDelegate dsp = null,
                                  params FilterLevel[] lvls)
        {
            _cachedInput = input;
            _cachedDsp = dsp;
            _cachedLevels = lvls;

            _clips = new List<StreamedAudioClip>(_audioSources.Count);

            if (_audioSources.Count == 0)
            {
                PurrLogger.LogError($"MultiAudioSourceVoiceProvider has no AudioSources assigned. Audio will be received but not played.", this);
            }

            foreach (AudioSource src in _audioSources)
            {
                if (!src) continue;
                InitClipForSource(src);
            }

            if (_clips.Count == 0 && _audioSources.Count > 0)
            {
                PurrLogger.LogError($"MultiAudioSourceVoiceProvider has {_audioSources.Count} AudioSource slot(s) but all are null. Audio will be received but not played.", this);
            }

            _output = new MultiVoiceOutput(_clips);
            isInitialized = true;
        }

        /// <summary>
        /// Replaces all audio sources at runtime. If already initialized,
        /// tears down existing clips and rebuilds with the new sources.
        /// </summary>
        public void SetAudioSources(List<AudioSource> sources)
        {
            if (isInitialized)
            {
                _output.Stop();
                _clips.Clear();

                _audioSources = sources;

                foreach (AudioSource src in _audioSources)
                {
                    if (!src) continue;
                    InitClipForSource(src);
                }

                _output = new MultiVoiceOutput(_clips);
            }
            else
            {
                _audioSources = sources;
            }
        }

        /// <summary>
        /// Adds a single audio source at runtime.
        /// If not yet initialized, queues it for the next Init call.
        /// </summary>
        public void AddAudioSource(AudioSource source)
        {
            if (!source) return;

            _audioSources.Add(source);

            if (!isInitialized)
                return;

            var clip = InitClipForSource(source);
            _output.AddClip(clip);
        }

        /// <summary>
        /// Removes a single audio source at runtime and cleans up its clip.
        /// </summary>
        public void RemoveAudioSource(AudioSource source)
        {
            if (!source) return;

            int idx = _audioSources.IndexOf(source);
            if (idx < 0) return;

            _audioSources.RemoveAt(idx);

            if (!isInitialized || _clips == null)
                return;

            // Find the clip that uses this audio source
            for (int i = _clips.Count - 1; i >= 0; i--)
            {
                if (_clips[i].source == source)
                {
                    var clip = _clips[i];
                    _output.RemoveClip(clip);
                    _clips.RemoveAt(i);
                    break;
                }
            }
        }

        private StreamedAudioClip InitClipForSource(AudioSource src)
        {
            var clip = new StreamedAudioClip();
            clip.SetAudioSource(src);
            clip.Init(_cachedInput, _cachedDsp, _cachedLevels);
            _clips.Add(clip);

            var reader = EnsurePurrAudioReader(src);
            if (reader != null)
                reader.OnAudioFilter += clip.HandleAudioFilterRead;

            return clip;
        }

        private static PurrAudioReader EnsurePurrAudioReader(AudioSource src)
        {
            if (src.TryGetComponent<PurrAudioReader>(out var reader))
                return reader;

            reader = src.gameObject.AddComponent<PurrAudioReader>();
            return reader;
        }

        /// <summary>
        /// Subscribes the clip to the reader's audio callback so it stays
        /// sample-synchronized with the primary clip.
        /// </summary>
        private void AddCallback(PurrAudioReader reader, StreamedAudioClip clip)
        {
            reader.OnAudioFilter += clip.HandleAudioFilterRead;
        }

        /// <summary>
        /// Propagates a new microphone (or any <see cref="IAudioInputSource"/>)
        /// reference to every existing clip.
        /// </summary>
        public override void SetInput(IAudioInputSource mic)
        {
            _cachedInput = mic;
            foreach (var c in _clips) c.SetInput(mic);
        }

        private void OnDestroy()
        {
            if (_clips == null) return;
            foreach (var clip in _clips)
                clip?.Dispose();
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor-time safety net: makes sure every configured AudioSource has
        /// a <see cref="PurrAudioReader"/> component so sample callbacks work
        /// both in Play mode and Edit mode.
        /// </summary>
        private void OnValidate()
        {
            if (_audioSources == null || _audioSources.Count == 0) return;

            foreach (AudioSource src in _audioSources)
            {
                if (src.TryGetComponent<PurrAudioReader>(out var reader))
                    continue;

                // Add the missing component and move to the top of the GameObject.
                reader = src.gameObject.AddComponent<PurrAudioReader>();
                while (ComponentUtility.MoveComponentUp(reader)) { }
            }
        }
#endif
    }
}
