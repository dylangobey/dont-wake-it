using System;
using System.Collections;
using System.Collections.Generic;
using PurrNet;
using UnityEngine;

namespace DontWakeIt.Items
{
    public enum ItemState
    {
        World = 0,    // lying around, physics runs on the server
        Held = 1,     // in a player's hand, physics runs on that player's machine
        Stowed = 2    // in a player's slot but not in the hand: hidden
    }

    /// <summary>
    /// A physical, networked item (tool). Goes on the item prefab next to a Rigidbody and a Network Rigidbody
    /// (owner authority on).
    ///
    ///  - While nobody owns it the server simulates it. Picking it up gives the player ownership, so they
    ///    simulate it, and it is held to their hand with a joint: it swings, bumps and drags like a real object.
    ///  - Other players see it through the Network Rigidbody, so nothing extra is synced for held items.
    ///  - Knocking it about makes noise (the static Noise event), for the creature to react to later.
    ///  - Jobs build on it: subclass and override OnUseStarted / OnUseEnded, or listen to UseStarted / UseEnded.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Item : NetworkBehaviour
    {
        /// <summary>An item hit something. (item, loudness 0..1+, world position). Raised on the machine simulating it.</summary>
        public static event Action<Item, float, Vector3> Noise;

        [SerializeField] private ItemDefinition definition;
        [Tooltip("Seconds after being let go before it can bump the player who threw it.")]
        [SerializeField] private float releaseIgnoreTime = 0.5f;
        [SerializeField] private float noiseCooldown = 0.12f;

        private SyncVar<int> state = new SyncVar<int>((int)ItemState.World);

        public ItemDefinition Definition => definition;
        public ItemState State => (ItemState)state.value;

        /// <summary>The inventory holding this item. Only set on the machine of the player holding it.</summary>
        public PlayerInventory Holder { get; private set; }
        public bool IsBeingUsed { get; private set; }

        public event Action<PlayerInventory> UseStarted;
        public event Action<PlayerInventory> UseEnded;

        private Rigidbody rb;
        private Collider[] colliders;
        private Renderer[] renderers;
        private FixedJoint joint;
        private PlayerInventory ignoredHolder;
        private float lastNoiseTime;
        private Coroutine restoreRoutine;

        // ---------------------------------------------------------------- Lifecycle

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            colliders = GetComponentsInChildren<Collider>(true);
            renderers = GetComponentsInChildren<Renderer>(true);
        }

        protected override void OnSpawned()
        {
            state.onChanged += OnStateChanged;
            ApplyState();
        }

        protected override void OnDespawned()
        {
            state.onChanged -= OnStateChanged;
            DestroyJoint();
            if (Holder) Holder.Forget(this);
            Holder = null;
        }

        protected override void OnOwnerChanged(PlayerID? oldOwner, PlayerID? newOwner, bool asServer)
        {
            // Lost to someone else (e.g. the holder left): forget our hold.
            if (Holder && newOwner != localPlayer)
            {
                DestroyJoint();
                Holder.Forget(this);
                Holder = null;
            }
        }

        protected override void OnDestroy()
        {
            DestroyJoint();
            base.OnDestroy();
        }

        private void OnStateChanged(int _) => ApplyState();

        /// <summary>Show / hide / enable physics to match the state, on every machine.</summary>
        private void ApplyState()
        {
            var s = State;
            bool stowed = s == ItemState.Stowed;

            foreach (var r in renderers) if (r) r.enabled = !stowed;
            foreach (var c in colliders) if (c) c.enabled = !stowed;

            rb.useGravity = !stowed;
            if (stowed && !rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            // Don't collide with the body holding it (the colliders only exist once it's not stowed).
            if (Holder && !stowed) SetIgnoreHolder(Holder, true);
        }

        private void FixedUpdate()
        {
            // A stowed item follows its owner around (hidden) so it isn't left behind or falling through the world.
            if (!isSpawned || State != ItemState.Stowed || !Holder || !isController) return;
            if (rb.isKinematic) return;

            rb.position = Holder.StowPoint;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        // Everyone except the holder: stick the item to the holder's hand. The item and the body are synced separately
        // and arrive at slightly different times, so trusting the item's own network position makes it trail behind
        // or float away from the hand.
        private void LateUpdate()
        {
            if (!isSpawned || State != ItemState.Held || isController || definition == null) return;

            var holder = PlayerInventory.FindOwnedBy(owner);
            if (!holder) return;

            Transform handTransform = holder.HandTransform;
            Vector3 position = handTransform.position + handTransform.rotation * definition.heldLocalPosition;
            Quaternion rotation = handTransform.rotation * Quaternion.Euler(definition.heldLocalEuler);
            transform.SetPositionAndRotation(position, rotation);
        }

        // ---------------------------------------------------------------- Noise

        private void OnCollisionEnter(Collision c)
        {
            if (!isSpawned || !isController || definition == null || definition.impactNoise <= 0f) return;
            if (Time.time - lastNoiseTime < noiseCooldown) return;

            float speed = c.relativeVelocity.magnitude;
            if (c.contactCount > 0) speed = Mathf.Abs(Vector3.Dot(c.relativeVelocity, c.GetContact(0).normal));
            if (speed < definition.noiseMinSpeed) return;

            float t = Mathf.InverseLerp(definition.noiseMinSpeed, definition.noiseFullSpeed, speed);
            lastNoiseTime = Time.time;
            Noise?.Invoke(this, t * definition.impactNoise,
                c.contactCount > 0 ? c.GetContact(0).point : transform.position);
        }

        // ---------------------------------------------------------------- Use (jobs hook in here)

        /// <summary>Called on the holder's machine when they press the use key.</summary>
        public void BeginUse(PlayerInventory user)
        {
            if (IsBeingUsed) return;
            IsBeingUsed = true;
            OnUseStarted(user);
            UseStarted?.Invoke(user);
        }

        /// <summary>Called when they let go of the use key, or put the item away.</summary>
        public void EndUse(PlayerInventory user)
        {
            if (!IsBeingUsed) return;
            IsBeingUsed = false;
            OnUseEnded(user);
            UseEnded?.Invoke(user);
        }

        protected virtual void OnUseStarted(PlayerInventory user) { }
        protected virtual void OnUseEnded(PlayerInventory user) { }

        // ---------------------------------------------------------------- Held / stowed / dropped (owner machine)

        /// <summary>Put it in the hand: teleport to the hand and join it with a joint.</summary>
        public void LocalHold(PlayerInventory holder, Rigidbody hand)
        {
            Holder = holder;
            DestroyJoint();
            CancelRestore();

            var def = definition;
            Vector3 localPos = def ? def.heldLocalPosition : Vector3.zero;
            Quaternion localRot = def ? Quaternion.Euler(def.heldLocalEuler) : Quaternion.identity;

            rb.position = hand.position + hand.rotation * localPos;
            rb.rotation = hand.rotation * localRot;
            transform.SetPositionAndRotation(rb.position, rb.rotation);
            rb.linearVelocity = hand.linearVelocity;
            rb.angularVelocity = Vector3.zero;

            joint = gameObject.AddComponent<FixedJoint>();
            joint.connectedBody = hand;
            joint.enableCollision = false;

            RequestState(ItemState.Held);
        }

        /// <summary>Remember who is carrying it (stowed) without putting it in the hand.</summary>
        public void SetHolderStowed(PlayerInventory holder)
        {
            Holder = holder;
            DestroyJoint();
            CancelRestore();
        }

        /// <summary>Take it out of the hand but keep it (hidden).</summary>
        public void LocalStow()
        {
            EndUse(Holder);
            DestroyJoint();
            RequestState(ItemState.Stowed);
        }

        /// <summary>Let go of it. The server takes over the physics, starting with the given velocity.</summary>
        public void LocalDrop(Vector3 velocity)
        {
            EndUse(Holder);
            DestroyJoint();

            var oldHolder = Holder;
            Holder = null;
            if (oldHolder) BeginRestore(oldHolder);

            DropRpc(velocity);
        }

        private void DestroyJoint()
        {
            if (joint) Destroy(joint);
            joint = null;
        }

        public void RequestState(ItemState newState) => SetStateRpc((int)newState);

        // ---------------------------------------------------------------- Collision with the holder

        private void SetIgnoreHolder(PlayerInventory holder, bool ignore)
        {
            if (!holder) return;
            foreach (var body in holder.Character.Bodies)
            {
                if (!body) continue;
                foreach (var a in body.GetComponentsInChildren<Collider>())
                {
                    if (!a.enabled || !a.gameObject.activeInHierarchy) continue;
                    foreach (var b in colliders)
                    {
                        if (b && b.enabled && b.gameObject.activeInHierarchy)
                            Physics.IgnoreCollision(a, b, ignore);
                    }
                }
            }
            ignoredHolder = ignore ? holder : null;
        }

        private void BeginRestore(PlayerInventory oldHolder)
        {
            CancelRestore();
            restoreRoutine = StartCoroutine(RestoreCollisions(oldHolder));
        }

        private void CancelRestore()
        {
            if (restoreRoutine != null) StopCoroutine(restoreRoutine);
            restoreRoutine = null;
        }

        private IEnumerator RestoreCollisions(PlayerInventory oldHolder)
        {
            yield return new WaitForSeconds(releaseIgnoreTime);
            SetIgnoreHolder(oldHolder, false);
            restoreRoutine = null;
        }

        // ---------------------------------------------------------------- Server

        /// <summary>Server only: the item has just been picked up by this player.</summary>
        public void ServerClaim(PlayerID player)
        {
            state.value = (int)ItemState.Stowed;
            GiveOwnership(player);
        }

        [ServerRpc]
        private void SetStateRpc(int newState)
        {
            // Only the owner can call this (default RPC rules). A dropped item can't be re-held from a stale message.
            if (State == ItemState.World) return;
            state.value = Mathf.Clamp(newState, (int)ItemState.Held, (int)ItemState.Stowed);
        }

        [ServerRpc]
        private void DropRpc(Vector3 velocity)
        {
            state.value = (int)ItemState.World;
            RemoveOwnership();
            rb.linearVelocity = velocity;
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 2f;
        }
    }
}
