using System.Collections.Generic;
using PurrNet.Voice;
using UnityEngine;

namespace DontWakeIt.Voice
{
    /// <summary>
    /// Which microphone to use. The choice is saved on this PC, so it carries across sessions, and is made in the
    /// main menu. Always falls back to the first device, and to "no microphone" rather than throwing.
    /// </summary>
    public static class MicrophoneSettings
    {
        private const string PrefKey = "dwi.microphone";

        public static IReadOnlyList<Device> Devices => AudioDevices.devices;

        public static bool HasMicrophone => AudioDevices.devices.Count > 0;

        /// <summary>The saved device id (empty = use the first one).</summary>
        public static string PreferredId
        {
            get => PlayerPrefs.GetString(PrefKey, string.Empty);
            set
            {
                PlayerPrefs.SetString(PrefKey, value ?? string.Empty);
                PlayerPrefs.Save();
            }
        }

        /// <summary>The device that should be used right now, or null if there is no microphone.</summary>
        public static Device Resolve()
        {
            var list = AudioDevices.devices;
            if (list.Count == 0) return null;

            string preferred = PreferredId;
            if (!string.IsNullOrEmpty(preferred))
            {
                foreach (var device in list)
                    if (device.device.id == preferred) return device;
            }
            return list[0];
        }

        /// <summary>Moves to the next microphone and returns it (null if there are none).</summary>
        public static Device Next()
        {
            var list = AudioDevices.devices;
            if (list.Count == 0) return null;

            var current = Resolve();
            int index = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i] == current) { index = i; break; }

            var next = list[(index + 1) % list.Count];
            PreferredId = next.device.id;
            return next;
        }

        /// <summary>A readable name for the menu.</summary>
        public static string Describe(Device device)
        {
            if (device == null) return "none";
            string name = device.device.displayName;
            return string.IsNullOrEmpty(name) ? device.device.id : name;
        }

        /// <summary>Loudness of a block of samples on a 0..1 scale (dB mapped between silenceDb and loudDb).</summary>
        public static float Loudness(System.ArraySegment<float> samples, float silenceDb = -55f, float loudDb = -10f)
        {
            int count = samples.Count;
            if (count == 0 || samples.Array == null) return 0f;

            double sum = 0d;
            for (int i = 0; i < count; i++)
            {
                float s = samples.Array[samples.Offset + i];
                sum += s * s;
            }

            float rms = (float)System.Math.Sqrt(sum / count);
            float db = 20f * Mathf.Log10(Mathf.Max(rms, 1e-5f));
            return Mathf.InverseLerp(silenceDb, loudDb, db);
        }
    }
}
