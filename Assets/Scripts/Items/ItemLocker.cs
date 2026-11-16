using DontWakeIt.Networking;
using PurrNet;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DontWakeIt.Items
{
    public class ItemLocker : NetworkBehaviour, IInteractable
    {
        [SerializeField] private ItemCatalog catalog;
        [SerializeField] private Transform dispensePoint;
        [SerializeField] private float dispenseCooldown = 0.4f;
        [SerializeField] private string prompt = "Open Tool Locker";

        private bool open;
        private float lastDispense = float.NegativeInfinity;

        public string Prompt => prompt;
        public bool CanInteract(PlayerInventory who) => catalog != null && !open;

        public void Interact(PlayerInventory who)
        {
            if (!catalog || open) return;
            open = true;
            ConnectionManager.UiBlockers++;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Close()
        {
            if(!open) return;
            open = false;
            ConnectionManager.UiBlockers = Mathf.Max(0, ConnectionManager.UiBlockers - 1);
            if(ConnectionManager.UiBlockers == 0 && !ConnectionManager.PauseOpen)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void OnDisable() => Close();

        private void Update()
        {
            if (open && Keyboard.current != null && Keyboard.current[Key.Escape].wasPressedThisFrame)
                Close();
        }

        private void OnGUI()
        {
            if (!open || !catalog) return;

            const float w = 340f;
            float h = 90f + catalog.items.Count * 40f;
            var box = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.Box(box, "Tool locker");

            GUILayout.BeginArea(new Rect(box.x + 16f, box.y + 30f, w - 32f, h - 40f));
            for (int i = 0; i < catalog.items.Count; i++)
            {
                var def = catalog.items[i];
                if (!def) continue;
                string label = def.displayName + (def.tier > 1 ? "  (tier " + def.tier + ")" : string.Empty);
                if (GUILayout.Button(label, GUILayout.Height(34f))) Dispense(i);
            }
            GUILayout.Space(6f);
            if (GUILayout.Button("Close  (Esc)", GUILayout.Height(26f))) Close();
            GUILayout.EndArea();
        }

        private void Dispense(int index) => DispenseRpc(index);

        [ServerRpc(requireOwnership: false)]
        private void DispenseRpc(int index)
        {
            if(!catalog || index < 0 || index >= catalog.items.Count) return;
            if (Time.time - lastDispense < dispenseCooldown) return;

            var def = catalog.items[index];
            if(!def || !def.prefab) return;
            lastDispense = Time.time;

            Vector3 position = dispensePoint ? dispensePoint.position : transform.position + transform.forward * 1.2f + Vector3.up * 1f;
            Quaternion rotation = dispensePoint ? dispensePoint.rotation : transform.rotation;

            var manager = NetworkManager.main;
            var instance = UnityProxy.Instantiate(def.prefab, position, rotation, gameObject.scene);
            if (!instance || !manager) return;

            if (instance.TryGetComponent(out NetworkIdentity identity) && !identity.IsSpawned(manager.isServer))
                manager.Spawn(instance);
        }
    }
}