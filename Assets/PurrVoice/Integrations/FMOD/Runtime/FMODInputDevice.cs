using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace PurrNet.Voice.FMODIntegration
{
    /// <summary>
    /// IAudioInputSource implementation that captures microphone audio through FMOD's recording API.
    /// Use this instead of the default Device class when Unity's audio system is disabled.
    /// </summary>
    public class FMODInputDevice : IAudioInputSource
    {
        public int frequency => _sampleRate;
        public bool isRecording => _isRecording;
        public event Action<ArraySegment<float>> onSampleReady;

        private readonly int _driverId;
        private readonly string _driverName;

        private FMOD.System _fmodSystem;
        private FMOD.Sound _recordSound;

        private int _sampleRate;
        private bool _isRecording;
        private uint _lastReadPos;

        private float[] _readBuffer;

        private uint _recordLengthSamples;

        public FMODInputDevice(int driverId, string driverName, int sampleRate)
        {
            _driverId = driverId;
            _driverName = driverName;
            _sampleRate = sampleRate;
            _readBuffer = new float[sampleRate / 50 * 3];
        }

        public override string ToString()
        {
            return _driverName;
        }

        public StartDeviceResult Start()
        {
            if (_isRecording)
                return StartDeviceResult.AlreadyRecording;

            _fmodSystem = FMODUnity.RuntimeManager.CoreSystem;

            _recordLengthSamples = (uint)_sampleRate;

            var exInfo = new FMOD.CREATESOUNDEXINFO
            {
                cbsize = Marshal.SizeOf<FMOD.CREATESOUNDEXINFO>(),
                numchannels = 1,
                defaultfrequency = _sampleRate,
                format = FMOD.SOUND_FORMAT.PCMFLOAT,
                length = _recordLengthSamples * sizeof(float)
            };

            var result = _fmodSystem.createSound((string)null,
                FMOD.MODE.OPENUSER | FMOD.MODE.LOOP_NORMAL,
                ref exInfo, out _recordSound);

            if (result != FMOD.RESULT.OK)
            {
                Debug.LogError($"[PurrVoice] FMOD createSound for recording failed: {result}");
                return StartDeviceResult.DeviceNotFound;
            }

            result = _fmodSystem.recordStart(_driverId, _recordSound, true);
            if (result != FMOD.RESULT.OK)
            {
                Debug.LogError($"[PurrVoice] FMOD recordStart failed: {result}");
                _recordSound.release();
                return StartDeviceResult.DeviceNotFound;
            }

            _lastReadPos = 0;
            _isRecording = true;

            AudioDevices.onUpdate += Update;

            return StartDeviceResult.Success;
        }

        public void Stop()
        {
            if (!_isRecording)
                return;

            _isRecording = false;
            AudioDevices.onUpdate -= Update;

            _fmodSystem.recordStop(_driverId);

            if (_recordSound.hasHandle())
            {
                _recordSound.release();
                _recordSound.clearHandle();
            }
        }

        private void Update()
        {
            if (!_isRecording)
                return;

            var result = _fmodSystem.getRecordPosition(_driverId, out uint currentPos);
            if (result != FMOD.RESULT.OK)
                return;

            uint samplesToRead;
            if (currentPos >= _lastReadPos)
            {
                samplesToRead = currentPos - _lastReadPos;
            }
            else
            {
                samplesToRead = (_recordLengthSamples - _lastReadPos) + currentPos;
            }

            if (samplesToRead == 0)
                return;

            if (_readBuffer.Length < samplesToRead)
                _readBuffer = new float[samplesToRead];

            uint byteOffset = _lastReadPos * sizeof(float);
            uint byteLength = samplesToRead * sizeof(float);

            result = _recordSound.@lock(byteOffset, byteLength, out IntPtr ptr1, out IntPtr ptr2, out uint len1, out uint len2);
            if (result != FMOD.RESULT.OK)
                return;

            int samples1 = (int)(len1 / sizeof(float));
            if (samples1 > 0)
                Marshal.Copy(ptr1, _readBuffer, 0, samples1);

            int samples2 = (int)(len2 / sizeof(float));
            if (samples2 > 0)
                Marshal.Copy(ptr2, _readBuffer, samples1, samples2);

            _recordSound.unlock(ptr1, ptr2, len1, len2);

            int totalSamples = samples1 + samples2;
            if (totalSamples > 0)
                onSampleReady?.Invoke(new ArraySegment<float>(_readBuffer, 0, totalSamples));

            _lastReadPos = currentPos;
        }
    }
}
