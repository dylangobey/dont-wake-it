using System;
using PurrNet.Voice;
using UnityEngine;

namespace DontWakeIt.Voice
{
    /// <summary>
    /// Replacement for PurrVoice's DefaultInputProvider. It uses the microphone chosen in the menu, and copes with
    /// a PC that has no microphone at all (the stock provider throws, which would break the player's spawn).
    /// Put it on the Player prefab next to the PurrVoicePlayer.
    /// </summary>
    public class VoiceInputProvider : InputProvider
    {
        [Tooltip("Switch microphone automatically when one is plugged in or removed.")]
        [SerializeField] private bool reactToDeviceChanges = true;

        private IAudioInputSource mic;

        public override IAudioInputSource input => mic;
        public override event Action onDeviceChanged;

        public override void Init(PurrVoicePlayer purrVoicePlayer)
        {
            mic = MicrophoneSettings.Resolve();
            if (mic == null)
                Debug.LogWarning("No microphone found: you can hear other players, but they won't hear you.", this);
        }

        public override void Cleanup()
        {
            mic?.Stop();
        }

        public override void ChangeInput(IAudioInputSource newInput)
        {
            if (mic == newInput) return;

            Cleanup();
            mic = newInput;
            onDeviceChanged?.Invoke();
        }

        private void OnEnable()
        {
            if (reactToDeviceChanges)
                AudioDevices.onDevicesChanged += OnDevicesChanged;
        }

        private void OnDisable()
        {
            AudioDevices.onDevicesChanged -= OnDevicesChanged;
        }

        private void LateUpdate()
        {
            // Keeps PurrVoice's list of microphones fresh.
            if (Application.platform != RuntimePlatform.WebGLPlayer)
                AudioDevices.QueryDevicesUnity();
        }

        private void OnDevicesChanged()
        {
            if (!this || !gameObject) return;

            // A microphone appeared where there was none (or ours was unplugged): pick again.
            bool unusable = mic == null || (mic is Device device && !AudioDevices.IsValidDevice(device.device));
            if (unusable) mic = MicrophoneSettings.Resolve();

            onDeviceChanged?.Invoke();
        }
    }
}
