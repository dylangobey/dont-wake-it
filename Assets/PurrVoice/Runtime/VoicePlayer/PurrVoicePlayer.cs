using System;
using System.Buffers;
using System.Collections.Concurrent;
using JetBrains.Annotations;
using PurrNet.Logging;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Voice
{
    public delegate ArraySegment<float> ProcessSamplesDelegate(ArraySegment<float> input, int frequency, params FilterLevel[] level);

    public partial class PurrVoicePlayer : NetworkIdentity
    {
        [SerializeField, PurrLock] private InputProvider _inputProvider;
        [SerializeField, PurrLock] private OutputProvider _outputProvider;
        [SerializeField, PurrLock] private AudioCodecSettings _codecSettings;

#if PURR_PIPES
        [SerializeField]
#endif
        private NetworkAudioModule _transport = new ();

        /// <summary>
        /// Handles whether this PurrVoicePlayer is muted.
        /// If it's the local one, no audio will be sent.
        /// If it's a remote client, we won't replay their audio
        /// </summary>
        public bool muted;
        [SerializeField, PurrLock] private bool _enableLocalPlayback = false;
        [SerializeField, PurrLock, PurrShowIf("_enableLocalPlayback")]
        private OutputProvider _localOutputProvider;

        public bool usingLocalPlayback => _enableLocalPlayback;

        private IAudioInputSource micDevice => _inputProvider.input;

        public int inputFrequency => micDevice?.frequency ?? -1;
        public IVoiceOutput output => _outputProvider?.output;

        [SerializeField, PurrReadOnly, UsedImplicitly] private string _currentDevice;

        /// <summary>
        /// Whenever you start playing audio from the microphone, this event will be invoked with the samples.
        /// </summary>
        public event Action<ArraySegment<float>> onReceivedSample;

        /// <summary>
        /// When you locally record audio from the microphone, this event will be invoked with the samples.
        /// </summary>
        public event Action<ArraySegment<float>> onLocalSample;

        private bool _initializedController;
        private bool _initializedRemote;
        private bool _outputInitialized;
        private VoiceDebugSettings _debugSettings;

        // Cached filter level array to avoid params allocation per mic callback
        private static readonly FilterLevel[] SenderLevels = { FilterLevel.Sender };

        // Async sender filter processing state
        private struct SenderWorkItem
        {
            public float[] buffer;
            public int count;
            public int frequency;
        }

        private struct SenderResult
        {
            public float[] buffer;
            public int count;
        }

        private struct EncodedSendItem
        {
            public byte[] buffer;
            public int length;
        }

        private ConcurrentQueue<SenderWorkItem> _senderInputQueue;
        private ConcurrentQueue<SenderResult> _senderResultQueue;
        private ConcurrentQueue<EncodedSendItem> _encodedSendQueue;
        private Action _senderFilterCallback;

        private float[] _workerChunkBuffer;
        private byte[] _workerEncodeBuffer;
        private int _workerBufferPos;
        private double _workerResampleCarry;

        private void Awake()
        {
            _senderInputQueue = new ConcurrentQueue<SenderWorkItem>();
            _senderResultQueue = new ConcurrentQueue<SenderResult>();
            _encodedSendQueue = new ConcurrentQueue<EncodedSendItem>();
            _senderFilterCallback = DrainSenderFilterQueue;
        }

        private void InitOutputIfNeeded()
        {
            if (_outputInitialized)
                return;
            _outputInitialized = true;

            if (!_outputProvider)
            {
                PurrLogger.LogError($"Can't initialize PurrVoicePlayer with no output provider!", this);
                return;
            }

            if (_outputProvider is MultiAudioSourceVoiceProvider && GetComponent<AudioSource>() != null)
            {
                PurrLogger.LogError($"When using the MultiAudioSource Provider, PurrVoicePlayer cannot be on the same object as an audio source!", this);
                return;
            }

            _transport.SetCodec(_codecSettings ? _codecSettings.CreateCodec() : null);
            _transport.OnFrequencyChanged += OnFrequencyInitialized;
            _outputProvider.Init(_transport, ProcessSamples, FilterLevel.Receiver);
            if (_outputProvider.output != null)
                _outputProvider.output.onEndPlayingSample += OnReplayingSample;
            FilterAwake();
        }

        private void OnFrequencyInitialized(int freq)
        {
            DebugAwake(freq);
            SetupVisualization(freq);
        }

        protected override void OnOwnerChanged(PlayerID? oldOwner, PlayerID? newOwner, bool asServer)
        {
            base.OnOwnerChanged(oldOwner, newOwner, asServer);

            if (asServer)
                return;

            InitOutputIfNeeded();

            if ((_initializedController || _initializedRemote) && oldOwner.HasValue && oldOwner == localPlayer)
            {
                Deinitialize();
            }

            if (isOwner)
            {
                _inputProvider.Init(this);
                SetupMicrophone();
                AudioDevices.onDevicesChanged += OnDevicesChanged;
            }
            else
            {
                SetupRemotePlayback();
            }
        }

        private void OnEnable()
        {
            if (_initializedController && micDevice != null)
            {
                micDevice.onSampleReady -= OnMicrophoneData;
                micDevice.onSampleReady += OnMicrophoneData;
                AudioDevices.onUpdate -= DrainSenderResults;
                AudioDevices.onUpdate += DrainSenderResults;
            }
        }

        private void OnDisable()
        {
            if (micDevice != null)
            {
                micDevice.onSampleReady -= OnMicrophoneData;
            }

            AudioDevices.onUpdate -= DrainSenderResults;
        }

        protected override void OnDespawned()
        {
            base.OnDespawned();
            Cleanup();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            Cleanup();
        }

        private void OnValidate()
        {
            VisualizerValidate();

#if UNITY_EDITOR
            if (_localOutputProvider == _outputProvider && _outputProvider != null)
            {
                _localOutputProvider = null;
                PurrLogger.LogError($"Only one instance per output! If you want them to be the same, add 2 components of the same type", this);
            }
#endif
        }

        private void Cleanup()
        {
            CleanupVisualization();

            _inputProvider?.Cleanup();
            _localOutputProvider?.output?.Stop();
            output?.Stop();

            _transport.DisposeCodec();

            AudioDevices.onDevicesChanged -= OnDevicesChanged;
            AudioDevices.onUpdate -= DrainSenderResults;
            CleanupSenderQueues();
        }

        private void SetupMicrophone()
        {
            if (_initializedController)
                return;

            _initializedController = true;
            if (micDevice != null)
            {
                _currentDevice = micDevice.ToString();
                micDevice.onSampleReady += OnMicrophoneData;
                AudioDevices.onUpdate += DrainSenderResults;

                if (_enableLocalPlayback)
                {
                    if (!_localOutputProvider)
                    {
                        PurrLogger.LogError($"Can't do local playback without a local output provider defined!", this);
                        return;
                    }
                    _localOutputProvider.Init(micDevice, ProcessSamplesLocal, FilterLevel.Receiver);
                    _localOutputProvider.output.onStartPlayingSample += DebugStreamedAudio;
                    _localOutputProvider.output.onEndPlayingSample += DebugStreamedAudioEnd;
                    _localOutputProvider.output.Start();
                }
                else
                {
                    micDevice.Start();
                }

                int reportedFrequency = micDevice.frequency;
                _debugSettings = GetComponent<VoiceDebugSettings>();
                if (_debugSettings)
                    reportedFrequency = _debugSettings.ResolveFrequency(micDevice.frequency);

                _transport.SetFrequency(reportedFrequency);

                if (_debugSettings)
                    _debugSettings.ReportCodecTargetRate(_transport.frequency);
            }
            else
            {
                PurrLogger.LogError($"No microphone devices found for {name}. Please connect a microphone.", this);
            }
        }

        private void Deinitialize()
        {
            if (micDevice != null)
            {
                micDevice.onSampleReady -= OnMicrophoneData;
                micDevice.Stop();
            }

            AudioDevices.onUpdate -= DrainSenderResults;
            CleanupSenderQueues();

            if(output != null)
                output.Stop();

            _initializedController = false;
            _initializedRemote = false;
        }

        public void ChangeMicrophone(IAudioInputSource mic)
        {
            if (mic == null || !isController)
                return;

            if (micDevice != null)
            {
                micDevice.onSampleReady -= OnMicrophoneData;
                micDevice.Stop();
            }
            _transport.Stop();

            _inputProvider.ChangeInput(mic);
            _currentDevice = micDevice?.ToString();
            _localOutputProvider?.SetInput(mic);
            micDevice?.Start();
            _transport.SetCodec(_codecSettings ? _codecSettings.CreateCodec() : null);

            int reportedFrequency = mic.frequency;
            if (_debugSettings)
                reportedFrequency = _debugSettings.ResolveFrequency(mic.frequency);

            _transport.SetFrequency(reportedFrequency);
            _transport.Start();

            if(micDevice != null)
                micDevice.onSampleReady += OnMicrophoneData;
        }

        private void SetupRemotePlayback()
        {
            if (_initializedRemote)
                return;

            _initializedRemote = true;
            output?.Start();
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            output?.HandleAudioFilterRead(data, channels);
            _localOutputProvider?.output?.HandleAudioFilterRead(data, channels);
        }

        private ArraySegment<float> ProcessSamples(ArraySegment<float> inputSamples, int frequency, params FilterLevel[] levels)
        {
            if (muted)
            {
                for (int i = 0; i < levels.Length; i++)
                {
                    if (levels[i] == FilterLevel.Receiver)
                        return MuteAudio(inputSamples);
                }
            }

            return DoProcessFilters(inputSamples, frequency, levels);
        }

        private ArraySegment<float> ProcessSamplesLocal(ArraySegment<float> inputSamples, int frequency, params FilterLevel[] levels)
        {
            if (muted)
                return MuteAudio(inputSamples);

            return DoLocalProcessFilters(inputSamples, frequency, levels);
        }

        private ArraySegment<float> MuteAudio(ArraySegment<float> inputSamples)
        {
            for (int i = 0; i < inputSamples.Count; i++)
            {
                inputSamples[i] = 0;
            }

            return inputSamples;
        }

        private void OnMicrophoneData(ArraySegment<float> samples)
        {
            if (!this || !enabled || _senderInputQueue == null)
                return;

            if (muted)
                return;

            DebugMicrophoneDataPreProcessing(samples);

            float[] resampleBuffer = null;
            if (_debugSettings && _debugSettings.needsResample)
            {
                samples = _debugSettings.ResampleMicData(samples, null, out int outCount);
                if (outCount > 0)
                    resampleBuffer = samples.Array;
            }

            if (VoiceThreading.IsMultithreadingSupported)
            {
                int count = samples.Count;
                var buffer = ArrayPool<float>.Shared.Rent(count);
                Array.Copy(samples.Array!, samples.Offset, buffer, 0, count);

                if (resampleBuffer != null)
                    _debugSettings.ReturnResampleBuffer(resampleBuffer);

                _senderInputQueue.Enqueue(new SenderWorkItem
                {
                    buffer = buffer,
                    count = count,
                    frequency = micDevice.frequency
                });
                VoiceThreading.QueueWork(_senderFilterCallback);
            }
            else
            {
                samples = DoProcessFilters(samples, micDevice.frequency, SenderLevels);
                onLocalSample?.Invoke(samples);
                _transport.SendAudioChunk(samples);
                DebugMicrophoneDataPostProcessing(samples);

                if (resampleBuffer != null)
                    _debugSettings.ReturnResampleBuffer(resampleBuffer);
            }
        }

        private void DrainSenderFilterQueue()
        {
            if (_senderInputQueue == null || _senderResultQueue == null)
                return;

            while (_senderInputQueue.TryDequeue(out var item))
            {
                var segment = new ArraySegment<float>(item.buffer, 0, item.count);
                segment = DoProcessFilters(segment, item.frequency, SenderLevels);

                var codec = _transport.codec;
                int targetRate = _transport.frequency;
                int micFreq = _transport.micFrequency;
                int chunkSize = _transport.chunkSize;

                if (chunkSize <= 0 || targetRate <= 0)
                {
                    _senderResultQueue.Enqueue(new SenderResult { buffer = item.buffer, count = segment.Count });
                    continue;
                }

                ArraySegment<float> resampled = segment;
                float[] resampleBuf = null;

                if (micFreq > 0 && micFreq != targetRate)
                {
                    double exact = segment.Count * (double)targetRate / micFreq;
                    int outCount = (int)(exact + _workerResampleCarry);
                    _workerResampleCarry += exact - outCount;
                    resampleBuf = ArrayPool<float>.Shared.Rent(outCount);
                    float ratio = micFreq / (float)targetRate;
                    for (int i = 0; i < outCount; i++)
                    {
                        float srcIdx = i * ratio;
                        int s0 = (int)srcIdx;
                        int s1 = Math.Min(s0 + 1, segment.Count - 1);
                        float frac = srcIdx - s0;
                        resampleBuf[i] = segment.Array![segment.Offset + s0] * (1f - frac)
                                       + segment.Array![segment.Offset + s1] * frac;
                    }
                    resampled = new ArraySegment<float>(resampleBuf, 0, outCount);
                }

                if (_workerChunkBuffer == null || _workerChunkBuffer.Length != chunkSize)
                {
                    _workerChunkBuffer = new float[chunkSize];
                    _workerBufferPos = 0;
                }

                if (codec != null && (_workerEncodeBuffer == null || _workerEncodeBuffer.Length < OpusCodec.MaxEncodedBytes))
                    _workerEncodeBuffer = new byte[OpusCodec.MaxEncodedBytes];

                int offset = 0;
                while (offset < resampled.Count)
                {
                    int remaining = chunkSize - _workerBufferPos;
                    int copy = Math.Min(remaining, resampled.Count - offset);
                    if (copy <= 0) break;
                    Array.Copy(resampled.Array!, resampled.Offset + offset, _workerChunkBuffer, _workerBufferPos, copy);
                    _workerBufferPos += copy;
                    offset += copy;

                    if (_workerBufferPos == chunkSize)
                    {
                        if (codec != null)
                        {
                            int encodedLen = codec.Encode(_workerChunkBuffer, 0, chunkSize, _workerEncodeBuffer);
                            var encodedCopy = ArrayPool<byte>.Shared.Rent(encodedLen);
                            Array.Copy(_workerEncodeBuffer, 0, encodedCopy, 0, encodedLen);
                            _encodedSendQueue.Enqueue(new EncodedSendItem { buffer = encodedCopy, length = encodedLen });
                        }
                        else
                        {
                            int rawLen = chunkSize * sizeof(float);
                            var rawCopy = ArrayPool<byte>.Shared.Rent(rawLen);
                            Buffer.BlockCopy(_workerChunkBuffer, 0, rawCopy, 0, rawLen);
                            _encodedSendQueue.Enqueue(new EncodedSendItem { buffer = rawCopy, length = rawLen });
                        }

                        _workerBufferPos = 0;
                    }
                }

                if (resampleBuf != null)
                    ArrayPool<float>.Shared.Return(resampleBuf);

                _senderResultQueue.Enqueue(new SenderResult { buffer = item.buffer, count = segment.Count });
            }
        }

        private void DrainSenderResults()
        {
            if (!this || !enabled || _transport == null)
                return;

            if (_senderResultQueue != null)
            {
                while (_senderResultQueue.TryDequeue(out var result))
                {
                    var segment = new ArraySegment<float>(result.buffer, 0, result.count);
                    onLocalSample?.Invoke(segment);
                    DebugMicrophoneDataPostProcessing(segment);
                    ArrayPool<float>.Shared.Return(result.buffer);
                }
            }

            if (_encodedSendQueue != null)
            {
                while (_encodedSendQueue.TryDequeue(out var item))
                {
                    _transport.SendPreEncoded(item.buffer, 0, item.length);
                    ArrayPool<byte>.Shared.Return(item.buffer);
                }
            }
        }

        private void CleanupSenderQueues()
        {
            if (_senderInputQueue != null)
            {
                while (_senderInputQueue.TryDequeue(out var item))
                    ArrayPool<float>.Shared.Return(item.buffer);
            }

            if (_senderResultQueue != null)
            {
                while (_senderResultQueue.TryDequeue(out var result))
                    ArrayPool<float>.Shared.Return(result.buffer);
            }

            if (_encodedSendQueue != null)
            {
                while (_encodedSendQueue.TryDequeue(out var item))
                    ArrayPool<byte>.Shared.Return(item.buffer);
            }

            _workerChunkBuffer = null;
            _workerEncodeBuffer = null;
            _workerBufferPos = 0;
            _workerResampleCarry = 0d;
        }

        private void OnReplayingSample(ArraySegment<float> obj)
        {
            onReceivedSample?.Invoke(obj);
        }

        private void OnDevicesChanged()
        {
            if (!isOwner) return;
            if (!this || !gameObject) return;

            micDevice?.Stop();
            _localOutputProvider?.output?.Stop();
            SetupMicrophone();
        }
    }
}
