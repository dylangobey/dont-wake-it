using PurrNet;
using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DontWakeIt.Player
{
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public class RagdollPlayerController : NetworkBehaviour
    {
        [Header("References")]
        [Tooltip("Optional. Select Camera on Character, otherwise one will be created")]
        [SerializeField] private Camera playerCamera;
        [SerializeField] private AudioListener audioListener;
        [SerializeField] private InputActionAsset inputAsset;

        [Header("Testing")]
        [SerializeField] private bool allowOfflineControl = true;

        [Header("Physics")]
        [SerializeField] private bool overridePhysicsRate = true;
        [SerializeField] private int physicsRate = 100;
        [SerializeField] private float gravityScale = 2f;

        [Header("Movement")]
        [SerializeField] private float walkSpeed = 3.0f;
        [SerializeField] private float sprintMultiplier = 1.5f;
        [SerializeField] private float groundAcceleration = 40f;
        [SerializeField] private float groundBraking = 65f;
        [SerializeField] private float airAcceleration = 8f;
        [SerializeField] private float turnRate = 540f;
        [SerializeField] private float strafeTurnRate = 480f;

        [Header("Balance")]
        [SerializeField] private float hipsStiffness = 260f;
        [SerializeField] private float hipsDamping = 28f;

        [Header("Hover")]
        [SerializeField] private LayerMask groundMask = ~0;
        [SerializeField] private float hoverStiffness = 500f;
        [SerializeField] private float hoverDamping = 42f;
        [SerializeField] private float maxHoverAcceleration = 70f;
        [SerializeField] private float probeExtraDistance = 0.3f;
        [SerializeField] private float maxSlopeAngle = 50f;
        [Range(0.9f, 1.02f)][SerializeField] private float heightScale = 0.99f;

        [Header("Jump")]
        [SerializeField] private float jumpHeight = 1.1f;
        [SerializeField] private float coyoteTime = 0.12f;
        [SerializeField] private float jumpBuffer = 0.12f;

        [Header("Knockdown")]
        [SerializeField] private float knockdownSpeed = 7f;
        [SerializeField] private float knockdownDuration = 1.5f;
        [SerializeField] private float knockdownCooldown = 0.6f;
        [SerializeField] private float recoveryRate = 1.6f;

        [Header("Look & Camera")]
        [SerializeField] private float lookSensitivity = 0.12f;
        [SerializeField] private float maxPitch = 80f;
        [SerializeField] private Vector3 cameraOffset = new Vector3(0f, 0.04f, 0.13f);
        [SerializeField] private float cameraFollow = 24f;
        [Range(0f, 1f)]
        [SerializeField] private float cameraStability = 0.6f;
        [Range(0f, 1f)][SerializeField] private float limpCameraTumble = 0.85f;

        public event Action<float> OnImpact;
        public event Action OnKnockedDown;

        public bool IsGrounded { get; private set; }
        public bool IsLimp => limp > 0.5f;
        public float Limp => limp;
        public Vector3 GroundVelocity => ground.Velocity;
        public Vector3 RelativeVelocity => character && character.Hips ? character.Hips.linearVelocity - ground.Velocity : Vector3.zero;

        public void Shove(Vector3 velocityChange, float limpSeconds = 0f)
        {
            if (!IsLocal) return;
            foreach (var rb in character.Bodies) rb.AddForce(velocityChange, ForceMode.VelocityChange);
            if (limpSeconds > 0f) Knockdown(limpSeconds);
        }

        public void Knockdown(float seconds)
        {
            if (!IsLocal) return;
            if (limpTimer <= 0f && limp < 0.5f) OnKnockedDown?.Invoke();
            limpTimer = Mathf.Max(limpTimer, seconds);
            limp = 1f;
            lastKnockdownTime = Time.time;
        }

        // ---------------------------------------------------------------- State

        private ActiveRagdollCharacter character;
        private readonly GroundTracker ground = new GroundTracker();
        private readonly RaycastHit[] probeHits = new RaycastHit[24];

        private InputAction moveAction, lookAction, jumpAction, sprintAction, grabLeftAction, grabRightAction;
        private bool ownsActions;

        private Vector2 moveInput;
        private bool sprintHeld, grabLeft, grabRight;
        private float lookYaw, pitch, facingYaw, moveOffset;
        private bool moveReverse;
        private float reachL, reachR;

        private float limp, limpTimer, lastKnockdownTime = float.NegativeInfinity;
        private float lastGroundedTime = float.NegativeInfinity;
        private float lastJumpPressedTime = float.NegativeInfinity;
        private float lastJumpTime = float.NegativeInfinity;
        private float spawnTime;
        private float groundHeight;
        private float smoothedAnimHeight = -1f;

        private bool IsLocal => isSpawned ? isController : allowOfflineControl;
        private bool JumpedRecently => Time.time - lastJumpTime < 0.25f;
        private float EffectiveGravity => Mathf.Abs(Physics.gravity.y) * gravityScale;

        // ---------------------------------------------------------------- Lifecycle

        private void Awake()
        {
            if (overridePhysicsRate && physicsRate > 0) Time.fixedDeltaTime = 1f / physicsRate;
            character = GetComponent<ActiveRagdollCharacter>();
        }

        protected override void OnSpawned()
        {
            base.OnSpawned();
            RefreshLocalState();
        }

        protected override void OnOwnerChanged(PlayerID? oldOwner, PlayerID? newOwner, bool asServer)
        {
            base.OnOwnerChanged(oldOwner, newOwner, asServer);
            RefreshLocalState();
        }

        private void Start()
        {
            if (!character.IsReady) { enabled = false; return; }

            // Sync the puppet's parts over PurrNet if NetworkBones is present.
            var bones = GetComponent<NetworkBones>();
            if (bones) bones.extraBones = character.PuppetTransforms;

            lookYaw = facingYaw = character.Hips.transform.eulerAngles.y;
            spawnTime = Time.time;
            character.PartCollision += OnPartCollision;

            if (!isSpawned) RefreshLocalState();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (character) character.PartCollision -= OnPartCollision;
            DisableInput();
        }

        private void RefreshLocalState()
        {
            if (!character || !character.IsReady) return;
            bool local = IsLocal;
            character.SetSimulated(local);
            character.SetBoneVisible(Bone.Head, !local); // don't look out of the inside of your own head

            if (local)
            {
                EnsureCamera();
                EnableInput();
            }
            else
            {
                DisableInput();
            }

            if (playerCamera) playerCamera.enabled = local;
            if (audioListener) audioListener.enabled = local;
            Cursor.lockState = local ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !local;
        }

        private void EnsureCamera()
        {
            if (!playerCamera) playerCamera = character.RigCamera;
            if (!playerCamera)
            {
                var go = new GameObject("PlayerCamera");
                go.transform.SetParent(transform, false);
                playerCamera = go.AddComponent<Camera>();
                playerCamera.nearClipPlane = 0.05f;
            }
            if (!audioListener) audioListener = playerCamera.GetComponent<AudioListener>();
            if (!audioListener) audioListener = playerCamera.gameObject.AddComponent<AudioListener>();
        }

        // ---------------------------------------------------------------- Input

        private void EnableInput()
        {
            if (moveAction != null) return;

            if (inputAsset)
            {
                var map = inputAsset.FindActionMap("Player", true);
                moveAction = map.FindAction("Move", true);
                lookAction = map.FindAction("Look", true);
                jumpAction = map.FindAction("Jump", true);
                sprintAction = map.FindAction("Sprint", true);
                grabLeftAction = map.FindAction("Attack", true);
                grabRightAction = map.FindAction("Interact", true);
                ownsActions = false;
            }
            else
            {
                moveAction = new InputAction("Move", InputActionType.Value);
                moveAction.AddCompositeBinding("2DVector")
                    .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
                    .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
                moveAction.AddBinding("<Gamepad>/leftStick");

                lookAction = new InputAction("Look", InputActionType.Value, "<Mouse>/delta");
                lookAction.AddBinding("<Gamepad>/rightStick");

                jumpAction = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
                jumpAction.AddBinding("<Gamepad>/buttonSouth");
                sprintAction = new InputAction("Sprint", InputActionType.Button, "<Keyboard>/leftShift");
                sprintAction.AddBinding("<Gamepad>/leftStickPress");
                grabLeftAction = new InputAction("GrabLeft", InputActionType.Button, "<Mouse>/leftButton");
                grabLeftAction.AddBinding("<Gamepad>/leftTrigger");
                grabRightAction = new InputAction("GrabRight", InputActionType.Button, "<Mouse>/rightButton");
                grabRightAction.AddBinding("<Gamepad>/rightTrigger");
                ownsActions = true;
            }

            foreach (var a in AllActions()) a.Enable();
            jumpAction.performed += OnJumpPerformed;
        }

        private InputAction[] AllActions() => new[]
            { moveAction, lookAction, jumpAction, sprintAction, grabLeftAction, grabRightAction };

        private void DisableInput()
        {
            if (moveAction == null) return;
            jumpAction.performed -= OnJumpPerformed;
            foreach (var a in AllActions())
            {
                if (ownsActions) a.Dispose(); else a.Disable();
            }
            moveAction = lookAction = jumpAction = sprintAction = grabLeftAction = grabRightAction = null;
            moveInput = Vector2.zero;
            sprintHeld = grabLeft = grabRight = false;
        }

        private void OnJumpPerformed(InputAction.CallbackContext _) => lastJumpPressedTime = Time.time;

        private void Update()
        {
            if (!character || !character.IsReady) return;
            if (!IsLocal)
            {
                if (moveAction != null) RefreshLocalState(); // lost control
                return;
            }
            if (moveAction == null) { RefreshLocalState(); return; }

            moveInput = Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f);
            sprintHeld = sprintAction.IsPressed();
            grabLeft = grabLeftAction.IsPressed();
            grabRight = grabRightAction.IsPressed();

            Vector2 look = lookAction.ReadValue<Vector2>() * lookSensitivity;
            lookYaw += look.x;
            pitch = Mathf.Clamp(pitch - look.y, -maxPitch, maxPitch);
        }

        // ---------------------------------------------------------------- Physics step

        private void FixedUpdate()
        {
            if (!IsLocal || !character.IsReady) return;
            float dt = Time.fixedDeltaTime;

            ProbeGround(dt);
            UpdateLimp(dt);
            float support = 1f - limp;      // 1 = fully in control, 0 = ragdoll

            // Carry the camera/facing with a rolling creature.
            if (IsGrounded) { lookYaw += ground.YawDelta; facingYaw += ground.YawDelta; }

            UpdateFacing(dt, out float signedSpeed);

            reachL = Mathf.MoveTowards(reachL, grabLeft ? 1f : 0f, 7f * dt);
            reachR = Mathf.MoveTowards(reachR, grabRight ? 1f : 0f, 7f * dt);
            Vector3 lookDir = Quaternion.Euler(pitch, lookYaw, 0f) * Vector3.forward;

            // Play the animation for this step and push it into the joint motors.
            character.Evaluate(dt, signedSpeed, IsGrounded, facingYaw, lookDir, reachL, reachR, limp);

            float animHeight = character.AnimatedHipsHeight;
            smoothedAnimHeight = smoothedAnimHeight < 0f ? animHeight : Mathf.Lerp(smoothedAnimHeight, animHeight, 1f - Mathf.Exp(-25f * dt));

            ApplyExtraGravity();
            ApplyBalance(support);
            if (IsGrounded) ApplyHover(support);
            ApplyMovement(dt, support);
            HandleJump(support);

            character.SetGrip(BodySide.Left, grabLeft);
            character.SetGrip(BodySide.Right, grabRight);
        }

        /// <summary>
        /// The body faces where you look, but swings round toward the direction you are strafing, and walks the
        /// animation in reverse when you back up.
        /// </summary>
        private void UpdateFacing(float dt, out float signedSpeed)
        {
            Vector3 planar = RelativeVelocity;
            planar.y = 0f;
            float speed = planar.magnitude;

            bool hasInput = moveInput.sqrMagnitude > 0.01f;
            float targetOffset = 0f;
            if (hasInput && IsGrounded)
            {
                float a = Mathf.Atan2(moveInput.x, moveInput.y) * Mathf.Rad2Deg;   // 0 = forward, +90 = right
                bool reverse = Mathf.Abs(a) > 100f;
                moveReverse = reverse;
                targetOffset = reverse ? a - Mathf.Sign(a) * 180f : a;
            }

            moveOffset = Mathf.MoveTowardsAngle(moveOffset, targetOffset, strafeTurnRate * dt);
            facingYaw = Mathf.MoveTowardsAngle(facingYaw, lookYaw + moveOffset, turnRate * dt);

            signedSpeed = moveReverse ? -speed : speed;
        }

        // ---- Ground

        /// <summary>Hover target: the animation's hip height plus the sag a spring of this stiffness has under gravity.</summary>
        private float HoverTarget => smoothedAnimHeight * heightScale + Mathf.Abs(Physics.gravity.y) / hoverStiffness;

        private void ProbeGround(float dt)
        {
            Vector3 origin = character.Hips.position;
            float radius = character.HipsRadius * 0.9f;
            float target = smoothedAnimHeight > 0f ? HoverTarget : character.AnimatedHipsHeight;
            float distance = target + probeExtraDistance;
            int n = Physics.SphereCastNonAlloc(origin, radius, Vector3.down, probeHits, distance,
                groundMask, QueryTriggerInteraction.Ignore);

            bool found = false;
            RaycastHit best = default;
            float bestDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = probeHits[i];
                if (h.collider.transform.IsChildOf(transform)) continue;
                if (h.distance <= 0f) continue; // started inside something
                if (h.distance < bestDist) { bestDist = h.distance; best = h; found = true; }
            }

            bool walkable = found && Vector3.Angle(best.normal, Vector3.up) <= maxSlopeAngle;
            if (found)
            {
                ground.Sample(best, dt);
                groundHeight = origin.y - best.point.y;
            }
            else
            {
                // Keep the last surface velocity briefly so jumps/coyote time stay relative to the creature.
                ground.Clear(Time.time - lastGroundedTime <= coyoteTime);
            }

            IsGrounded = walkable && !JumpedRecently && found && smoothedAnimHeight > 0f
                         && groundHeight <= HoverTarget + 0.2f;
            if (IsGrounded) lastGroundedTime = Time.time;
        }

        private void UpdateLimp(float dt)
        {
            if (limpTimer > 0f)
            {
                limpTimer -= dt;
                limp = 1f;
            }
            else
            {
                limp = Mathf.MoveTowards(limp, 0f, recoveryRate * dt);
            }
        }

        // ---- Forces

        /// <summary>Snappier, heavier gravity whenever the body is not being held up by the hover spring.</summary>
        private void ApplyExtraGravity()
        {
            float extra = (gravityScale - 1f) * Mathf.Abs(Physics.gravity.y);
            if (extra <= 0f) return;
            float amount = IsGrounded ? limp : 1f;   // grounded and in control: the hover spring handles weight
            if (amount <= 0.001f) return;
            foreach (var rb in character.Bodies) rb.AddForce(Vector3.down * (extra * amount), ForceMode.Acceleration);
        }

        /// <summary>Turn the hips to the animated hips' orientation: facing, plus whatever sway the animation has.</summary>
        private void ApplyBalance(float support)
        {
            if (support <= 0.001f) return;
            var hips = character.Hips;
            Quaternion target = character.AnimatedHipsRotation;

            Quaternion delta = target * Quaternion.Inverse(hips.rotation);
            delta.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            if (float.IsNaN(axis.x) || float.IsInfinity(axis.x) || axis.sqrMagnitude < 1e-6f) return;

            Vector3 torque = axis.normalized * (angle * Mathf.Deg2Rad * hipsStiffness) - hips.angularVelocity * hipsDamping;
            hips.AddTorque(torque * support, ForceMode.Acceleration);
        }

        private void ApplyHover(float support)
        {
            if (support <= 0.01f) return;

            float compress = HoverTarget - groundHeight;     // > 0 when the body sits too low
            if (compress <= 0f) return;

            float relVy = character.Hips.linearVelocity.y - ground.Velocity.y;
            float accel = Mathf.Clamp(hoverStiffness * compress - hoverDamping * relVy, 0f, maxHoverAcceleration);
            accel *= support;

            // Spread over every body so no single joint has to carry the whole weight.
            foreach (var rb in character.Bodies) rb.AddForce(Vector3.up * accel, ForceMode.Acceleration);

            // Newton's third law: push down on the thing we're standing on if it can move.
            var groundBody = ground.Collider ? ground.Collider.attachedRigidbody : null;
            if (groundBody && !groundBody.isKinematic)
                groundBody.AddForceAtPosition(Vector3.down * (accel * character.TotalMass), character.Hips.position);
        }

        private void ApplyMovement(float dt, float support)
        {
            if (support <= 0.01f) return;

            float speed = walkSpeed;
            if (sprintHeld && moveInput.y > 0.1f) speed *= sprintMultiplier;

            Vector3 wish = Quaternion.Euler(0f, lookYaw, 0f) * new Vector3(moveInput.x, 0f, moveInput.y);
            Vector3 desired = wish * speed;
            bool hasInput = moveInput.sqrMagnitude > 0.01f;

            Vector3 current = character.Hips.linearVelocity - ground.Velocity;
            current.y = 0f;
            Vector3 delta = desired - current;
            if (!IsGrounded && !hasInput) return;

            float maxAccel;
            if (!IsGrounded) maxAccel = airAcceleration;
            else if (!hasInput || Vector3.Dot(desired, current) < 0.5f * current.sqrMagnitude) maxAccel = groundBraking;
            else maxAccel = groundAcceleration;

            Vector3 accel = Vector3.ClampMagnitude(delta / dt, maxAccel) * support;
            foreach (var rb in character.Bodies) rb.AddForce(accel, ForceMode.Acceleration);

            var groundBody = ground.Collider ? ground.Collider.attachedRigidbody : null;
            if (IsGrounded && groundBody && !groundBody.isKinematic)
                groundBody.AddForceAtPosition(-accel * character.TotalMass, character.Hips.position);
        }

        private void HandleJump(float support)
        {
            bool buffered = Time.time - lastJumpPressedTime <= jumpBuffer;
            bool canJump = IsGrounded || Time.time - lastGroundedTime <= coyoteTime;
            if (!buffered || !canJump || support < 0.6f) return;

            float jumpSpeed = Mathf.Sqrt(2f * EffectiveGravity * jumpHeight);
            foreach (var rb in character.Bodies)
            {
                Vector3 v = rb.linearVelocity;
                v.y = Mathf.Max(v.y, ground.Velocity.y) + jumpSpeed;
                rb.linearVelocity = v;
            }

            lastJumpPressedTime = float.NegativeInfinity;
            lastGroundedTime = float.NegativeInfinity;
            lastJumpTime = Time.time;
            IsGrounded = false;
        }

        // ---- Impacts

        private void OnPartCollision(Bone part, float relativeSpeed, Collision c)
        {
            if (!IsLocal || Time.time - spawnTime < 1f) return; // ignore the settling drop at spawn
            if (relativeSpeed < knockdownSpeed) return;
            if (Time.time - lastKnockdownTime < knockdownCooldown) return;

            OnImpact?.Invoke(relativeSpeed);
            Knockdown(knockdownDuration * Mathf.Clamp(relativeSpeed / knockdownSpeed, 1f, 2f));
        }

        // ---------------------------------------------------------------- Camera

        private void LateUpdate()
        {
            if (!IsLocal || !playerCamera || !character.IsReady) return;

            Quaternion yawRot = Quaternion.Euler(0f, lookYaw, 0f);
            // Blend between the (shaky) head and a steady point above the hips for a calmer view.
            Vector3 hipsPos = character.Hips.position;
            Vector3 headPos = character.Head.position;
            Vector3 steady = new Vector3(hipsPos.x, headPos.y, hipsPos.z);
            Vector3 anchor = Vector3.Lerp(headPos, steady, cameraStability);
            Vector3 target = anchor + yawRot * cameraOffset;

            Transform cam = playerCamera.transform;
            cam.position = Vector3.Lerp(cam.position, target, 1f - Mathf.Exp(-cameraFollow * Time.deltaTime));

            Quaternion look = Quaternion.Euler(pitch, lookYaw, 0f);
            cam.rotation = limp > 0.01f
                ? Quaternion.Slerp(look, character.Head.rotation, limp * limpCameraTumble)
                : look;
        }
    }
}