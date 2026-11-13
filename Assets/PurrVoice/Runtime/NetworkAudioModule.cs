using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using PurrNet.Transports;
using UnityEngine;

#if PURR_PIPES
using PurrNet.Pipes;
#endif

namespace PurrNet.Voice
{
    [Serializable]
    public class NetworkAudioModule : NetworkModule, IAudioInputSource
    {
#if PURR_PIPES
        [SerializeField] private NetworkPipe _pipe;
#endif
        private float[] _chunkBuffer;
        private float[] _decodeBuffer;
        private byte[] _encodeBuffer;
        private byte[] _rawSendBuffer;
        private float[] _rawReceiveBuffer;
        private int _rawReceivePos;
        private int _chunkSize;
        private int _bufferPos;
        private readonly SyncVar<int> _frequency = new(-1, ownerAuth:true);
        private IAudioCodec _codec;

        private int _micFrequency;
        private double _resampleCarry;
        private readonly HashSet<PlayerID> _observerCache = new();

        public IAudioCodec codec => _codec;
        public int chunkSize => _chunkSize;
        public int micFrequency => _micFrequency;

        public event Action<int> OnFrequencyChanged
        {
            add => _frequency.onChanged += value;
            remove => _frequency.onChanged -= value;
        }

        public int frequency => _frequency;

        public bool isRecording { get; private set; }

        public event Action<ArraySegment<float>> onSampleReady;

        private const int MAX_CHUNK_SIZE_BYTES = 900;


        public NetworkAudioModule()
        {
            _frequency.onChanged += OnFrequencySet;
        }

        public void SetCodec(IAudioCodec codec)
        {
            _codec?.Dispose();
            _codec = codec;

            if (_frequency.value > 0)
                OnFrequencySet(_frequency.value);
        }

#if PURR_PIPES
        public override void OnEarlySpawn()
        {
            if (_pipe)
                _pipe.onDataReceivedUntyped += OnPipeData;
        }

        public override void OnDespawned()
        {
            if (_pipe)
                _pipe.onDataReceivedUntyped -= OnPipeData;
        }
#endif

        private void OnFrequencySet(int newFreq)
        {
            if (_codec != null && _codec.TargetSampleRate != newFreq)
            {
                Debug.LogWarning($"[PurrVoice] Codec target rate ({_codec.TargetSampleRate}) does not match network frequency ({newFreq}). " +
                                 $"Recreating codec at correct rate.");

                _codec.Dispose();
                int frameSize = newFreq / 50;
                _codec = new OpusCodec(newFreq, 1, frameSize);
                Debug.Log($"[PurrVoice] Recreated decoder codec at {newFreq} Hz, frame size {frameSize}.");
            }

            _chunkSize = newFreq / 50;
            _chunkBuffer = new float[_chunkSize];
            _decodeBuffer = new float[_chunkSize];

            if (_codec != null)
            {
                _encodeBuffer = new byte[OpusCodec.MaxEncodedBytes];
            }
            else
            {
                _rawSendBuffer = new byte[_chunkSize * sizeof(float)];
                _rawReceiveBuffer = new float[_chunkSize];
                _rawReceivePos = 0;
            }

            _bufferPos = 0;
        }

        public void SetFrequency(int micFrequency)
        {
            if (!isController)
            {
                Debug.LogError($"Only the controller can set the frequency. Current controller: {owner}, current player: {localPlayer}");
                return;
            }

            _micFrequency = micFrequency;
            _resampleCarry = 0d;

            if (_codec != null)
                _frequency.value = _codec.TargetSampleRate;
            else
                _frequency.value = micFrequency;
        }

        public void SendAudioChunk(ArraySegment<float> segment)
        {
            if (!isOwner || _frequency.value < 0 || _chunkBuffer.Length <= 0) return;
            if (_codec != null && _codec.TargetSampleRate <= 0) return;

            int targetRate = _frequency.value;

            if (_micFrequency > 0 && _micFrequency != targetRate)
            {
                int outCount = (int)((segment.Count * (double)targetRate / _micFrequency) + _resampleCarry);
                _resampleCarry += (segment.Count * (double)targetRate / _micFrequency) - outCount;

                var resampled = ArrayPool<float>.Shared.Rent(outCount);
                try
                {
                    float ratio = _micFrequency / (float)targetRate;
                    for (int i = 0; i < outCount; i++)
                    {
                        float srcIdx = i * ratio;
                        int s0 = (int)srcIdx;
                        int s1 = Math.Min(s0 + 1, segment.Count - 1);
                        float frac = srcIdx - s0;
                        resampled[i] = segment.Array![segment.Offset + s0] * (1f - frac)
                                     + segment.Array![segment.Offset + s1] * frac;
                    }

                    SendAudioChunkInternal(new ArraySegment<float>(resampled, 0, outCount));
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(resampled);
                }
            }
            else
            {
                SendAudioChunkInternal(segment);
            }
        }

        private void SendAudioChunkInternal(ArraySegment<float> segment)
        {
            int offset = 0;
            while (offset < segment.Count)
            {
                int remaining = _chunkSize - _bufferPos;
                int copy = Math.Min(remaining, segment.Count - offset);
                if (copy <= 0) break;
                Array.Copy(segment.Array!, segment.Offset + offset, _chunkBuffer, _bufferPos, copy);
                _bufferPos += copy;
                offset += copy;

                if (_bufferPos == _chunkSize)
                {
                    (parent as PurrVoicePlayer)?.DebugNetworkSentData(_chunkBuffer);

                    if (_codec != null)
                    {
                        int encodedLen = _codec.Encode(_chunkBuffer, 0, _chunkSize, _encodeBuffer);

                        int encodedOffset = 0;
                        while (encodedOffset < encodedLen)
                        {
                            int chunkLen = Math.Min(MAX_CHUNK_SIZE_BYTES, encodedLen - encodedOffset);
                            var data = new ByteData(_encodeBuffer, encodedOffset, chunkLen);
#if PURR_PIPES
                            if (_pipe)
                                _pipe.Broadcast(data, Channel.Unreliable);
                            else RpcSendAudio(data);
#else
                            RpcSendAudio(data);
#endif
                            encodedOffset += chunkLen;
                        }
                    }
                    else
                    {
                        Buffer.BlockCopy(_chunkBuffer, 0, _rawSendBuffer, 0, _chunkSize * sizeof(float));

                        int rawOffset = 0;
                        int rawLen = _chunkSize * sizeof(float);
                        while (rawOffset < rawLen)
                        {
                            int chunkLen = Math.Min(MAX_CHUNK_SIZE_BYTES, rawLen - rawOffset);
                            var data = new ByteData(_rawSendBuffer, rawOffset, chunkLen);
#if PURR_PIPES
                            if (_pipe)
                                _pipe.Broadcast(data, Channel.Unreliable);
                            else RpcSendAudio(data);
#else
                            RpcSendAudio(data);
#endif
                            rawOffset += chunkLen;
                        }
                    }

                    _bufferPos = 0;
                }
            }
        }

        public void SendPreEncoded(byte[] data, int offset, int length)
        {
            if (!isOwner || length <= 0) return;

            bool needsCopy = length > MAX_CHUNK_SIZE_BYTES;

            int encodedOffset = offset;
            int end = offset + length;
            while (encodedOffset < end)
            {
                int chunkLen = Math.Min(MAX_CHUNK_SIZE_BYTES, end - encodedOffset);

                ByteData byteData;
                if (needsCopy)
                {
                    var chunk = new byte[chunkLen];
                    Buffer.BlockCopy(data, encodedOffset, chunk, 0, chunkLen);
                    byteData = new ByteData(chunk, 0, chunkLen);
                }
                else
                {
                    byteData = new ByteData(data, encodedOffset, chunkLen);
                }

#if PURR_PIPES
                if (_pipe)
                    _pipe.Broadcast(byteData, Channel.Unreliable);
                else RpcSendAudio(byteData);
#else
                RpcSendAudio(byteData);
#endif
                encodedOffset += chunkLen;
            }
        }

#if PURR_PIPES
        private void OnPipeData(PlayerID player, ByteData encoded)
        {
            ReceiveAudioEncoded(encoded);
        }
#endif

        [ServerRpc(channel: Channel.Unreliable)]
        private void RpcSendAudio(ByteData encoded)
        {
            if (encoded.data == null || encoded.length <= 0)
                return;

            var observers = parent.observers;
            _observerCache.Clear();
            for (int i = 0; i < observers.Count; i++)
                _observerCache.Add(observers[i]);

            for (var i = 0; i < networkManager.players.Count; i++)
            {
                var player = networkManager.players[i];
                if (!_observerCache.Contains(player)) continue;
                if (owner == player) continue;

                if (player == localPlayer)
                {
                    if (_codec != null || _decodeBuffer != null)
                        ReceiveAudioEncoded(encoded);
                    continue;
                }

                TargetReceiveAudio(player, encoded);
            }
        }

        [TargetRpc(channel: Channel.Unreliable)]
        private void TargetReceiveAudio(PlayerID player, ByteData encoded)
        {
            ReceiveAudioEncoded(encoded);
        }

        private bool _loggedNullCodecWarning;

        private void ReceiveAudioEncoded(ByteData encoded)
        {
            if (_decodeBuffer == null || encoded.data == null || encoded.length <= 0)
                return;

            if (_codec != null)
            {
                int sampleCount = _codec.Decode(encoded.data, encoded.offset, encoded.length, _decodeBuffer);
                var segment = new ArraySegment<float>(_decodeBuffer, 0, sampleCount);
                (parent as PurrVoicePlayer)?.DebugReceived(segment);
                onSampleReady?.Invoke(segment);
                return;
            }

            if (!_loggedNullCodecWarning && _frequency.value > 0)
            {
                _loggedNullCodecWarning = true;
                Debug.Log($"[PurrVoice] Receiving raw PCM audio (no codec). Frequency: {_frequency.value}");
            }

            int floatCount = encoded.length / sizeof(float);
            if (_rawReceiveBuffer == null || _chunkSize <= 0)
            {
                if (floatCount > _decodeBuffer.Length)
                    floatCount = _decodeBuffer.Length;
                Buffer.BlockCopy(encoded.data, encoded.offset, _decodeBuffer, 0, floatCount * sizeof(float));
                var segment = new ArraySegment<float>(_decodeBuffer, 0, floatCount);
                (parent as PurrVoicePlayer)?.DebugReceived(segment);
                onSampleReady?.Invoke(segment);
                return;
            }

            int srcOffset = encoded.offset;
            int remaining = floatCount;

            while (remaining > 0)
            {
                int space = _chunkSize - _rawReceivePos;
                int copy = Math.Min(space, remaining);
                Buffer.BlockCopy(encoded.data, srcOffset, _rawReceiveBuffer, _rawReceivePos * sizeof(float), copy * sizeof(float));
                _rawReceivePos += copy;
                srcOffset += copy * sizeof(float);
                remaining -= copy;

                if (_rawReceivePos >= _chunkSize)
                {
                    var segment = new ArraySegment<float>(_rawReceiveBuffer, 0, _chunkSize);
                    (parent as PurrVoicePlayer)?.DebugReceived(segment);
                    onSampleReady?.Invoke(segment);
                    _rawReceivePos = 0;
                }
            }
        }

        public StartDeviceResult Start()
        {
            isRecording = true;
            return StartDeviceResult.Success;
        }

        public void Stop()
        {
            isRecording = false;
        }

        public void DisposeCodec()
        {
            _codec?.Dispose();
            _codec = null;
        }
    }
}
