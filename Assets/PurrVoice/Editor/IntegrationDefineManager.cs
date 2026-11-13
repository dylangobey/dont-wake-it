using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;

namespace PurrVoice.Editor
{
    [InitializeOnLoad]
    static class IntegrationDefineManager
    {
        static readonly (string define, string typeName)[] Integrations =
        {
            ("PURRVOICE_FMOD", "FMODUnity.RuntimeManager"),
        };

        const string FMOD_DEFINE = "PURRVOICE_FMOD";

        static IntegrationDefineManager()
        {
            UpdateDefines();
        }

        static void GetCurrentDefines(out HashSet<string> defines)
        {
            string raw = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup));
            defines = new HashSet<string>(
                raw.Split(';', StringSplitOptions.RemoveEmptyEntries));
        }

        static void ApplyDefines(HashSet<string> defines)
        {
            string joined = string.Join(";", defines);
            PlayerSettings.SetScriptingDefineSymbols(
                NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup),
                joined);
        }

        static void UpdateDefines()
        {
            GetCurrentDefines(out var defines);
            bool changed = false;

            foreach (var (define, typeName) in Integrations)
            {
                bool typeExists = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Any(a => a.GetType(typeName) != null);

                if (typeExists && defines.Add(define))
                    changed = true;
                else if (!typeExists && defines.Remove(define))
                    changed = true;
            }

            if (changed)
                ApplyDefines(defines);
        }

        internal static bool HasDefine(string define)
        {
            GetCurrentDefines(out var defines);
            return defines.Contains(define);
        }

        internal static void SetDefine(string define, bool enabled)
        {
            GetCurrentDefines(out var defines);
            bool changed = enabled ? defines.Add(define) : defines.Remove(define);
            if (changed)
                ApplyDefines(defines);
        }

        internal static bool IsFMODEnabled => HasDefine(FMOD_DEFINE);

        internal static void SetFMODEnabled(bool enabled) => SetDefine(FMOD_DEFINE, enabled);

        const string MENU_FMOD = "Tools/PurrVoice/FMOD Support";

        [MenuItem(MENU_FMOD, priority = 100)]
        static void ToggleFMOD() => SetFMODEnabled(!IsFMODEnabled);

        [MenuItem(MENU_FMOD, true)]
        static bool ToggleFMODValidate()
        {
            Menu.SetChecked(MENU_FMOD, IsFMODEnabled);
            return true;
        }
    }
}
