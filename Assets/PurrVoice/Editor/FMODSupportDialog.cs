#if UNITY_EDITOR
using UnityEditor;

namespace PurrVoice.Editor
{
    static class FMODSupportDialog
    {
        internal static void Show()
        {
            bool enable = EditorUtility.DisplayDialog(
                "Enable FMOD Support",
                "This will add the PURRVOICE_FMOD scripting define symbol.\n\n" +
                "Make sure the FMOD Unity plugin (com.fmod.unity) is installed in your project, " +
                "otherwise the FMOD integration scripts will not compile.\n\n" +
                "Enable FMOD support?",
                "Enable",
                "Cancel");

            if (enable)
            {
                IntegrationDefineManager.SetFMODEnabled(true);
            }
        }
    }
}
#endif
