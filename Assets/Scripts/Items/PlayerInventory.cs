using System.Collections;
using DontWakeIt.Networking;
using DontWakeIt.Player;
using PurrNet;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DontWakeIt.Items
{
    /// <summary>
    /// What a player is carrying, and the controls for it. Goes on the Player prefab.
    ///
    ///   E        pick up the item you're looking at / use the thing you're looking at (a locker...)
    ///   1 2 3    take that slot out (press again to put it away); the mouse wheel cycles
    ///   G        drop (toss) what's in your hand
    ///   F        use what's in your hand (hold)
    ///
    /// One item is in the hand at a time, joined to the hand with a physics joint; the others are stowed (hidden).
    /// Picking things up goes through the server so two players can't take the same item.
    /// </summary>
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public class PlayerInventory : NetworkBehaviour
    {
        /// <summary>The inventory of the player on this machine.</summary>
        public static PlayerInventory Local { get; private set; }

        private static readonly System.Collections.Generic.List<PlayerInventory> All = new System.Collections.Generic.List<PlayerInventory>();

        /// <summary>The inventory belonging to this player (on any machine), or null.</summary>
        public static PlayerInventory FindOwnedBy(PlayerID? player)
        {
            if (!player.HasValue) return null;
            foreach (var inventory in All)
                if (inventory && inventory.isSpawned && inventory.owner == player) return inventory;
            return null;
        }

        [Header("Slots")]
        [Range(1, 6)][SerializeField] private int slotCount = 3;
        [SerializeField] private BodySide hand = BodySide.Right;

        [Header("Reach")]
        [SerializeField] private float reach = 3f;
        [Tooltip("Thickness of the look ray, so small things are easy to point at.")]
        [SerializeField] private float aimRadius = 0.25f;
        [SerializeField] private float throwSpeed = 3f;

        [Header("Keys")]
        [SerializeField] private Key interactKey = Key.E;
        [SerializeField] private Key dropKey = Key.G;
        [SerializeField] private Key useKey = Key.F;

        [Header("On-screen")]
        [SerializeField] private bool showHud = true;
        [Tooltip("Shows what the look ray hits and logs what happens when you press the interact key. Turn off once it works.")]
        [SerializeField] private bool showDebug = true;

        public ActiveRagdollCharacter Character { get; private set; }
        public int SlotCount => slotCount;
        public Item Equipped => equipped >= 0 ? slots[equipped] : null;
        public Item GetSlot(int index) => index >= 0 && index < slots.Length ? slots[index] : null;
        public bool IsLocal => isSpawned && isOwner;

        /// <summary>Where stowed items wait (hidden), so they travel with the player.</summary>
        public Vector3 StowPoint => Character.Hips.position;

        /// <summary>The hand an item is held in.</summary>
        public Transform HandTransform => Character.Body(hand == BodySide.Right ? Bone.RightHand : Bone.LeftHand).transform;

        private static readonly Key[] NumberKeys = { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6 };

        private Item[] slots;
        private int equipped = -1;
        private Item pendingPickup;
        private Item lookItem;
        private IInteractable lookThing;
        private string lookDebug = "no ray yet";
        private string message;
        private System.Collections.Generic.HashSet<Collider> ownColliders;
        private float messageUntil;

        // ---------------------------------------------------------------- Lifecycle

        private void Awake()
        {
            Character = GetComponent<ActiveRagdollCharacter>();
            slots = new Item[slotCount];
            All.Add(this);
        }

        protected override void OnDestroy()
        {
            All.Remove(this);
            if (Local == this) Local = null;
            base.OnDestroy();
        }

        /// <summary>Called by an item that left our hands some other way (despawned, ownership lost).</summary>
        public void Forget(Item item)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] != item) continue;
                slots[i] = null;
                if (equipped == i) equipped = -1;
            }
        }

        // ---------------------------------------------------------------- Input

        private void Update()
        {
            if (!IsLocal) return;
            Local = this;

            lookItem = null;
            lookThing = null;
            FindLookTarget();

            if (ConnectionManager.InputBlocked) return;
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard[interactKey].wasPressedThisFrame)
            {
                if (showDebug)
                    Debug.Log("[Inventory] " + interactKey + " pressed. lookItem=" + (lookItem ? lookItem.name : "none") +
                              " lookThing=" + (lookThing != null) + " | " + lookDebug, this);
                Interact();
            }
            if (keyboard[dropKey].wasPressedThisFrame) DropEquipped();

            for (int i = 0; i < slotCount && i < NumberKeys.Length; i++)
                if (keyboard[NumberKeys[i]].wasPressedThisFrame) ToggleSlot(i);

            var mouse = Mouse.current;
            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (scroll > 0.1f) CycleSlot(-1);
                else if (scroll < -0.1f) CycleSlot(1);
            }

            var item = Equipped;
            if (item)
            {
                if (keyboard[useKey].wasPressedThisFrame) item.BeginUse(this);
                if (keyboard[useKey].wasReleasedThisFrame) item.EndUse(this);
            }
        }

        private RagdollPlayerController controller;

        /// <summary>The camera the player looks through.</summary>
        private Camera ViewCamera
        {
            get
            {
                if (!controller) controller = GetComponent<RagdollPlayerController>();
                if (controller && controller.PlayerCamera) return controller.PlayerCamera;
                return Character.RigCamera ? Character.RigCamera : Camera.main;
            }
        }

        private bool IsOwnCollider(Collider c)
        {
            if (c.transform.IsChildOf(transform)) return true;
            if (ownColliders == null)
            {
                ownColliders = new System.Collections.Generic.HashSet<Collider>();
                foreach (var body in Character.Bodies)
                    if (body) foreach (var col in body.GetComponentsInChildren<Collider>(true)) ownColliders.Add(col);
            }
            return ownColliders.Contains(c);
        }

        private void FindLookTarget()
        {
            var cam = ViewCamera;
            if (!cam) { lookDebug = "NO CAMERA"; return; }
            lookDebug = "ray hits nothing";

            var ray = new Ray(cam.transform.position, cam.transform.forward);
            var hits = Physics.SphereCastAll(ray, aimRadius, reach, ~0, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (var hit in hits)
            {
                if (IsOwnCollider(hit.collider)) continue;   // our own body

                var item = hit.collider.GetComponentInParent<Item>();
                if (item)
                {
                    lookDebug = "item '" + item.name + "' state=" + item.State + " " + hit.distance.ToString("F1") + "m";
                    if (item.State == ItemState.World) { lookItem = item; return; }
                    continue;   // someone else's held item: look through it
                }

                var thing = hit.collider.GetComponentInParent<IInteractable>();
                if (thing != null)
                {
                    lookDebug = "interactable '" + hit.collider.name + "' can=" + thing.CanInteract(this);
                    if (thing.CanInteract(this)) { lookThing = thing; return; }
                }

                if (!hit.collider.isTrigger)
                {
                    if (!(thing != null)) lookDebug = "blocked by '" + hit.collider.name + "' " + hit.distance.ToString("F1") + "m";
                    return;   // a wall or the floor is in the way
                }
            }
        }

        private void Interact()
        {
            if (lookItem) TryPickUp(lookItem);
            else lookThing?.Interact(this);
        }

        // ---------------------------------------------------------------- Pick up

        private int FreeSlot()
        {
            for (int i = 0; i < slots.Length; i++) if (!slots[i]) return i;
            return -1;
        }

        private void TryPickUp(Item item)
        {
            if (pendingPickup) return;
            if (FreeSlot() < 0) { Say("Hands full: drop something first (G)"); return; }

            pendingPickup = item;
            RequestPickUp(item);
        }

        [ServerRpc]
        private void RequestPickUp(Item item, RPCInfo info = default)
        {
            bool ok = item && item.State == ItemState.World &&
                      Vector3.Distance(item.transform.position, Character.Hips.position) <= reach + 3f;
            if (ok) item.ServerClaim(info.sender);
            PickUpResult(info.sender, item, ok);
        }

        [TargetRpc]
        private void PickUpResult(PlayerID player, Item item, bool accepted)
        {
            pendingPickup = null;
            if (!accepted || !item) { Say("Couldn't pick that up"); return; }
            StartCoroutine(AcceptWhenOwned(item));
        }

        // The ownership message and this one arrive separately: wait until the item is really ours.
        private IEnumerator AcceptWhenOwned(Item item)
        {
            float end = Time.time + 3f;
            while (item && !item.isOwner && Time.time < end) yield return null;
            if (!item || !item.isOwner) yield break;

            int slot = FreeSlot();
            if (slot < 0)
            {
                item.LocalDrop(Vector3.zero);
                yield break;
            }

            slots[slot] = item;
            item.SetHolderStowed(this);   // already Stowed on the server
            if (equipped < 0) SetEquipped(slot);
        }

        // ---------------------------------------------------------------- Equip / drop

        private void ToggleSlot(int index) => SetEquipped(index == equipped ? -1 : index);

        private void CycleSlot(int direction)
        {
            int start = equipped < 0 ? (direction > 0 ? -1 : 0) : equipped;
            for (int step = 1; step <= slots.Length; step++)
            {
                int index = (start + direction * step + slots.Length * 2) % slots.Length;
                if (slots[index]) { SetEquipped(index); return; }
            }
        }

        /// <summary>Take this slot's item out (-1 = empty hands). The previous one is stowed.</summary>
        public void SetEquipped(int index)
        {
            if (index >= slots.Length) return;

            var current = Equipped;
            if (current) current.LocalStow();

            equipped = index;
            var next = Equipped;
            if (next) next.LocalHold(this, Character.Body(hand == BodySide.Right ? Bone.RightHand : Bone.LeftHand));
        }

        public void DropEquipped()
        {
            var item = Equipped;
            if (!item) return;

            slots[equipped] = null;
            equipped = -1;

            var cam = ViewCamera;
            Vector3 forward = cam ? cam.transform.forward : transform.forward;
            item.LocalDrop(Character.Hips.linearVelocity + forward * throwSpeed);

            // Take the next thing out of the pocket so the hand isn't left empty.
            for (int i = 0; i < slots.Length; i++)
                if (slots[i]) { SetEquipped(i); break; }
        }

        // ---------------------------------------------------------------- HUD

        private void Say(string text)
        {
            message = text;
            messageUntil = Time.time + 2f;
        }

        private void OnGUI()
        {
            if (!showHud || !IsLocal) return;

            if (showDebug)
                GUI.Label(new Rect(12f, Screen.height - 120f, 500f, 22f),
                    "inventory: ok | blocked=" + ConnectionManager.InputBlocked + " | " + lookDebug);

            if (ConnectionManager.InputBlocked) return;

            string prompt = null;
            if (lookItem && lookItem.Definition) prompt = interactKey + "  pick up " + lookItem.Definition.displayName;
            else if (lookThing != null) prompt = interactKey + "  " + lookThing.Prompt;
            if (Time.time < messageUntil) prompt = message;

            var centered = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 15 };
            if (!string.IsNullOrEmpty(prompt))
                GUI.Label(new Rect(Screen.width * 0.5f - 200f, Screen.height * 0.5f + 40f, 400f, 28f), prompt, centered);

            // Slot bar along the bottom.
            const float w = 120f, h = 28f, gap = 6f;
            float total = slots.Length * w + (slots.Length - 1) * gap;
            float x = Screen.width * 0.5f - total * 0.5f;
            float y = Screen.height - 44f;
            for (int i = 0; i < slots.Length; i++)
            {
                var rect = new Rect(x + i * (w + gap), y, w, h);
                string name = slots[i] && slots[i].Definition ? slots[i].Definition.displayName : "-";
                string label = (i + 1) + "  " + name;
                if (i == equipped) label = "[ " + label + " ]";
                GUI.Box(rect, GUIContent.none);
                GUI.Label(rect, label, centered);
            }
        }
    }
}
