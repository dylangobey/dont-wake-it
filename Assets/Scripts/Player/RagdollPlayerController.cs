using System;
using System.Collections;
using PurrNet;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DontWakeIt.Player
{
    /// <summary>
    /// First-person controller for an <see cref="ActiveRagdollCharacter"/>.
    ///
    /// The physics body stays up and moves because of three things this script applies:
    ///  - Hover:    a stiff spring holds the hips at the height the walk animation wants (so the animated legs
    ///              reach the floor), and pushes back on the surface underfoot.
    ///  - Movement: accelerates the whole body toward a target velocity RELATIVE to the ground, so the
    ///              player is carried by the creature.
    ///  - Balance:  torque turns the hips to match the animated hips (facing + sway).
    /// The character then plays the walk animation scrubbed by actual ground speed, so the feet track the ground.
    ///
    /// Put this on the same GameObject as ActiveRagdollCharacter (an empty "Player" object, NOT on the character).
    /// Optionally add NetworkBones to sync the puppet.
    /// </summary>
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public class RagdollPlayerController : NetworkBehaviour
    {
        [Header("References")]
        [Tooltip("Optional. Uses a Camera found on your character, otherwise one is created.")]
        [SerializeField] private Camera playerCamera;
        [SerializeField] private AudioListener audioListener;
        [Tooltip("Optional. Needs a Player map with Move, Look, Jump, Sprint, Attack (left hand), Interact (right hand).")]
        [SerializeField] private InputActionAsset inputAsset;

        [Header("Testing")]
        [Tooltip("Treat this player as local when no PurrNet session has spawned it (greybox scenes).")]
        [SerializeField] private bool allowOfflineControl = true;

        [Header("Physics")]
        [Tooltip("Ragdolls are much steadier at a higher physics rate. Sets Time.fixedDeltaTime on start.")]
        [SerializeField] private bool overridePhysicsRate = true;
        [SerializeField] private int physicsRate = 100;
        [Tooltip("Gravity multiplier while airborne or limp. ~2 feels like a game; 1 feels floaty.")]
        [SerializeField] private float gravityScale = 2f;

        [Header("Facing")]
        [Tooltip("Body always faces exactly where the camera looks: no strafe turning, no turn lag. Strafing walks the forward animation sideways.")]
        [SerializeField] private bool lockBodyToCamera = true;

        [Header("Movement")]
        [SerializeField] private float walkSpeed = 3.0f;
        [SerializeField] private float sprintMultiplier = 1.5f;
        [Tooltip("m/s^2 when speeding up on the ground.")]
        [SerializeField] private float groundAcceleration = 40f;
        [Tooltip("m/s^2 when slowing down / changing direction on the ground. Higher = stops dead.")]
        [SerializeField] private float groundBraking = 65f;
        [SerializeField] private float airAcceleration = 8f;
        [Tooltip("How fast the body turns to face its heading (deg/s).")]
        [SerializeField] private float turnRate = 600f;
        [Tooltip("How fast the body swings round to face a strafe / backwards direction (deg/s).")]
        [SerializeField] private float strafeTurnRate = 600f;

        [Header("Balance (torque on hips and chest)")]
        [Tooltip("Holds the body upright (tilt). Keep this high: it always wins over turning.")]
        [SerializeField] private float hipsTiltStiffness = 450f;
        [SerializeField] private float hipsTiltDamping = 42f;
        [Tooltip("Turns the hips about the vertical axis. Lower = gentler, steadier turns.")]
        [SerializeField] private float hipsYawStiffness = 200f;
        [SerializeField] private float hipsYawDamping = 26f;
        [Tooltip("Cap on turning torque (rad/s^2) so a fast mouse flick can't whip the body over.")]
        [SerializeField] private float maxYawAcceleration = 300f;
        [Tooltip("Extra tilt stiffness while turning fast (0 = none, 1 = up to double). Stops the body leaning over mid-turn.")]
        [Range(0f, 3f)][SerializeField] private float turnLeanBoost = 1.2f;
        [Tooltip("Share of the hips' turning torque also given to the legs, so they swing round with the hips " +
                 "instead of twisting under them (0 = none).")]
        [Range(0f, 1.5f)][SerializeField] private float legTurnAssist = 0.7f;
        [SerializeField] private float torsoTiltStiffness = 160f;
        [SerializeField] private float torsoTiltDamping = 18f;
        [SerializeField] private float torsoYawStiffness = 90f;
        [SerializeField] private float torsoYawDamping = 12f;

        [Header("Hover (keeps the body standing)")]
        [SerializeField] private LayerMask groundMask = ~0;
        [SerializeField] private float hoverStiffness = 500f;
        [SerializeField] private float hoverDamping = 42f;
        [SerializeField] private float maxHoverAcceleration = 70f;
        [SerializeField] private float probeExtraDistance = 0.3f;
        [SerializeField] private float maxSlopeAngle = 50f;
        [Tooltip("Hips target height as a fraction of the animation's own hip height. Slightly below 1 keeps feet planted.")]
        [Range(0.9f, 1.02f)][SerializeField] private float heightScale = 0.99f;

        [Header("Jump")]
        [SerializeField] private float jumpHeight = 1.1f;
        [SerializeField] private float coyoteTime = 0.12f;
        [SerializeField] private float jumpBuffer = 0.12f;

        [Header("Knockdown")]
        [Tooltip("Relative collision speed (m/s) above which the body goes limp.")]
        [SerializeField] private float knockdownSpeed = 7f;
        [SerializeField] private float knockdownDuration = 1.5f;
        [SerializeField] private float knockdownCooldown = 0.6f;
        [Tooltip("How fast motors come back on after the limp period (1/s).")]
        [SerializeField] private float recoveryRate = 1.6f;
        [Tooltip("Draws a readout of grounded / limp / speed / animation state in the top-left of the Game view.")]
        [SerializeField] private bool showDebug;
        [Tooltip("Logs where the body is and what is under it for a few seconds after you take control. Handy if a player sinks or falls.")]
        [SerializeField] private bool diagnoseSpawn = true;
        [Tooltip("If the hips fall this far below where the player started, put them back at the start.")]
        [SerializeField] private float voidDepth = 25f;
        [Tooltip("Logs what caused each knockdown to the Console.")]
        [SerializeField] private bool logKnockdowns = true;

        [Header("Look & camera")]
        [SerializeField] private float lookSensitivity = 0.12f;
        [SerializeField] private float maxPitch = 80f;
        [SerializeField] private Vector3 cameraOffset = new Vector3(0f, 0.04f, 0.13f);
        [Tooltip("Higher = camera sticks harder to the head. Lower = smoother.")]
        [SerializeField] private float cameraFollow = 24f;
        [Tooltip("0 = camera rides the head (shaky), 1 = rides a steady point above the hips.")]
        [Range(0f, 1f)][SerializeField] private float cameraStability = 0.6f;
        [Tooltip("How much the camera takes the head's tumbling orientation while limp (0..1).")]
        [Range(0f, 1f)][SerializeField] private float limpCameraTumble = 0.85f;

        // ---------------------------------------------------------------- Public API

        /// <summary>Fired on the owner for hard impacts. Arg: relative speed. For the Stir system.</summary>
        public event Action<float> OnImpact;
        public event Action OnKnockedDown;

        public bool IsGrounded { get; private set; }
        public bool IsLimp => limp > 0.5f;
        public float Limp => limp;
        public Vector3 GroundVelocity => ground.Velocity;
        /// <summary>Hips velocity relative to the surface underfoot. Use for noise (steps, scuffs).</summary>
        public Vector3 RelativeVelocity => character && character.Hips ? character.Hips.linearVelocity - ground.Velocity : Vector3.zero;

        /// <summary>Shove the whole body and optionally knock it limp (creature twitches, explosions, other players).</summary>
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
        private float spawnHeight;
        private bool diagStarted;
        private float localSince = -1f;

        // Only the owning player controls a body. Before ownership arrives a freshly spawned body is nobody's,
        // so neither the host nor the joiner simulates it. Offline test control only applies with no session.
        private bool IsLocal => isSpawned
            ? isOwner
            : allowOfflineControl && (NetworkManager.main == null || NetworkManager.main.isOffline);
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
            if (!character.IsReady)
            {
                Debug.LogError("RagdollPlayerController: the character failed to set up, so this player can't move. " +
                               "Look for an earlier error from ActiveRagdollCharacter (usually 'Animated Rig' not assigned " +
                               "or a body part not found).", this);
                enabled = false;
                return;
            }

            lookYaw = facingYaw = character.RestFacingYaw;
            spawnTime = Time.time;
            spawnHeight = character.Hips.position.y;
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

                if (localSince < 0f)
                {
                    localSince = Time.time;
                    // The physics bodies can be left where they were created if PurrNet repositions the player
                    // right after spawning it. Seat them at the spawn point before simulating anything.
                    character.ResetPoseToRest();
                }

                if (diagnoseSpawn && !diagStarted)
                {
                    diagStarted = true;
                    StartCoroutine(DiagnoseSpawn());
                }
            }
            else
            {
                DisableInput();
            }

            if (!local)
            {
                // Other players' copies must never render or listen: turn off any camera that came with the character.
                var rigCamera = character.RigCamera;
                if (rigCamera)
                {
                    rigCamera.enabled = false;
                    var rigListener = rigCamera.GetComponent<AudioListener>();
                    if (rigListener) rigListener.enabled = false;
                }
            }

            if (playerCamera) playerCamera.enabled = local;
            if (audioListener) audioListener.enabled = local;
            if (local)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
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

            // The pause menu (ConnectionManager) blocks player input while it is open.
            bool blocked = DontWakeIt.Networking.ConnectionManager.InputBlocked;
            moveInput = blocked ? Vector2.zero : Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f);
            sprintHeld = !blocked && sprintAction.IsPressed();
            grabLeft = !blocked && grabLeftAction.IsPressed();
            grabRight = !blocked && grabRightAction.IsPressed();
            if (blocked) return;

            Vector2 look = lookAction.ReadValue<Vector2>() * lookSensitivity;
            lookYaw += look.x;
            pitch = Mathf.Clamp(pitch - look.y, -maxPitch, maxPitch);
        }

        // ---------------------------------------------------------------- Physics step

        private void FixedUpdate()
        {
            if (!IsLocal || !character.IsReady) return;
            float dt = Time.fixedDeltaTime;

            // For the first moments after taking control the body must still be at its spawn point. If it is
            // far from it (a stale physics pose), seat it there again.
            if (localSince >= 0f && Time.time - localSince < 2f &&
                (character.Hips.position - character.RestHipsWorld).sqrMagnitude > 4f)
            {
                Debug.LogWarning("Player body was away from its spawn point; re-seating it.", this);
                character.ResetPoseToRest();
                smoothedAnimHeight = -1f;
                return;
            }

            // Fell out of the world: put the player back at the start.
            if (character.Hips.position.y < spawnHeight - voidDepth)
            {
                Debug.LogWarning("Player fell out of the world; resetting to the start.", this);
                character.ResetPoseToRest();
                limp = 0f;
                limpTimer = 0f;
                smoothedAnimHeight = -1f;
                return;
            }

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
            // The chest and head turn to the camera immediately, also making up for the hips still catching up.
            float hipsLag = Mathf.DeltaAngle(YawOf(character.Hips.rotation), YawOf(character.AnimatedHipsRotation));
            float upperBodyYaw = Mathf.DeltaAngle(facingYaw, lookYaw) + hipsLag;
            character.Evaluate(dt, signedSpeed, IsGrounded, facingYaw, upperBodyYaw, lookDir, reachL, reachR, limp);

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

        private static float YawOf(Quaternion q)
        {
            Vector3 f = q * Vector3.forward;
            f.y = 0f;
            return f.sqrMagnitude < 1e-6f ? 0f : Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
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

            if (lockBodyToCamera)
            {
                moveOffset = 0f;
                // The hips turn at a limited rate (steadier); the chest and head track the camera instantly.
                facingYaw = Mathf.MoveTowardsAngle(facingYaw, lookYaw, turnRate * dt);
                if (hasInput) moveReverse = moveInput.y < -0.1f;   // only S / back plays the walk in reverse
                signedSpeed = moveReverse ? -speed : speed;
                return;
            }

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

        /// <summary>Hold hips and chest upright and turn them to the animated orientation (facing + sway).</summary>
        private void ApplyBalance(float support)
        {
            if (support <= 0.001f) return;

            // The faster the body is spinning about the vertical, the harder it is to stay upright.
            float turnSpeed = Mathf.Abs(character.Hips.angularVelocity.y);
            float boost = 1f + turnLeanBoost * Mathf.Clamp01(turnSpeed / 6f);

            float hipsYaw = Balance(character.Hips, character.AnimatedHipsRotation,
                hipsTiltStiffness * boost, hipsTiltDamping * boost, hipsYawStiffness, hipsYawDamping, support);

            Quaternion torsoTarget = Quaternion.Euler(0f, character.TorsoTwistDegrees, 0f) * character.AnimatedRotation(Bone.Torso);
            Balance(character.Body(Bone.Torso), torsoTarget,
                torsoTiltStiffness * boost, torsoTiltDamping * boost, torsoYawStiffness, torsoYawDamping, support);

            // Legs turn with the hips.
            if (legTurnAssist > 0f && Mathf.Abs(hipsYaw) > 0.01f)
            {
                Vector3 assist = Vector3.up * (hipsYaw * legTurnAssist);
                character.Body(Bone.LeftThigh).AddTorque(assist, ForceMode.Acceleration);
                character.Body(Bone.RightThigh).AddTorque(assist, ForceMode.Acceleration);
                character.Body(Bone.LeftLeg).AddTorque(assist, ForceMode.Acceleration);
                character.Body(Bone.RightLeg).AddTorque(assist, ForceMode.Acceleration);
            }
        }

        /// <summary>
        /// Torque toward a target orientation, split in two: tilt (lean over) is corrected stiffly and uncapped,
        /// yaw (turning) is gentler and capped. Turning hard can then never overpower staying upright.
        /// </summary>
        /// <summary>Returns the turning acceleration it applied (rad/s^2).</summary>
        private float Balance(Rigidbody rb, Quaternion target, float tiltKp, float tiltKd, float yawKp, float yawKd, float scale)
        {
            Quaternion delta = target * Quaternion.Inverse(rb.rotation);
            delta.ToAngleAxis(out float angleDeg, out Vector3 axis);
            if (angleDeg > 180f) angleDeg -= 360f;
            if (float.IsNaN(axis.x) || float.IsInfinity(axis.x) || axis.sqrMagnitude < 1e-6f) return 0f;

            Vector3 error = axis.normalized * (angleDeg * Mathf.Deg2Rad);   // world-space rotation vector
            Vector3 w = rb.angularVelocity;

            float yawError = error.y;
            Vector3 tiltError = new Vector3(error.x, 0f, error.z);
            Vector3 tiltVel = new Vector3(w.x, 0f, w.z);

            Vector3 tiltTorque = tiltError * tiltKp - tiltVel * tiltKd;
            float yawTorque = Mathf.Clamp(yawError * yawKp - w.y * yawKd, -maxYawAcceleration, maxYawAcceleration);

            rb.AddTorque((tiltTorque + Vector3.up * yawTorque) * scale, ForceMode.Acceleration);
            return yawTorque * scale;
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

            if (logKnockdowns)
                Debug.Log("Knockdown: " + part + " hit '" + c.collider.name + "' at " + relativeSpeed.ToString("F1") + " m/s", this);
            OnImpact?.Invoke(relativeSpeed);
            Knockdown(knockdownDuration * Mathf.Clamp(relativeSpeed / knockdownSpeed, 1f, 2f));
        }

        /// <summary>Logs position, what is under the hips and the body set-up for a few seconds after taking control.</summary>
        private IEnumerator DiagnoseSpawn()
        {
            for (int i = 0; i < 10; i++)
            {
                yield return new WaitForSeconds(0.5f);
                if (!character || !character.IsReady) yield break;

                Vector3 p = character.Hips.position;
                string groundInfo = "NONE within 50m";
                float nearest = float.MaxValue;
                foreach (var hit in Physics.RaycastAll(p, Vector3.down, 50f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider.transform.IsChildOf(transform)) continue;
                    if (hit.distance < nearest)
                    {
                        nearest = hit.distance;
                        var hipsCollider = character.Hips.GetComponent<Collider>();
                        bool ignoreLayer = Physics.GetIgnoreLayerCollision(character.Hips.gameObject.layer, hit.collider.gameObject.layer);
                        bool ignorePair = hipsCollider && Physics.GetIgnoreCollision(hipsCollider, hit.collider);
                        groundInfo = "'" + hit.collider.name + "' " + hit.distance.ToString("F2") + "m below" +
                                     " (layer=" + LayerMask.LayerToName(hit.collider.gameObject.layer) +
                                     " ignoreLayer=" + ignoreLayer + " ignorePair=" + ignorePair + ")";
                    }
                }

                Debug.Log("[PlayerDiag " + i + "] hips=" + p.ToString("F2") +
                          " vy=" + character.Hips.linearVelocity.y.ToString("F1") +
                          " ground=" + groundInfo + " | " + character.DebugBodies() +
                          " | grounded=" + IsGrounded + " limp=" + limp.ToString("F2") +
                          " | fixedDt=" + Time.fixedDeltaTime + " physicsMode=" + Physics.simulationMode, this);
            }
        }

        private void OnGUI()
        {
            // Only the local player draws it, otherwise every player's text piles up in the same corner.
            if (!showDebug || !character || !IsLocal) return;
            if (!character.IsReady)
            {
                GUI.Label(new Rect(10f, 10f, 900f, 40f), "CHARACTER NOT READY - see the Console for the error above.");
                return;
            }

            GUI.Label(new Rect(10f, 10f, 1000f, 200f),
                "spawned=" + isSpawned + "  owner=" + isOwner + "  local=" + IsLocal +
                "  inputBlocked=" + DontWakeIt.Networking.ConnectionManager.InputBlocked + "\n" +
                "grounded=" + IsGrounded + "  limp=" + limp.ToString("F2") + "  speed=" + RelativeVelocity.magnitude.ToString("F2") +
                "  hipsH=" + groundHeight.ToString("F2") + "/" + HoverTarget.ToString("F2") + "\n" +
                character.DebugInfo + "\n" + character.DebugJoints());
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
