using System;
using System.Collections.Generic;
using UnityEngine;

namespace PurrNet.Voice.FMODIntegration
{
    /// <summary>
    /// Input provider that captures microphone audio through FMOD's recording API.
    /// Use this instead of DefaultInputProvider when Unity's audio system is disabled.
    /// </summary>
    public class FMODInputProvider : InputProvider
    {
        [Tooltip("Which FMOD recording driver to use. 0 = system default.")]
        [SerializeField] private int _driverIndex;

        private FMODInputDevice _device;
        private readonly List<FMODInputDevice> _availableDevices = new();

        public override IAudioInputSource input => _device;
        public override event Action onDeviceChanged;

        /// <summary>
        /// All available FMOD recording devices.
        /// </summary>
        public IReadOnlyList<FMODInputDevice> availableDevices => _availableDevices;

        public override void Init(PurrVoicePlayer purrVoicePlayer)
        {
            RefreshDevices();

            if (_availableDevices.Count > 0)
            {
                int idx = Mathf.Clamp(_driverIndex, 0, _availableDevices.Count - 1);
                _device = _availableDevices[idx];
            }
            else
            {
                Debug.LogWarning("[PurrVoice] No FMOD recording devices found.");
            }
        }

        public override void Cleanup()
        {
            _device?.Stop();
        }

        public override void ChangeInput(IAudioInputSource newInput)
        {
            if (_device == newInput) return;

            Cleanup();

            if (newInput is FMODInputDevice fmodDevice)
            {
                _device = fmodDevice;
                onDeviceChanged?.Invoke();
            }
        }

        /// <summary>
        /// Enumerates all FMOD recording drivers and populates the available devices list.
        /// </summary>
        public void RefreshDevices()
        {
            _availableDevices.Clear();

            var system = FMODUnity.RuntimeManager.CoreSystem;
            var result = system.getRecordNumDrivers(out int numDrivers, out _);

            if (result != FMOD.RESULT.OK)
            {
                Debug.LogWarning($"[PurrVoice] FMOD getRecordNumDrivers failed: {result}");
                return;
            }

            for (int i = 0; i < numDrivers; i++)
            {
                result = system.getRecordDriverInfo(i, out string name, 256,
                    out _, out int sampleRate, out _, out _, out _);

                if (result == FMOD.RESULT.OK)
                {
                    _availableDevices.Add(new FMODInputDevice(i, name, sampleRate));
                }
            }
        }
    }
}
