#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using DontWakeIt.Networking;
using DontWakeIt.UI;
using PurrNet;
using PurrNet.Steam;
using PurrNet.Transports;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DontWakeIt.EditorTools
{
    /// <summary>
    /// Tools > Dont Wake It > Networking Setup.
    /// Creates the Boot scene (NetworkManager + transports + ConnectionManager) and the MainMenu scene,
    /// and puts Boot, MainMenu and your game scene in the Build Settings in the right order.
    /// </summary>
    public class NetworkingSetupTools : EditorWindow
    {
        private const string ScenesFolder = "Assets/Scenes";
        private const string BootPath = ScenesFolder + "/Boot.unity";
        private const string MenuPath = ScenesFolder + "/MainMenu.unity";

        private SceneAsset gameScene;

        [MenuItem("Tools/Dont Wake It/Networking Setup")]
        private static void Open()
        {
            var w = GetWindow<NetworkingSetupTools>("Networking Setup");
            w.minSize = new Vector2(380f, 330f);
            if (!w.gameScene) w.gameScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenesFolder + "/SampleScene.unity");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Multiplayer + main menu", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "1. Pick the scene the game is played in (it needs a PlayerSpawner with your Player prefab).\n" +
                "2. Press 'Create Boot + Main Menu Scenes'.\n" +
                "3. Press Play from the Boot scene.\n\n" +
                "Room code length / characters are on the ConnectionManager in the Boot scene.",
                MessageType.Info);

            gameScene = (SceneAsset)EditorGUILayout.ObjectField("Game scene", gameScene, typeof(SceneAsset), false);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!gameScene))
            {
                if (GUILayout.Button("Create Boot + Main Menu Scenes", GUILayout.Height(32f)))
                    CreateScenes();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Steam (optional)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Install the Steamworks.NET package (Package Manager > Add from git URL):\n" +
                "https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net\n\n" +
                "Steam must be running. For testing, steam_appid.txt with app id 480 (Spacewar) is used.",
                MessageType.None);

            if (GUILayout.Button("Create steam_appid.txt (480 / Spacewar)"))
                CreateSteamAppId();
        }

        // ---------------------------------------------------------------- Scenes

        private void CreateScenes()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            if ((File.Exists(BootPath) || File.Exists(MenuPath)) &&
                !EditorUtility.DisplayDialog("Replace scenes?",
                    "Boot.unity and/or MainMenu.unity already exist in Assets/Scenes. Replace them?", "Replace", "Cancel"))
                return;

            string gameScenePath = AssetDatabase.GetAssetPath(gameScene);
            string gameSceneName = Path.GetFileNameWithoutExtension(gameScenePath);

            CreateMenuScene();
            CreateBootScene(gameSceneName);
            SetBuildSettings(gameScenePath);

            EditorSceneManager.OpenScene(BootPath);
            Debug.Log("Networking setup done. Press Play from the Boot scene. " +
                      "Make sure '" + gameSceneName + "' has a PlayerSpawner with your Player prefab.");
        }

        private static void CreateMenuScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.06f, 0.09f);
            camGo.AddComponent<AudioListener>();

            new GameObject("MainMenu").AddComponent<MainMenuUI>();

            EditorSceneManager.SaveScene(scene, MenuPath);
        }

        private static void CreateBootScene(string gameSceneName)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var go = new GameObject("Networking");

            // Transports first, so the NetworkManager doesn't add its own default.
            var udp = go.AddComponent<UDPTransport>();
            go.AddComponent<SteamTransport>();
            var manager = go.AddComponent<NetworkManager>();
            var connection = go.AddComponent<ConnectionManager>();

            var so = new SerializedObject(manager);
            so.FindProperty("_startServerFlags").intValue = 0;     // never auto-start: the menu does it
            so.FindProperty("_startClientFlags").intValue = 0;
            so.FindProperty("_dontDestroyOnLoad").boolValue = true;
            so.FindProperty("_transport").objectReferenceValue = udp;

            var rules = FindRules();
            if (rules) so.FindProperty("_networkRules").objectReferenceValue = rules;
            else Debug.LogWarning("No NetworkRules asset found. Assign one on the NetworkManager.");

            so.FindProperty("_networkPrefabs").objectReferenceValue = FindOrCreateNetworkPrefabs();
            so.ApplyModifiedPropertiesWithoutUndo();

            var cmSo = new SerializedObject(connection);
            cmSo.FindProperty("menuScene").stringValue = "MainMenu";
            cmSo.FindProperty("gameScene").stringValue = gameSceneName;
            cmSo.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, BootPath);
        }

        private static NetworkRules FindRules()
        {
            NetworkRules first = null;
            foreach (string guid in AssetDatabase.FindAssets("t:NetworkRules"))
            {
                var rules = AssetDatabase.LoadAssetAtPath<NetworkRules>(AssetDatabase.GUIDToAssetPath(guid));
                if (!rules) continue;
                if (rules.name == "ServerOwner") return rules;
                if (!first) first = rules;
            }
            return first;
        }

        private static NetworkPrefabs FindOrCreateNetworkPrefabs()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:NetworkPrefabs"))
            {
                var existing = AssetDatabase.LoadAssetAtPath<NetworkPrefabs>(AssetDatabase.GUIDToAssetPath(guid));
                if (existing) return existing;
            }

            // Auto-generate (the default) registers every prefab that has a NetworkIdentity, e.g. the Player.
            var created = CreateInstance<NetworkPrefabs>();
            AssetDatabase.CreateAsset(created, "Assets/NetworkPrefabs.asset");
            AssetDatabase.SaveAssets();
            return created;
        }

        private static void SetBuildSettings(string gameScenePath)
        {
            var scenes = new List<EditorBuildSettingsScene>
            {
                new EditorBuildSettingsScene(BootPath, true),
                new EditorBuildSettingsScene(MenuPath, true),
                new EditorBuildSettingsScene(gameScenePath, true)
            };

            // Keep any other scenes that were already listed.
            foreach (var existing in EditorBuildSettings.scenes)
            {
                if (existing.path == BootPath || existing.path == MenuPath || existing.path == gameScenePath) continue;
                scenes.Add(existing);
            }
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        // ---------------------------------------------------------------- Steam

        private static void CreateSteamAppId()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            string path = Path.Combine(root, "steam_appid.txt");

            if (File.Exists(path) &&
                !EditorUtility.DisplayDialog("steam_appid.txt exists", "Overwrite it with 480 (Spacewar)?", "Overwrite", "Cancel"))
                return;

            File.WriteAllText(path, "480");
            Debug.Log("Wrote " + path + " (app id 480). Replace it with your own app id once you have one.");
        }
    }
}
#endif
