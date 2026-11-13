using System;
using System.Buffers;
using System.Collections.Concurrent;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Voice
{
    public class StreamedAudioClip : IVoiceOutput
    {
        public AudioSource source;

        public event Action<ArraySegment<float>> onStartPlayingSample;
        public event Action<ArraySegment<float>> onEndPlayingSample;

        private ProcessSamplesDelegate _processSamples;
        private FilterLevel[] _levels;

        public float preBufferSeconds = 0.2f;
        public float bufferCapacitySeconds = 1f;
        public IAudioInputSource inputSource;

        public int frequency => inputSource?.frequency ?? -1;

        private bool _isReady;
        private bool _shouldPlay;
        private bool _setupAudioPending;
        private bool _loggedMissingSource;

        private AudioClip _streamClip;
        private bool _audioSetup;

        private int _clipLen;
        private int _writeHead;
        private int _desiredLag;

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

        private readonly ConcurrentQueue<FilterWorkItem> _filterInputQueue = new ConcurrentQueue<FilterWorkItem>();
        private readonly ConcurrentQueue<PendingWrite> _pendingWrites = new ConcurrentQueue<PendingWrite>();
        private Action _drainFilterQueueCallback;

        public void Init(IAudioInputSource inputSource, ProcessSamplesDelegate processSamples = null, params FilterLevel[] levels)
        {
            this.inputSource = inputSource;
            _processSamples = processSamples;
            _levels = levels;
            _drainFilterQueueCallback = DrainFilterQueue;
            NetworkManager.main.onTick += OnTick;
        }

        public void SetAudioSource(AudioSource source)
        {
            this.source = source;

            if (source != null && _setupAudioPending)
            {
                _setupAudioPending = false;
                source.loop = true;
                source.playOnAwake = false;

                while (_filterInputQueue.TryDequeue(out var item))
                    ArrayPool<float>.Shared.Return(item.buffer);
                while (_pendingWrites.TryDequeue(out var pending))
                    ArrayPool<float>.Shared.Return(pending.buffer);
            }
        }

        public void SetInput(IAudioInputSource mic)
        {
            if (mic == null)
                return;

            if (inputSource != null)
            {
                inputSource.Stop();
                inputSource.onSampleReady -= OnSampleReady;
            }

            inputSource = mic;
            inputSource.onSampleReady += OnSampleReady;
            inputSource.Start();
        }

        public void Start()
        {
            if (inputSource == null || inputSource.isRecording)
                return;

            if (inputSource.Start() != StartDeviceResult.Success)
                return;

            SetupAudio();
        }

        public void SetupAudio()
        {
            inputSource.onSampleReady += OnSampleReady;

            if (source != null)
            {
                source.loop = true;
                source.playOnAwake = false;
            }
            else
            {
                _setupAudioPending = true;
            }

            _isReady = false;
            _audioSetup = false;
        }

        private void EnsureAudioClipCreated()
        {
            if (_audioSetup || frequency <= 0 || source == null) return;
            int sr = AudioSettings.outputSampleRate;
            _clipLen = Math.Max(sr, (int)(sr * bufferCapacitySeconds));
            _streamClip = AudioClip.Create("StreamedVoice", _clipLen, 1, sr, false);
            source.clip = _streamClip;
            AudioSettings.GetDSPBufferSize(out int dsp, out int num);
            _desiredLag = (int)Math.Ceiling(preBufferSeconds * sr) + (dsp * num);
            _writeHead = 0;
            _audioSetup = true;
        }

        public void Stop()
        {
            if (inputSource == null || !inputSource.isRecording)
                return;

            inputSource.Stop();
            StopAudio();
        }

        public void Dispose()
        {
            Stop();

            if (NetworkManager.main)
                NetworkManager.main.onTick -= OnTick;
        }

        public void StopAudio()
        {
            inputSource.onSampleReady -= OnSampleReady;
            if (source && source.isPlaying) source.Stop();

            while (_filterInputQueue.TryDequeue(out var item))
                ArrayPool<float>.Shared.Return(item.buffer);
            while (_pendingWrites.TryDequeue(out var pending))
                ArrayPool<float>.Shared.Return(pending.buffer);

            _audioSetup = false;
            _isReady = false;
        }

        public void HandleAudioFilterRead(float[] data, int channels)
        {
        }

        private void OnSampleReady(ArraySegment<float> data)
        {
            EnsureAudioClipCreated();

            if (VoiceThreading.IsMultithreadingSupported)
            {
                int count = data.Count;
                int outRate = AudioSettings.outputSampleRate;
                var buffer = ArrayPool<float>.Shared.Rent(count);
                Array.Copy(data.Array!, data.Offset, buffer, 0, count);

                _filterInputQueue.Enqueue(new FilterWorkItem
                {
                    buffer = buffer,
                    count = count,
                    outRate = outRate
                });
                VoiceThreading.QueueWork(_drainFilterQueueCallback);
            }
            else
            {
                onStartPlayingSample?.Invoke(data);
                if (_processSamples != null) data = _processSamples(data, frequency, _levels);
                WriteAudioData(data, frequency);
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

        private void DrainPendingWrites()
        {
            while (_pendingWrites.TryDequeue(out var pending))
            {
                var segment = new ArraySegment<float>(pending.buffer, 0, pending.count);
                onStartPlayingSample?.Invoke(segment);
                VoicePlaybackMonitor.ReportPlayback(segment);
                WriteFromBuffer(pending.buffer, 0, pending.count);
                onEndPlayingSample?.Invoke(segment);
                ArrayPool<float>.Shared.Return(pending.buffer);
            }
        }

        private void WriteAudioData(ArraySegment<float> data, int inRate)
        {
            int outRate = AudioSettings.outputSampleRate;
            if (inRate == outRate)
            {
                WriteSamplesDirect(data);
            }
            else
            {
                int outCount = (int)Math.Ceiling(data.Count * (double)outRate / inRate);
                var tmp = ArrayPool<float>.Shared.Rent(outCount);
                try
                {
                    float ratio = inRate / (float)outRate;
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
                    WriteFromBuffer(tmp, 0, outCount);
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(tmp);
                }
            }
        }

        public void OnTick(bool asServer)
        {
            if (!_audioSetup || _streamClip == null || source == null)
            {
                if (!_loggedMissingSource && !_pendingWrites.IsEmpty)
                {
                    _loggedMissingSource = true;
                    PurrLogger.LogError($"StreamedAudioClip is receiving audio but cannot play it. AudioSource: {(source ? source.name : "null")} | AudioSetup: {_audioSetup} | StreamClip: {(_streamClip != null ? "ok" : "null")} | Frequency: {frequency}");
                }
                return;
            }

            DrainPendingWrites();

            int ahead = (_writeHead - source.timeSamples + _clipLen) % _clipLen;
            if (ahead < _desiredLag) WriteZeros(_desiredLag - ahead);

            if (!_isReady && ahead >= _desiredLag)
            {
                int startPos = (_writeHead - _desiredLag + _clipLen) % _clipLen;
                source.timeSamples = startPos;
                source.Play();
                _isReady = true;
            }

            if (_shouldPlay)
            {
                _shouldPlay = false;
                if (!source.isPlaying) source.Play();
            }
        }

        private void WriteZeros(int count)
        {
            var exact = new float[count];
            _streamClip.SetData(exact, _writeHead);
            _writeHead = (_writeHead + count) % _clipLen;
        }

        private void WriteSamplesDirect(ArraySegment<float> data)
        {
            var exact = new float[data.Count];
            Array.Copy(data.Array!, data.Offset, exact, 0, data.Count);
            _streamClip.SetData(exact, _writeHead);
            _writeHead = (_writeHead + data.Count) % _clipLen;
        }

        private void WriteFromBuffer(float[] buffer, int srcOffset, int count)
        {
            var exact = new float[count];
            Array.Copy(buffer, srcOffset, exact, 0, count);
            _streamClip.SetData(exact, _writeHead);
            _writeHead = (_writeHead + count) % _clipLen;
        }
    }
}
