#if UNITY_EDITOR
using DontWakeIt.Player;
using DontWakeIt.Voice;
using PurrNet.Voice;
using UnityEditor;
using UnityEngine;

namespace DontWakeIt.EditorTools
{
    /// <summary>
    /// Tools > Dont Wake It > Voice Setup.
    /// Adds everything PurrVoice needs to the Player prefab: PurrVoicePlayer, our microphone provider, the
    /// output provider, PlayerVoice, and an Opus codec settings asset.
    /// </summary>
    public class VoiceSetupTools : EditorWindow
    {
        private GameObject playerPrefab;
        private const string LobbyPrefabPath = "Assets/Prefabs/LobbyVoice.prefab";

        [MenuItem("Tools/Dont Wake It/Voice Setup")]
        private static void Open()
        {
            var w = GetWindow<VoiceSetupTools>("Voice Setup");
            w.minSize = new Vector2(380f, 300f);
            if (!w.playerPrefab && Selection.activeObject is GameObject go && PrefabUtility.IsPartOfPrefabAsset(go))
                w.playerPrefab = go;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Proximity voice chat (PurrVoice)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Drag in your Player PREFAB (the asset, not a scene object: the one with Active Ragdoll Character " +
                "and Ragdoll Player Controller).\n\n" +
                "This adds a PurrVoicePlayer, the microphone and speaker providers, and PlayerVoice. Other players " +
                "are heard from their head, getting quieter with distance. M mutes your mic.",
                MessageType.Info);

            playerPrefab = (GameObject)EditorGUILayout.ObjectField("Player prefab", playerPrefab, typeof(GameObject), false);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!playerPrefab))
            {
                if (GUILayout.Button("Add Voice To Player Prefab", GUILayout.Height(32f)))
                    AddVoice();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Lobby voice", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Lets players talk in the lobby before the game starts. Creates a Lobby Voice prefab and a PlayerSpawner " +
                "for it in the MainMenu scene (Assets/Scenes/MainMenu.unity).",
                MessageType.None);
            if (GUILayout.Button("Set Up Lobby Voice", GUILayout.Height(28f)))
                SetUpLobbyVoice();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Standalone builds on macOS need a microphone usage description (Project Settings > Player > " +
                "Other Settings > Microphone Usage Description), or macOS will block the microphone.",
                MessageType.None);
        }

        private void AddVoice()
        {
            string path = AssetDatabase.GetAssetPath(playerPrefab);
            if (string.IsNullOrEmpty(path) || !PrefabUtility.IsPartOfPrefabAsset(playerPrefab))
            {
                EditorUtility.DisplayDialog("Not a prefab", "Please drag in the Player prefab from the Project window.", "OK");
                return;
            }

            using (var scope = new PrefabUtility.EditPrefabContentsScope(path))
            {
                GameObject root = scope.prefabContentsRoot;

                if (!root.GetComponent<ActiveRagdollCharacter>())
                {
                    EditorUtility.DisplayDialog("Wrong prefab?",
                        "This prefab has no Active Ragdoll Character on its root, so it doesn't look like the Player prefab.", "OK");
                    return;
                }

                var voice = GetOrAdd<PurrVoicePlayer>(root);
                var input = GetOrAdd<VoiceInputProvider>(root);
                var output = GetOrAdd<MultiAudioSourceVoiceProvider>(root);
                GetOrAdd<PlayerVoice>(root);

                // No AudioSource goes on the prefab root: PlayerVoice creates one on the head at runtime, so the
                // voice comes from where the body actually is.
                var so = new SerializedObject(voice);
                so.FindProperty("_inputProvider").objectReferenceValue = input;
                so.FindProperty("_outputProvider").objectReferenceValue = output;
                so.FindProperty("_codecSettings").objectReferenceValue = FindOrCreateCodec();
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            AssetDatabase.SaveAssets();
            Debug.Log("Voice added to '" + playerPrefab.name + "'. Press Play and host/join a room to test.");
        }

        private static void SetUpLobbyVoice()
        {
            if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            if (!AssetDatabase.IsValidFolder("Assets/Prefabs")) AssetDatabase.CreateFolder("Assets", "Prefabs");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LobbyPrefabPath);
            if (!prefab)
            {
                var temp = new GameObject("LobbyVoice");
                temp.AddComponent<PurrNet.NetworkIdentity>();
                temp.AddComponent<PurrVoicePlayer>();
                temp.AddComponent<VoiceInputProvider>();
                temp.AddComponent<MultiAudioSourceVoiceProvider>();
                temp.AddComponent<LobbyVoice>();
                prefab = PrefabUtility.SaveAsPrefabAsset(temp, LobbyPrefabPath);
                DestroyImmediate(temp);
            }

            using (var scope = new PrefabUtility.EditPrefabContentsScope(LobbyPrefabPath))
            {
                GameObject root = scope.prefabContentsRoot;
                var voice = GetOrAdd<PurrVoicePlayer>(root);
                var so = new SerializedObject(voice);
                so.FindProperty("_inputProvider").objectReferenceValue = GetOrAdd<VoiceInputProvider>(root);
                so.FindProperty("_outputProvider").objectReferenceValue = GetOrAdd<MultiAudioSourceVoiceProvider>(root);
                so.FindProperty("_codecSettings").objectReferenceValue = FindOrCreateCodec();
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            const string menuPath = "Assets/Scenes/MainMenu.unity";
            if (!System.IO.File.Exists(menuPath))
            {
                Debug.LogWarning("Lobby Voice prefab created, but " + menuPath + " doesn't exist. Add a PlayerSpawner " +
                                 "with it to your menu scene yourself.");
                return;
            }

            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(menuPath);
            var spawner = Object.FindFirstObjectByType<PurrNet.PlayerSpawner>();
            if (!spawner) spawner = new GameObject("Lobby Voice Spawner").AddComponent<PurrNet.PlayerSpawner>();

            var spawnerSo = new SerializedObject(spawner);
            spawnerSo.FindProperty("_playerPrefab").objectReferenceValue = prefab;
            spawnerSo.ApplyModifiedPropertiesWithoutUndo();

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("Lobby voice set up: " + LobbyPrefabPath + " + a PlayerSpawner in the MainMenu scene. " +
                      "Make sure your NetworkPrefabs asset includes the prefab (auto-generate does this).");
        }

        private static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var existing = go.GetComponent<T>();
            return existing ? existing : go.AddComponent<T>();
        }

        private static AudioCodecSettings FindOrCreateCodec()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:OpusCodecSettings"))
            {
                var existing = AssetDatabase.LoadAssetAtPath<AudioCodecSettings>(AssetDatabase.GUIDToAssetPath(guid));
                if (existing) return existing;
            }

            if (!AssetDatabase.IsValidFolder("Assets/Voice"))
                AssetDatabase.CreateFolder("Assets", "Voice");

            var created = CreateInstance<OpusCodecSettings>();
            AssetDatabase.CreateAsset(created, "Assets/Voice/OpusCodecSettings.asset");
            AssetDatabase.SaveAssets();
            return created;
        }
    }
}
#endif
