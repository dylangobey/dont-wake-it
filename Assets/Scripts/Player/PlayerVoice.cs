using DontWakeIt.Networking;
using DontWakeIt.Voice;
using PurrNet.Voice;
using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DontWakeIt.Player
{
    [DefaultExecutionOrder(-50)]
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public class PlayerVoice : MonoBehaviour
    {
        public enum TalkMode
        {
            AlwaysOn,
            PushToTalk
        }

        public static PlayerVoice Local { get; private set; }

        [Header("Talking")]
        [SerializeField] private TalkMode talkMode = TalkMode.AlwaysOn;
        [SerializeField] private Key muteKey = Key.M;
        [SerializeField] private Key pushToTalkKey = Key.V;

        [Header("Hearing othert players")]
        [Range(0f, 1f)][SerializeField] private float spatialBlend = 1f;
        [SerializeField] private float minDistance = 3f;
        [SerializeField] private float maxDistance = 40f;
        [SerializeField] private AudioRolloffMode rolloff = AudioRolloffMode.Linear;
        [Range(0f, 1f)][SerializeField] private float volume = 1f;

        [Header("Loudness (for creature)")]
        [SerializeField] private float silenceDb = -55f;
        [SerializeField] private float loudDb = -10f;
        [SerializeField] private float riseSpeed = 25f;
        [SerializeField] private float fallSpeed = 5f;

        [Header("On-screen")]
        [SerializeField] private bool showIndicator = true;

        public float Loudness { get; private set; }
        public bool IsLocal => voice && voice.isOwner;
        public bool IsMuted => voice && voice.muted;
        public bool UserMuted => userMuted;

        public bool SetMuted(bool muted) => userMuted = muted;

        private ActiveRagdollCharacter character;
        private PurrVoicePlayer voice;
        private AudioSource emitter;
        private volatile float rawLoudness;
        private bool userMuted;

        private void Awake()
        {
            character = GetComponent<ActiveRagdollCharacter>();
            voice = GetComponent<PurrVoicePlayer>();

            if (!voice)
            {
                Debug.LogError("PlayerVoice: this prefab has no PurrVoicePlayer. Use Tools > Dont Wake It > Voice Setup.", this);
                enabled = false;
                return;
            }
            if (!character.IsReady)
            {
                Debug.LogError("PlayerVoice: the character isn't ready, so there is no head to speak from.", this);
                enabled = false;
                return;
            }

            CreateEmitter();
            voice.onLocalSample += OnLocalSample;
        }

        private void OnDestroy()
        {
            if (voice) voice.onLocalSample -= OnLocalSample;
            if (Local == this) Local = null;
        }

        private void CreateEmitter()
        {
            GameObject head = character.Head.gameObject;
            emitter = head.GetComponent<AudioSource>();
            if (!emitter) emitter = head.AddComponent<AudioSource>();

            emitter.playOnAwake = false;
            emitter.dopplerLevel = 0f;
            emitter.spatialBlend = spatialBlend;
            emitter.rolloffMode = rolloff;
            emitter.minDistance = minDistance;
            emitter.maxDistance = maxDistance;
            emitter.volume = volume;

            var provider = GetComponent<MultiAudioSourceVoiceProvider>();
            if (!provider)
            {
                Debug.LogError("PlayerVoice: add a MultiAudioSourceVoiceProvider (Tools > Dont Wake It > Voice Setup).", this);
                return;
            }
            provider.AddAudioSource(emitter);
        }

        private void OnLocalSample(ArraySegment<float> samples)
        {
            rawLoudness = MicrophoneSettings.Loudness(samples, silenceDb, loudDb);
        }

        private void Update()
        {
            if (!IsLocal) return;
            Local = this;

            HandleTalkInput();

            float target = voice.muted ? 0f : rawLoudness;
            float speed = target > Loudness ? riseSpeed : fallSpeed;
            Loudness = Mathf.Lerp(Loudness, target, 1f - Mathf.Exp(-speed * Time.deltaTime));
        }

        private void HandleTalkInput()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard[muteKey].wasPressedThisFrame) userMuted = !userMuted;

            bool talking = talkMode == TalkMode.AlwaysOn || (keyboard != null && keyboard[pushToTalkKey].isPressed);
            voice.muted = userMuted || !talking;
        }

        private void OnGUI()
        {
            if (!showIndicator || !IsLocal || ConnectionManager.Instance == null) return;

            const float x = 12f, w = 170f, h = 14f;
            float y = Screen.height - 34f;

            if (!MicrophoneSettings.HasMicrophone)
            {
                GUI.Label(new Rect(x, y - 4f, 300f, 22f), "NO MICROPHONE");
                return;
            }

            string label = userMuted ? "MIC MUTED  (" + muteKey + ")"
                : voice.muted ? "MIC OFF  (hold " + pushToTalkKey + " to talk)"
                : "MIC  (" + muteKey + " to mute)";
            GUI.Label(new Rect(x, y - 20f, 300f, 22f), label);

            GUI.Box(new Rect(x, y, w, h), GUIContent.none);
            if (!voice.muted)
                GUI.Box(new Rect(x, y, w * Mathf.Clamp01(Loudness), h), GUIContent.none);
        }
    }    
}