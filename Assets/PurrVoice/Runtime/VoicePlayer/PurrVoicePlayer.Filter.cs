using System;
using System.Collections.Generic;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Voice
{
    public partial class PurrVoicePlayer
    {
        [SerializeField] private SyncFilters _audioFilters = new();
        [SerializeField, Range(0f, 10f)] private float _amplification = 1f;
        [Tooltip("When enabled and supported (not WebGL), runs filter processing on a worker thread to reduce main-thread CPU usage.")]
        [SerializeField] private bool _useThreadedFilterProcessing = true;
        
        public List<SyncFilters.Filter> audioFilters => _audioFilters.ToList();
        private SyncFilters _localFilters = new();

        /// <summary>
        /// When true and the platform supports it (not WebGL), filter processing runs on a worker thread.
        /// Defaults to true. Set to false to force main-thread processing (e.g. for debugging).
        /// </summary>
        public bool useThreadedFilterProcessing { get => _useThreadedFilterProcessing; set => _useThreadedFilterProcessing = value; }

        /// <summary>
        /// Whether the current platform supports multithreading for filter processing.
        /// False on WebGL; true on standalone, mobile, and consoles.
        /// </summary>
        public static bool supportsMultithreading => VoiceThreading.IsMultithreadingSupported;
        
        /// <summary>
        /// This adds a filter to the audio processing chain.
        /// </summary>
        /// <param name="filter">Filter to add</param>
        /// <param name="level">Level at which the filter should be processed</param>
        /// <param name="initialStrength">The strength of the filter that gets setup. Only at this point can you sync the strength</param>
        public void AddFilter(PurrAudioFilter filter, FilterLevel level, float initialStrength = 1)
        {
            _audioFilters.AddFilter(filter, level, initialStrength);
            _localFilters.AddFilterLocal(filter, level, initialStrength);
        }

        /// <summary>
        /// Removed a filter from the audio processing chain.
        /// </summary>
        /// <param name="index">Index at which to remove said filter</param>
        public void RemoveFilter(int index)
        {
            _audioFilters.RemoveFilter(index);
            _localFilters.RemoveFilterLocal(index);
        }

        /// <summary>
        /// This removes a filter from the audio processing chain.
        /// </summary>
        /// <param name="filter">The filter you wish to remove. If multiple, it'll remove the first found</param>
        public void RemoveFilter(PurrAudioFilter filter)
        {
            int filterIndex = -1;
            for (int i = 0; i < _audioFilters.Count; i++)
            {
                if (_audioFilters[i].audioFilter == filter)
                {
                    filterIndex = i;
                    break;
                }
            }

            if (filterIndex >= 0)
                RemoveFilter(filterIndex);
            else
                PurrLogger.LogError($"Filter {filter.name} not found in the audio filters list.");
        }
        
        /// <summary>
        /// Sets the strength of a filter at a specific index. This only happens locally, so you need to sync it manually if you want it to be reflected on other clients.
        /// </summary>
        /// <param name="index">Index of filter</param>
        /// <param name="strength">Strength to set</param>
        public void SetFilterStrength(int index, float strength)
        {
            if (index < 0 || index >= _audioFilters.Count)
            {
                PurrLogger.LogError($"Invalid filter index: {index}. Cannot set strength.");
                return;
            }

            var filter = _audioFilters[index];
            filter.strength = strength;
            _audioFilters[index] = filter;

            if (index < _localFilters.Count)
            {
                var localFilter = _localFilters[index];
                localFilter.strength = strength;
                _localFilters[index] = localFilter;
            }
            else if (!_outputInitialized)
            {
                PurrLogger.LogWarning("SetFilterStrength was called before filters were fully initialized. The strength will apply once initialization completes.");
            }
            else
            {
                PurrLogger.LogError($"Local filter index {index} is out of range. Local filters count: {_localFilters.Count}.");
            }
        }
        
        /// <summary>
        /// Sets the strength of a specific filter. This only happens locally, so you need to sync it manually if you want it to be reflected on other clients.
        /// </summary>
        /// <param name="filter">Filter you want to change the strength of</param>
        /// <param name="strength">Strength to set</param>
        public void SetFilterStrength(PurrAudioFilter filter, float strength)
        {
            int filterIndex = -1;
            for (int i = 0; i < _audioFilters.Count; i++)
            {
                if (_audioFilters[i].audioFilter == filter)
                {
                    filterIndex = i;
                    break;
                }
            }

            if (filterIndex >= 0)
                SetFilterStrength(filterIndex, strength);
            else
                PurrLogger.LogError($"Filter {filter.name} not found in the audio filters list.");
        }
        
        private void FilterAwake()
        {
            _localFilters.Clear();
            _localFilters.AddRange(_audioFilters);
            
            for (var i = 0; i < _audioFilters.Count; i++)
            {
                var f = _audioFilters[i];
                f.Init();
                _audioFilters[i] = f;
            }
        }

        private ArraySegment<float> DoProcessFilters(ArraySegment<float> inputSamples, int frequency, params FilterLevel[] levels)
        {
            return ProcessFilters(_audioFilters, inputSamples, frequency, levels);
        }

        private ArraySegment<float> DoLocalProcessFilters(ArraySegment<float> inputSamples, int frequency, params FilterLevel[] levels)
        {
            return ProcessFilters(_localFilters, inputSamples, frequency, levels);
        }
        
        private ArraySegment<float> ProcessFilters(SyncFilters filters, ArraySegment<float> inputSamples, int frequency, params FilterLevel[] levels)
        {
            if (_amplification != 1f && inputSamples.Array != null)
            {
                float[] arr = inputSamples.Array;
                int off = inputSamples.Offset;
                int count = inputSamples.Count;
                float gain = _amplification;

                for (int i = 0; i < count; i++)
                    arr[off + i] = Math.Clamp(arr[off + i] * gain, -1f, 1f);
            }

            if (filters.Count == 0)
                return inputSamples;

            for (var i = 0; i < levels.Length; i++)
            {
                var level = levels[i];
                for (var x = 0; x < filters.Count; x++)
                {
                    var filter = filters[x];
                    filter.Process(inputSamples, frequency, level);
                }
            }

            return inputSamples;
        }
    }
}
