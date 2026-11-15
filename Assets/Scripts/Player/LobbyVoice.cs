using DontWakeIt.Voice;
using PurrNet.Voice;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DontWakeIt.Player
{
    /// <summary>
    /// Voice chat in the lobby, before the game starts. There are no bodies yet, so everyone is heard at the same
    /// volume (2D). Goes on the Lobby Voice prefab next to PurrVoicePlayer, VoiceInputProvider and
    /// MultiAudioSourceVoiceProvider. Tools > Dont Wake It > Voice Setup builds the prefab for you.
    /// </summary>
    public class LobbyVoice : MonoBehaviour
    {
        [SerializeField] private Key muteKey = Key.M;
        [Range(0f, 1f)][SerializeField] private float volume = 1f;

        private PurrVoicePlayer voice;
        private bool userMuted;

        private void Awake()
        {
            voice = GetComponent<PurrVoicePlayer>();
            var provider = GetComponent<MultiAudioSourceVoiceProvider>();
            if (!voice || !provider)
            {
                Debug.LogError("LobbyVoice: needs PurrVoicePlayer and MultiAudioSourceVoiceProvider. " +
                               "Use Tools > Dont Wake It > Voice Setup.", this);
                enabled = false;
                return;
            }

            // The multi-source provider requires the AudioSource to be on a different object from the PurrVoicePlayer.
            var speaker = new GameObject("Lobby Voice Speaker");
            speaker.transform.SetParent(transform, false);
            var source = speaker.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.volume = volume;
            provider.AddAudioSource(source);
        }

        private void Update()
        {
            if (!voice.isOwner) return;

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard[muteKey].wasPressedThisFrame) userMuted = !userMuted;
            voice.muted = userMuted;
        }

        private void OnGUI()
        {
            if (!voice || !voice.isOwner) return;

            string label = !MicrophoneSettings.HasMicrophone ? "NO MICROPHONE"
                : userMuted ? "MIC MUTED  (" + muteKey + " to unmute)"
                : "LOBBY VOICE ON  (" + muteKey + " to mute)";
            GUI.Label(new Rect(12f, Screen.height - 30f, 340f, 22f), label);
        }
    }
}
