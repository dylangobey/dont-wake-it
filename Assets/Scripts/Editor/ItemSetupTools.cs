#if UNITY_EDITOR
using DontWakeIt.Items;
using DontWakeIt.Player;
using PurrNet;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DontWakeIt.EditorTools
{
    /// <summary>
    /// Tools > Dont Wake It > Item Setup.
    ///  1. Adds the inventory to the Player prefab.
    ///  2. Creates a few starter tools (definitions + prefabs + a catalog) to test with.
    ///  3. Puts a tool locker in the open scene.
    /// </summary>
    public class ItemSetupTools : EditorWindow
    {
        private const string Folder = "Assets/Items";
        private const string CatalogPath = Folder + "/StarterCatalog.asset";

        private GameObject playerPrefab;

        [MenuItem("Tools/Dont Wake It/Item Setup")]
        private static void Open()
        {
            var w = GetWindow<ItemSetupTools>("Item Setup");
            w.minSize = new Vector2(400f, 330f);
            if (!w.playerPrefab && Selection.activeObject is GameObject go && PrefabUtility.IsPartOfPrefabAsset(go))
                w.playerPrefab = go;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Items and tools", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Controls: E pick up / use, 1-3 or mouse wheel switch, G drop, F use.\n\n" +
                "Do the steps in order. Item prefabs need to be in your NetworkPrefabs asset " +
                "(automatic if it auto-generates).",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("1. Player", EditorStyles.boldLabel);
            playerPrefab = (GameObject)EditorGUILayout.ObjectField("Player prefab", playerPrefab, typeof(GameObject), false);
            using (new EditorGUI.DisabledScope(!playerPrefab))
            {
                if (GUILayout.Button("Add Inventory To Player Prefab", GUILayout.Height(28f)))
                    AddInventory();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("2. Tools", EditorStyles.boldLabel);
            if (GUILayout.Button("Create Starter Tools (Brush, Rake, Lantern, Bucket)", GUILayout.Height(28f)))
                CreateStarterTools();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("3. Locker", EditorStyles.boldLabel);
            if (GUILayout.Button("Add Tool Locker To Open Scene", GUILayout.Height(28f)))
                AddLocker();
        }

        // ---------------------------------------------------------------- Player

        private void AddInventory()
        {
            string path = AssetDatabase.GetAssetPath(playerPrefab);
            if (string.IsNullOrEmpty(path) || !PrefabUtility.IsPartOfPrefabAsset(playerPrefab))
            {
                EditorUtility.DisplayDialog("Not a prefab", "Drag in the Player prefab from the Project window.", "OK");
                return;
            }

            using (var scope = new PrefabUtility.EditPrefabContentsScope(path))
            {
                if (!scope.prefabContentsRoot.GetComponent<ActiveRagdollCharacter>())
                {
                    EditorUtility.DisplayDialog("Wrong prefab?", "This prefab has no Active Ragdoll Character on its root.", "OK");
                    return;
                }
                if (!scope.prefabContentsRoot.GetComponent<PlayerInventory>())
                    scope.prefabContentsRoot.AddComponent<PlayerInventory>();
            }
            AssetDatabase.SaveAssets();
            Debug.Log("Inventory added to '" + playerPrefab.name + "'.");
        }

        // ---------------------------------------------------------------- Tools

        private struct ToolSpec
        {
            public string id, name;
            public ToolKind kind;
            public PrimitiveType shape;
            public Vector3 scale;
            public Color color;
            public Vector3 heldEuler;
            public float noise;
        }

        private static void CreateStarterTools()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "Items");

            var specs = new[]
            {
                new ToolSpec { id = "brush",   name = "Brush",   kind = ToolKind.Brush,   shape = PrimitiveType.Capsule,  scale = new Vector3(0.08f, 0.3f, 0.08f),  color = new Color(0.75f, 0.55f, 0.3f), heldEuler = new Vector3(90f, 0f, 0f), noise = 0.5f },
                new ToolSpec { id = "rake",    name = "Rake",    kind = ToolKind.Rake,    shape = PrimitiveType.Cube,     scale = new Vector3(0.5f, 0.05f, 0.08f),  color = new Color(0.5f, 0.5f, 0.55f),  heldEuler = new Vector3(0f, 90f, 0f), noise = 1.2f },
                new ToolSpec { id = "lantern", name = "Lantern", kind = ToolKind.Lantern, shape = PrimitiveType.Sphere,   scale = new Vector3(0.22f, 0.22f, 0.22f), color = new Color(1f, 0.85f, 0.4f),    heldEuler = Vector3.zero,             noise = 0.8f },
                new ToolSpec { id = "bucket",  name = "Bucket",  kind = ToolKind.Bucket,  shape = PrimitiveType.Cylinder, scale = new Vector3(0.28f, 0.16f, 0.28f), color = new Color(0.35f, 0.5f, 0.7f),  heldEuler = Vector3.zero,             noise = 1.6f },
            };

            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(CatalogPath);
            if (!catalog)
            {
                catalog = CreateInstance<ItemCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }
            catalog.items.Clear();

            foreach (var spec in specs)
            {
                string prefabPath = Folder + "/" + spec.name + ".prefab";
                string defPath = Folder + "/" + spec.name + ".asset";

                var def = AssetDatabase.LoadAssetAtPath<ItemDefinition>(defPath);
                if (!def)
                {
                    def = CreateInstance<ItemDefinition>();
                    AssetDatabase.CreateAsset(def, defPath);
                }
                def.id = spec.id;
                def.displayName = spec.name;
                def.kind = spec.kind;
                def.heldLocalEuler = spec.heldEuler;
                def.impactNoise = spec.noise;

                var prefab = BuildPrefab(spec, def, prefabPath);
                def.prefab = prefab;
                EditorUtility.SetDirty(def);
                catalog.items.Add(def);
            }

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Starter tools created in " + Folder + ". Replace the primitive shapes with your own models whenever you like.");
        }

        private static GameObject BuildPrefab(ToolSpec spec, ItemDefinition def, string path)
        {
            var go = GameObject.CreatePrimitive(spec.shape);
            go.name = spec.name;
            go.transform.localScale = spec.scale;

            var renderer = go.GetComponent<Renderer>();
            var material = new Material(renderer.sharedMaterial) { color = spec.color };
            renderer.sharedMaterial = material;

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 2f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            go.AddComponent<NetworkRigidbody>();   // owner authority is on by default
            var item = go.AddComponent<Item>();

            var so = new SerializedObject(item);
            so.FindProperty("definition").objectReferenceValue = def;
            so.ApplyModifiedPropertiesWithoutUndo();

            var saved = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return saved;
        }

        // ---------------------------------------------------------------- Locker

        private static void AddLocker()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(CatalogPath);
            if (!catalog)
            {
                EditorUtility.DisplayDialog("No tools yet", "Press 'Create Starter Tools' first (or assign your own catalog on the locker).", "OK");
                return;
            }

            var locker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            locker.name = "Tool Locker";
            locker.transform.localScale = new Vector3(1.2f, 2f, 0.6f);
            locker.transform.position = SceneView.lastActiveSceneView
                ? SceneView.lastActiveSceneView.pivot + Vector3.up
                : new Vector3(0f, 1f, 0f);
            Undo.RegisterCreatedObjectUndo(locker, "Add Tool Locker");

            var itemLocker = locker.AddComponent<ItemLocker>();

            // Where tools appear: in front of the locker (-Z side). The offset is scaled by the locker, so this is about 1 m out.
            var point = new GameObject("Dispense Point").transform;
            point.SetParent(locker.transform, false);
            point.localPosition = new Vector3(0f, -0.2f, -1.6f);

            var so = new SerializedObject(itemLocker);
            so.FindProperty("catalog").objectReferenceValue = catalog;
            so.FindProperty("dispensePoint").objectReferenceValue = point;
            so.ApplyModifiedPropertiesWithoutUndo();

            Selection.activeGameObject = locker;
            EditorSceneManager.MarkSceneDirty(locker.scene);
            Debug.Log("Tool locker added. Move it where you want and rotate it so its front (-Z side) faces the room.");
        }
    }
}
#endif
