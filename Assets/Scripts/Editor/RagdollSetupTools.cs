#if UNITY_EDITOR
using DontWakeIt.Player;
using PurrNet;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DontWakeIt.EditorTools
{
    public class RagdollSetupTools : EditorWindow
    {
        private GameObject character;
        private bool createGround = true;
        private bool disableOtherCameras = true;
        private bool addNetworkBones;
        private bool showAnimatedGhost;

        [MenuItem("Tools/Dont Wake It/Active Ragdoll Setup")]
        private static void Open()
        {
            var w = GetWindow<RagdollSetupTools>("Active Ragdoll Setup");
            w.minSize = new Vector2(340f, 280f);
            if(!w.character) w.character = Selection.activeGameObject;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Animated active ragdoll", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Drag in the character that has the Animator and walk animation (the object whose children are " + "Head, Torso, Stomach, Hips, LeftArm...). It stays in the scene as the hidden animation source; " + "a physics copy is built from it when you press Play.", MessageType.Info);

            character = (GameObject)EditorGUILayout.ObjectField("Character", character, typeof(GameObject), true);

            EditorGUILayout.Space();
            createGround = EditorGUILayout.Toggle(new GUIContent("Create test ground", "Adds a large floor if the scene has no TestGround object."), createGround);
            disableOtherCameras = EditorGUILayout.Toggle(new GUIContent("Disable Other Cameras", "Turns off other cameras/audio listeners so only the player's view renders"), disableOtherCameras);
            addNetworkBones = EditorGUILayout.Toggle(new GUIContent("Add NetworkBones",
                "Adds PurrNet's NetworkBones for syncing. Leave off for offline greybox testing."), addNetworkBones);
            showAnimatedGhost = EditorGUILayout.Toggle(new GUIContent("Show animated ghost",
                "Debug: draws the animation-only character over the physics one."), showAnimatedGhost);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!character))
            {
                if (GUILayout.Button("Create Player From Character", GUILayout.Height(32f)))
                    CreatePlayer();
            }
        }

        private void CreatePlayer()
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Create Active Ragdoll Player");

            if (createGround && !GameObject.Find("TestGround"))
            {
                var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
                ground.name = "TestGround";
                ground.transform.position = new Vector3(0f, -0.5f, 0f);
                ground.transform.localScale = new Vector3(60f, 1f, 60f);
                Undo.RegisterCreatedObjectUndo(ground, "Create TestGround");
            }

            if (disableOtherCameras)
            {
                foreach (var cam in FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (cam.transform.IsChildOf(character.transform)) continue;
                    Undo.RecordObject(cam, "Disable camera");
                    cam.enabled = false;
                }
                foreach (var l in FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (l.transform.IsChildOf(character.transform)) continue;
                    Undo.RecordObject(l, "Disable listener");
                    l.enabled = false;
                }
            }

            // The player lives on its own empty object, never on the character itself.
            var root = new GameObject("Player");
            var rig = root.AddComponent<ActiveRagdollCharacter>();
            if (addNetworkBones) root.AddComponent<NetworkBones>();
            root.AddComponent<RagdollPlayerController>();

            var so = new SerializedObject(rig);
            so.FindProperty("animatedRig").objectReferenceValue = character.transform;
            so.FindProperty("showAnimatedRig").boolValue = showAnimatedGhost;
            so.ApplyModifiedPropertiesWithoutUndo();

            Undo.RegisterCreatedObjectUndo(root, "Create Player");
            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(root.scene);
            Undo.CollapseUndoOperations(Undo.GetCurrentGroup());
        }
    }
}
#endif