using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

using FMODUnity;
using UnityEngine;

namespace PurrNet.Voice.FMODIntegration
{
    /// <summary>
    /// IVoiceOutput implementation that plays received voice audio through FMOD's low-level API
    /// using a programmer sound with a PCM read callback.
    /// </summary>
    public class FMODVoiceOutput : IVoiceOutput
    {
        private static readonly FMOD.SOUND_PCMREAD_CALLBACK StaticPcmReadCallback = StaticPcmRead;

        public event Action<ArraySegment<float>> onStartPlayingSample;
        public event Action<ArraySegment<float>> onEndPlayingSample;

        public int frequency => _inputSource?.frequency ?? -1;

        /// <summary>
        /// FMOD channel priority. 0 = highest priority (never culled), 256 = lowest.
        /// Set this before calling Start() or change it at runtime via the property.
        /// </summary>
        public int channelPriority
        {
            get => _channelPriority;
            set
            {
                _channelPriority = Mathf.Clamp(value, 0, 256);
                if (_channel.hasHandle())
                    _channel.setPriority(_channelPriority);
            }
        }

        public float volume
        {
            get => _volume;
            set
            {
                _volume = Mathf.Clamp01(value);
                if (_channel.hasHandle())
                    _channel.setVolume(_volume);
            }
        }

        public int decodeBufferMs = 100;
        public int preBufferMs = 200;

        public bool spatialize
        {
            get => _spatialize;
            set
            {
                _spatialize = value;
                ApplySpatialSettings();
            }
        }

        public Transform trackingTransform
        {
            get => _trackingTransform;
            set => _trackingTransform = value;
        }

        public float minDistance
        {
            get => _minDistance;
            set
            {
                _minDistance = Mathf.Max(0f, value);
                if (_maxDistance < _minDistance)
                    _maxDistance = _minDistance;
                ApplySpatialSettings();
            }
        }

        public float maxDistance
        {
            get => _maxDistance;
            set
            {
                _maxDistance = Mathf.Max(_minDistance, value);
                ApplySpatialSettings();
            }
        }

        public VoiceRolloffMode rolloffMode
        {
            get => _rolloffMode;
            set
            {
                _rolloffMode = value;
                ApplySpatialSettings();
            }
        }

        private IAudioInputSource _inputSource;
        private ProcessSamplesDelegate _processSamples;
        private FilterLevel[] _levels;

        private FMOD.System _fmodSystem;
        private FMOD.Sound _sound;
        private FMOD.Channel _channel;
        private int _channelPriority;
        private float _volume = 1f;
        private bool _spatialize = true;
        private Transform _trackingTransform;
        private float _minDistance = 5f;
        private float _maxDistance = 55f;
        private VoiceRolloffMode _rolloffMode = VoiceRolloffMode.InverseTapered;

        private int _outputRate;

        private float[] _ringBuffer;
        private int _ringSize;
        private volatile int _ringWritePos;
        private volatile int _ringReadPos;

        private readonly ConcurrentQueue<FilterWorkItem> _filterInputQueue = new();
        private readonly ConcurrentQueue<PendingWrite> _pendingWrites = new();
        private Action _drainFilterQueueCallback;

        private bool _isPlaying;
        private bool _isInitialized;
        private volatile bool _isReady;
        private int _desiredLag;

        private GCHandle _selfHandle;

        private float[] _pcmReadBuffer;

        private struct FilterWorkItem
        {
            public float[] buffer;
            public int count;
            public int outRate;
        }

        private struct PendingWrite
        {
            public float[] buffer;
            public int count;
        }

        public void Init(IAudioInputSource inputSource, ProcessSamplesDelegate processSamples = null,
            params FilterLevel[] levels)
        {
            _inputSource = inputSource;
            _processSamples = processSamples;
            _levels = levels;
            _drainFilterQueueCallback = DrainFilterQueue;

            _fmodSystem = FMODUnity.RuntimeManager.CoreSystem;

            _fmodSystem.getSoftwareFormat(out _outputRate, out _, out _);

            int preBufferSamples = _outputRate * preBufferMs / 1000;
            _ringSize = Math.Max(_outputRate, preBufferSamples * 4);
            _ringBuffer = new float[_ringSize];
            _ringWritePos = 0;
            _ringReadPos = 0;
            _isReady = false;
            _desiredLag = preBufferSamples;

            int decodeBufferSamples = _outputRate * decodeBufferMs / 1000;
            _pcmReadBuffer = new float[decodeBufferSamples];

            _isInitialized = true;

            NetworkManager.main.onTick += OnTick;
        }

        public void Start()
        {
            if (!_isInitialized || _inputSource == null)
                return;

            if (_inputSource.isRecording)
                return;

            if (_inputSource.Start() != StartDeviceResult.Success)
                return;

            _inputSource.onSampleReady += OnSampleReady;
            CreateFMODSound();
        }

        public void Stop()
        {
            if (_inputSource == null || !_inputSource.isRecording)
                return;

            _inputSource.Stop();
            _inputSource.onSampleReady -= OnSampleReady;
            ReleaseFMODSound();

            while (_filterInputQueue.TryDequeue(out var item))
                ArrayPool<float>.Shared.Return(item.buffer);
            while (_pendingWrites.TryDequeue(out var pending))
                ArrayPool<float>.Shared.Return(pending.buffer);

            _isPlaying = false;
            _isReady = false;
        }

        public void SetInput(IAudioInputSource mic)
        {
            if (mic == null)
                return;

            if (_inputSource != null)
            {
                _inputSource.Stop();
                _inputSource.onSampleReady -= OnSampleReady;
            }

            _inputSource = mic;
            _inputSource.onSampleReady += OnSampleReady;
            _inputSource.Start();
        }

        public void HandleAudioFilterRead(float[] data, int channels)
        {
            // Not used for FMOD
        }

        public void Dispose()
        {
            Stop();
            if (_isInitialized)
            {
                NetworkManager.main.onTick -= OnTick;
                _isInitialized = false;
            }
        }

        private void CreateFMODSound()
        {
            var exInfo = new FMOD.CREATESOUNDEXINFO
            {
                cbsize = Marshal.SizeOf<FMOD.CREATESOUNDEXINFO>(),
                numchannels = 1,
                defaultfrequency = _outputRate,
                decodebuffersize = (uint)(_outputRate * decodeBufferMs / 1000),
                length = (uint)(_outputRate * sizeof(float)) * 60,
                format = FMOD.SOUND_FORMAT.PCMFLOAT,
                pcmreadcallback = StaticPcmReadCallback
            };

            var result = _fmodSystem.createSound((string)null,
                FMOD.MODE.OPENUSER | FMOD.MODE.LOOP_NORMAL | FMOD.MODE.CREATESTREAM,
                ref exInfo, out _sound);

            if (result != FMOD.RESULT.OK)
            {
                UnityEngine.Debug.LogError($"[PurrVoice] FMOD createSound failed: {result}");
                return;
            }

            if (!_selfHandle.IsAllocated)
                _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);

            result = _sound.setUserData(GCHandle.ToIntPtr(_selfHandle));
            if (result != FMOD.RESULT.OK)
            {
                UnityEngine.Debug.LogError($"[PurrVoice] FMOD setUserData failed: {result}");
                _sound.release();
                _sound.clearHandle();
                return;
            }

            result = _fmodSystem.playSound(_sound, default, false, out _channel);
            if (result != FMOD.RESULT.OK)
            {
                UnityEngine.Debug.LogError($"[PurrVoice] FMOD playSound failed: {result}");
                _sound.setUserData(IntPtr.Zero);
                _sound.release();
                _sound.clearHandle();
                return;
            }

            _channel.setPriority(_channelPriority);
            _channel.setVolume(_volume);
            ApplySpatialSettings();
            UpdateTracking();
            _isPlaying = true;
        }

        private void ReleaseFMODSound()
        {
            if (_channel.hasHandle())
            {
                _channel.stop();
                _channel.clearHandle();
            }

            if (_sound.hasHandle())
            {
                _sound.setUserData(IntPtr.Zero);
                _sound.release();
                _sound.clearHandle();
            }

            if (_selfHandle.IsAllocated)
                _selfHandle.Free();
        }

        /// <summary>
        /// FMOD PCM read callback — called on the FMOD mixer thread.
        /// Reads samples from the ring buffer into FMOD's output buffer.
        /// </summary>
        [AOT.MonoPInvokeCallback(typeof(FMOD.SOUND_PCMREAD_CALLBACK))]
        private static FMOD.RESULT StaticPcmRead(IntPtr soundPtr, IntPtr data, uint datalen)
        {
            var sound = new FMOD.Sound(soundPtr);
            var result = sound.getUserData(out var userData);
            if (result != FMOD.RESULT.OK || userData == IntPtr.Zero)
            {
                ClearOutputBuffer(data, datalen);
                return FMOD.RESULT.OK;
            }

            var handle = GCHandle.FromIntPtr(userData);
            if (!(handle.Target is FMODVoiceOutput output))
            {
                ClearOutputBuffer(data, datalen);
                return FMOD.RESULT.OK;
            }

            return output.HandlePcmRead(data, datalen);
        }

        private FMOD.RESULT HandlePcmRead(IntPtr data, uint datalen)
        {
            int sampleCount = (int)(datalen / sizeof(float));
            int read = _ringReadPos;
            int write = _ringWritePos;
            int available = (write - read + _ringSize) % _ringSize;

            if (_pcmReadBuffer == null || _pcmReadBuffer.Length < sampleCount)
                _pcmReadBuffer = new float[sampleCount];

            if (!_isReady)
            {
                if (available < _desiredLag)
                {
                    Array.Clear(_pcmReadBuffer, 0, sampleCount);
                    Marshal.Copy(_pcmReadBuffer, 0, data, sampleCount);
                    return FMOD.RESULT.OK;
                }
                _isReady = true;
            }

            int toRead = Math.Min(sampleCount, available);

            if (toRead > 0)
            {
                int firstChunk = Math.Min(toRead, _ringSize - read);
                Array.Copy(_ringBuffer, read, _pcmReadBuffer, 0, firstChunk);

                int secondChunk = toRead - firstChunk;
                if (secondChunk > 0)
                    Array.Copy(_ringBuffer, 0, _pcmReadBuffer, firstChunk, secondChunk);

                _ringReadPos = (read + toRead) % _ringSize;
            }

            if (toRead < sampleCount)
            {
                const int FADE_OUT = 32;
                int fadeLen = Math.Min(FADE_OUT, toRead);
                for (int i = 0; i < fadeLen; i++)
                    _pcmReadBuffer[toRead - fadeLen + i] *= (fadeLen - i) / (float)(fadeLen + 1);

                for (int i = toRead; i < sampleCount; i++)
                    _pcmReadBuffer[i] = 0f;
            }

            Marshal.Copy(_pcmReadBuffer, 0, data, sampleCount);

            return FMOD.RESULT.OK;
        }

        [ThreadStatic] private static float[] _silenceBuffer;

        private static void ClearOutputBuffer(IntPtr data, uint datalen)
        {
            int sampleCount = (int)(datalen / sizeof(float));
            if (sampleCount <= 0)
                return;

            if (_silenceBuffer == null || _silenceBuffer.Length < sampleCount)
                _silenceBuffer = new float[sampleCount];
            else
                Array.Clear(_silenceBuffer, 0, sampleCount);

            Marshal.Copy(_silenceBuffer, 0, data, sampleCount);
        }

        private void OnSampleReady(ArraySegment<float> data)
        {
            if (!_isPlaying) return;

            if (VoiceThreading.IsMultithreadingSupported)
            {
                int count = data.Count;
                var buffer = ArrayPool<float>.Shared.Rent(count);
                Array.Copy(data.Array!, data.Offset, buffer, 0, count);

                _filterInputQueue.Enqueue(new FilterWorkItem
                {
                    buffer = buffer,
                    count = count,
                    outRate = _outputRate
                });
                VoiceThreading.QueueWork(_drainFilterQueueCallback);
            }
            else
            {
                onStartPlayingSample?.Invoke(data);
                if (_processSamples != null)
                    data = _processSamples(data, frequency, _levels);
                WriteToRingBuffer(data, frequency);
                VoicePlaybackMonitor.ReportPlayback(data);
                onEndPlayingSample?.Invoke(data);
            }
        }

        private void DrainFilterQueue()
        {
            int inRate = frequency;
            while (_filterInputQueue.TryDequeue(out var item))
            {
                var segment = new ArraySegment<float>(item.buffer, 0, item.count);
                if (_processSamples != null)
                    segment = _processSamples(segment, inRate, _levels);

                if (inRate > 0 && inRate != item.outRate)
                {
                    int outCount = (int)Math.Ceiling(segment.Count * (double)item.outRate / inRate);
                    var resampled = ArrayPool<float>.Shared.Rent(outCount);
                    float ratio = inRate / (float)item.outRate;
                    var srcArr = segment.Array;
                    int srcOff = segment.Offset;
                    int srcLen = segment.Count;

                    for (int i = 0; i < outCount; i++)
                    {
                        float t = i * ratio;
                        int t0 = (int)t;
                        int t1 = t0 + 1;
                        if (t1 >= srcLen) t1 = srcLen - 1;
                        float frac = t - t0;
                        resampled[i] = srcArr![srcOff + t0] + (srcArr[srcOff + t1] - srcArr[srcOff + t0]) * frac;
                    }

                    ArrayPool<float>.Shared.Return(item.buffer);
                    _pendingWrites.Enqueue(new PendingWrite { buffer = resampled, count = outCount });
                }
                else
                {
                    _pendingWrites.Enqueue(new PendingWrite { buffer = item.buffer, count = segment.Count });
                }
            }
        }

        private void OnTick(bool asServer)
        {
            DrainPendingWrites();

            if (_isReady)
            {
                int write = _ringWritePos;
                int read = _ringReadPos;
                int available = (write - read + _ringSize) % _ringSize;
                int maxLag = _desiredLag * 3;

                if (available > maxLag)
                {
                    _ringReadPos = (write - _desiredLag + _ringSize) % _ringSize;
                }
            }
        }

        public void UpdateTracking()
        {
            if (!_channel.hasHandle() || !_spatialize)
                return;

            var trackedTransform = _trackingTransform;
            Vector3 position = trackedTransform ? trackedTransform.position : Vector3.zero;
            var fmodPosition = position.ToFMODVector();
            var velocity = new FMOD.VECTOR();
            Check(_channel.set3DAttributes(ref fmodPosition, ref velocity), "set3DAttributes");
        }

        private void DrainPendingWrites()
        {
            while (_pendingWrites.TryDequeue(out var pending))
            {
                var segment = new ArraySegment<float>(pending.buffer, 0, pending.count);
                onStartPlayingSample?.Invoke(segment);
                VoicePlaybackMonitor.ReportPlayback(segment);
                WriteToRingBuffer(segment, _outputRate);
                onEndPlayingSample?.Invoke(segment);
                ArrayPool<float>.Shared.Return(pending.buffer);
            }
        }

        private void WriteToRingBuffer(ArraySegment<float> data, int inRate)
        {
            if (inRate > 0 && inRate != _outputRate)
            {
                int outCount = (int)Math.Ceiling(data.Count * (double)_outputRate / inRate);
                var tmp = ArrayPool<float>.Shared.Rent(outCount);
                try
                {
                    float ratio = inRate / (float)_outputRate;
                    var srcArr = data.Array;
                    int srcOff = data.Offset;
                    int srcLen = data.Count;

                    for (int i = 0; i < outCount; i++)
                    {
                        float t = i * ratio;
                        int t0 = (int)t;
                        int t1 = t0 + 1;
                        if (t1 >= srcLen) t1 = srcLen - 1;
                        float frac = t - t0;
                        tmp[i] = srcArr![srcOff + t0] + (srcArr[srcOff + t1] - srcArr[srcOff + t0]) * frac;
                    }

                    WriteRaw(tmp, 0, outCount);
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(tmp);
                }
            }
            else
            {
                WriteRaw(data.Array!, data.Offset, data.Count);
            }
        }

        private void WriteRaw(float[] buffer, int offset, int count)
        {
            if (count <= 0) return;

            int write = _ringWritePos;
            int firstChunk = Math.Min(count, _ringSize - write);
            Array.Copy(buffer, offset, _ringBuffer, write, firstChunk);
            if (count > firstChunk)
                Array.Copy(buffer, offset + firstChunk, _ringBuffer, 0, count - firstChunk);

            System.Threading.Thread.MemoryBarrier();
            _ringWritePos = (write + count) % _ringSize;
        }

        private void ApplySpatialSettings()
        {
            if (!_channel.hasHandle())
                return;

            if (_spatialize)
            {
                Check(_channel.setMode(FMOD.MODE._3D | (FMOD.MODE)_rolloffMode), "setMode(3D)");
                Check(_channel.set3DMinMaxDistance(_minDistance, _maxDistance), "set3DMinMaxDistance");
                return;
            }

            Check(_channel.setMode(FMOD.MODE._2D), "setMode(2D)");
        }

        private static void Check(FMOD.RESULT result, string operation)
        {
            if (result == FMOD.RESULT.OK)
                return;

            Debug.LogWarning($"[PurrVoice] FMOD {operation} failed: {result}");
        }
    }
}
