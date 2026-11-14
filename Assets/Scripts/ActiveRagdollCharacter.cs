using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace DontWakeIt.Player
{
    /// <summary>The 16 body parts, named exactly like the parts in the character's hierarchy.</summary>
    public enum Bone
    {
        Hips, Stomach, Torso, Head,
        LeftShoulder, RightShoulder, LeftArm, RightArm, LeftHand, RightHand,
        LeftThigh, RightThigh, LeftLeg, RightLeg, LeftFoot, RightFoot
    }

    public enum BodySide { Left = 0, Right = 1 }

    /// <summary>
    /// Animation-driven active ragdoll.
    ///
    /// You give it your existing character (the one with the Animator and the walk animation). It:
    ///  1. keeps that character as the hidden "animated rig" that only plays the animation,
    ///  2. clones it into a "physics puppet": a Rigidbody on every part, joined with ConfigurableJoints,
    ///  3. every physics step, reads how the animation has rotated each part relative to its parent and
    ///     feeds that to the joint motors, so physics tries to perform the animation.
    ///
    /// Motor strength is the dial between "plays the animation" (strong) and "floppy ragdoll" (weak).
    /// The animation is played from clips through a PlayableGraph so the controller can scrub it by
    /// ground speed (feet match the ground), reverse it for walking backwards, and freeze it in the air.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class ActiveRagdollCharacter : MonoBehaviour
    {
        [Header("Source character (has the Animator + walk animation)")]
        [Tooltip("Root of your existing character, the object whose children are Head, Torso, Hips, LeftArm...")]
        [SerializeField] private Transform animatedRig;
        [Tooltip("Optional. Found automatically from the Animator's controller if left empty.")]
        [SerializeField] private AnimationClip walkClip;
        [Tooltip("Optional. If empty the walk clip is frozen at Idle Normalized Time when standing still.")]
        [SerializeField] private AnimationClip idleClip;
        [Tooltip("Degrees. Set 180 if your character faces -Z.")]
        [SerializeField] private float forwardYawOffset;
        [Tooltip("Debug: show the animated rig overlapped on the physics puppet so you can see how well physics follows it.")]
        [SerializeField] private bool showAnimatedRig;

        [Header("Walk animation")]
        [Tooltip("Metres the character travels during ONE full loop of the walk clip. Tune until feet stop sliding.")]
        [SerializeField] private float metersPerCycle = 1.6f;
        [Range(0f, 1f)][SerializeField] private float idleNormalizedTime = 0f;
        [Range(0f, 1f)][SerializeField] private float airNormalizedTime = 0.25f;
        [Tooltip("Cycles per second used to ease the walk into its standing / airborne pose.")]
        [SerializeField] private float settleRate = 2f;

        [Header("Physics")]
        [SerializeField] private float totalMass = 70f;
        [Tooltip("Global multiplier for how hard the joints hold the animated pose. Lower = floppier.")]
        [SerializeField] private float jointStrength = 1f;
        [Tooltip("Motor strength multiplier when completely limp (0..1).")]
        [Range(0.005f, 0.5f)][SerializeField] private float limpStrength = 0.03f;
        [SerializeField] private float friction = 0.25f;
        [SerializeField] private int solverIterations = 14;
        [SerializeField] private int solverVelocityIterations = 4;

        [Header("Grip")]
        [SerializeField] private float gripBreakForce = 6000f;
        [Tooltip("A hand can only grab if it touched something this recently (seconds).")]
        [SerializeField] private float gripContactWindow = 0.12f;

        // ---------------------------------------------------------------- Tables

        private const int BoneCount = 16;

        private static readonly string[] Names =
        {
            "Hips", "Stomach", "Torso", "Head",
            "LeftShoulder", "RightShoulder", "LeftArm", "RightArm", "LeftHand", "RightHand",
            "LeftThigh", "RightThigh", "LeftLeg", "RightLeg", "LeftFoot", "RightFoot"
        };

        // Physical parent of each bone (index into Bone). -1 = root.
        private static readonly int[] Parents = { -1, 0, 1, 2, 2, 2, 4, 5, 6, 7, 0, 0, 10, 11, 12, 13 };

        private static readonly float[] MassWeight = { 12, 8, 14, 5, 2, 2, 3, 3, 1, 1, 8, 8, 5, 5, 2, 2 };

        // Joint motor spring / damper per bone (index 0, the root, has no joint).
        private static readonly float[] DriveSpring = { 0, 1500, 1500, 450, 700, 700, 500, 500, 250, 250, 3200, 3200, 2600, 2600, 900, 900 };
        private static readonly float[] DriveDamper = { 0, 100, 100, 35, 45, 45, 35, 35, 20, 20, 170, 170, 150, 150, 60, 60 };

        // ---------------------------------------------------------------- Public API

        public event Action<Bone, float, Collision> PartCollision;
        public event Action<BodySide> GripBroken;

        public bool IsReady { get; private set; }
        public float TotalMass { get; private set; }
        public Rigidbody Hips => bodies[(int)Bone.Hips];
        public Rigidbody Head => bodies[(int)Bone.Head];
        public Rigidbody Body(Bone b) => bodies[(int)b];
        public IReadOnlyList<Rigidbody> Bodies => bodies;
        public Transform[] PuppetTransforms { get; private set; } = Array.Empty<Transform>();
        public Camera RigCamera { get; private set; }
        public float HipsRadius { get; private set; } = 0.2f;

        /// <summary>Hips height above the lowest animated foot sole, as the animation currently has it (includes walk bob).</summary>
        public float AnimatedHipsHeight { get; private set; } = 1f;
        public Quaternion AnimatedHipsRotation => anim[(int)Bone.Hips] ? anim[(int)Bone.Hips].rotation : Quaternion.identity;

        // ---------------------------------------------------------------- State

        private Transform puppetRoot;
        private readonly Transform[] anim = new Transform[BoneCount];
        private readonly Transform[] puppet = new Transform[BoneCount];
        private readonly Rigidbody[] bodies = new Rigidbody[BoneCount];
        private readonly ConfigurableJoint[] joints = new ConfigurableJoint[BoneCount];
        private readonly Quaternion[] restRelative = new Quaternion[BoneCount];
        private readonly Vector3[] armAxisLocal = new Vector3[BoneCount];
        private readonly float[] footDrop = new float[BoneCount];   // foot centre -> sole distance
        private readonly Renderer[][] boneRenderers = new Renderer[BoneCount][];
        private readonly Grip[] grips = new Grip[2];
        private Renderer[] animatedRenderers = Array.Empty<Renderer>();

        private PlayableGraph graph;
        private AnimationClipPlayable walkPlayable, idlePlayable;
        private AnimationMixerPlayable mixer;
        private bool hasIdle;
        private float phase;
        private float appliedLimp = -1f;
        private bool simulated = true;
        private PhysicsMaterial material;

        private sealed class Grip
        {
            public RagdollPartContact hand;
            public FixedJoint joint;
            public GameObject anchor;
        }

        // ---------------------------------------------------------------- Setup

        private void Awake()
        {
            if (!animatedRig)
            {
                Debug.LogError("ActiveRagdollCharacter: assign 'Animated Rig' (your existing character).", this);
                enabled = false;
                return;
            }
            if (transform.IsChildOf(animatedRig))
            {
                Debug.LogError("ActiveRagdollCharacter must be on its own GameObject (e.g. an empty 'Player'), " +
                               "not on or inside the character it clones.", this);
                enabled = false;
                return;
            }

            if (!FindAnimatedBones()) { enabled = false; return; }
            MeasureAnimatedRig();
            UpdateAnimatedHipsHeight();   // rest pose, until the first animation frame
            BuildPuppet();
            PrepareAnimatedRig();
            BuildAnimationGraph();

            IsReady = true;
        }

        private bool FindAnimatedBones()
        {
            var missing = new List<string>();
            for (int i = 0; i < BoneCount; i++)
            {
                anim[i] = FindDeep(animatedRig, Names[i]);
                if (!anim[i]) missing.Add(Names[i]);
            }
            if (missing.Count > 0)
            {
                Debug.LogError("ActiveRagdollCharacter: couldn't find these parts under '" + animatedRig.name + "': " +
                               string.Join(", ", missing), this);
                return false;
            }
            return true;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            var queue = new Queue<Transform>();
            for (int i = 0; i < root.childCount; i++) queue.Enqueue(root.GetChild(i));
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase)) return t;
                for (int i = 0; i < t.childCount; i++) queue.Enqueue(t.GetChild(i));
            }
            return null;
        }

        /// <summary>Measures things from the animated rig while its colliders still exist.</summary>
        private void MeasureAnimatedRig()
        {
            foreach (var b in new[] { Bone.LeftFoot, Bone.RightFoot })
            {
                var col = anim[(int)b].GetComponent<Collider>();
                float lowest = anim[(int)b].position.y;
                if (col)
                {
                    GetShape(col, out Vector3 a, out Vector3 c, out float r);
                    lowest = Mathf.Min(a.y, c.y) - r;
                }
                footDrop[(int)b] = Mathf.Max(0f, anim[(int)b].position.y - lowest);
            }

            var hipsCol = anim[(int)Bone.Hips].GetComponent<Collider>();
            if (hipsCol)
            {
                GetShape(hipsCol, out _, out _, out float r);
                HipsRadius = Mathf.Max(0.05f, r);
            }

            RigCamera = animatedRig.GetComponentInChildren<Camera>(true);
        }

        private void BuildPuppet()
        {
            // Instantiate under an inactive holder so nothing on the clone wakes up before it is stripped.
            var holder = new GameObject("PuppetHolder");
            holder.SetActive(false);
            var clone = Instantiate(animatedRig.gameObject, animatedRig.position, animatedRig.rotation, holder.transform);
            clone.name = "PhysicsPuppet";
            puppetRoot = clone.transform;

            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true)) Destroy(mb);
            foreach (var an in clone.GetComponentsInChildren<Animator>(true)) Destroy(an);
            foreach (var cam in clone.GetComponentsInChildren<Camera>(true)) Destroy(cam);
            foreach (var al in clone.GetComponentsInChildren<AudioListener>(true)) Destroy(al);

            for (int i = 0; i < BoneCount; i++)
            {
                puppet[i] = FindDeep(puppetRoot, Names[i]);
                if (!puppet[i]) { Debug.LogError("Puppet is missing " + Names[i], this); return; }
                var rends = puppet[i].GetComponentsInChildren<Renderer>(true);
                boneRenderers[i] = rends;
            }

            material = new PhysicsMaterial("PuppetPart")
            {
                dynamicFriction = friction,
                staticFriction = friction,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };

            float weightSum = 0f;
            foreach (var w in MassWeight) weightSum += w;
            TotalMass = totalMass;

            // Rigidbodies and colliders first (joints need their connected bodies to exist).
            for (int i = 0; i < BoneCount; i++)
            {
                var t = puppet[i];
                var col = t.GetComponent<Collider>();
                if (!col)
                {
                    Debug.LogError("Part '" + Names[i] + "' has no collider (use sphere/capsule colliders).", this);
                    continue;
                }
                col.sharedMaterial = material;

                var rb = t.GetComponent<Rigidbody>();
                if (!rb) rb = t.gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = false;
                rb.useGravity = true;
                rb.mass = Mathf.Max(0.2f, totalMass * MassWeight[i] / weightSum);
                rb.linearDamping = 0.05f;
                rb.angularDamping = i == (int)Bone.Hips ? 0.6f : 0.8f;
                rb.maxAngularVelocity = 40f;
                rb.maxDepenetrationVelocity = 2.5f;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.solverIterations = solverIterations;
                rb.solverVelocityIterations = solverVelocityIterations;
                rb.collisionDetectionMode = (i <= (int)Bone.Head)
                    ? CollisionDetectionMode.ContinuousDynamic
                    : CollisionDetectionMode.Discrete;
                // Small parts have tiny natural inertia, which makes strong motors jittery. Floor it.
                rb.inertiaTensor = Vector3.Max(rb.inertiaTensor, Vector3.one * (rb.mass * 0.03f));
                bodies[i] = rb;

                var contact = t.gameObject.AddComponent<RagdollPartContact>();
                contact.owner = this;
                contact.bone = (Bone)i;
            }

            // Joints. Remember each part's rest pose relative to its parent: the animation is applied as a
            // change from this pose.
            for (int i = 1; i < BoneCount; i++)
            {
                int p = Parents[i];
                restRelative[i] = Quaternion.Inverse(puppet[p].rotation) * puppet[i].rotation;
                joints[i] = CreateJoint(i, p);
            }

            // Axis from shoulder-end to hand-end of each arm, so we can aim arms when reaching.
            foreach (var arm in new[] { Bone.LeftArm, Bone.RightArm })
            {
                int i = (int)arm;
                var col = puppet[i].GetComponent<Collider>();
                if (!col) continue;
                GetShape(col, out Vector3 a, out Vector3 b, out _);
                Vector3 parentCentre = puppet[Parents[i]].position;
                Vector3 near = (a - parentCentre).sqrMagnitude <= (b - parentCentre).sqrMagnitude ? a : b;
                Vector3 far = near == a ? b : a;
                Vector3 axis = (far - near);
                if (axis.sqrMagnitude < 1e-6f) axis = puppet[i].up;
                armAxisLocal[i] = Quaternion.Inverse(puppet[i].rotation) * axis.normalized;
            }

            // Wake the puppet up now that it is fully set up.
            puppetRoot.SetParent(transform, true);
            Destroy(holder);

            // Own parts never collide with each other.
            var colliders = new List<Collider>();
            for (int i = 0; i < BoneCount; i++)
            {
                var c = puppet[i].GetComponent<Collider>();
                if (c) colliders.Add(c);
            }
            for (int i = 0; i < colliders.Count; i++)
                for (int j = i + 1; j < colliders.Count; j++)
                    Physics.IgnoreCollision(colliders[i], colliders[j], true);

            var all = new Transform[BoneCount];
            for (int i = 0; i < BoneCount; i++) all[i] = puppet[i];
            PuppetTransforms = all;

            grips[0] = new Grip { hand = puppet[(int)Bone.LeftHand].GetComponent<RagdollPartContact>() };
            grips[1] = new Grip { hand = puppet[(int)Bone.RightHand].GetComponent<RagdollPartContact>() };
            grips[0].hand.isHand = true;
            grips[1].hand.isHand = true;

            SetLimp(0f);
        }

        private ConfigurableJoint CreateJoint(int bone, int parent)
        {
            var go = puppet[bone].gameObject;
            var j = go.GetComponent<ConfigurableJoint>();
            if (!j) j = go.AddComponent<ConfigurableJoint>();

            Vector3 anchorWorld = ComputeAnchor(bone, parent);
            j.connectedBody = bodies[parent];
            j.autoConfigureConnectedAnchor = false;
            j.anchor = puppet[bone].InverseTransformPoint(anchorWorld);
            j.connectedAnchor = puppet[parent].InverseTransformPoint(anchorWorld);

            // Joint frame == the part's own local axes, which is what the target-rotation maths below assumes.
            j.axis = Vector3.right;
            j.secondaryAxis = Vector3.up;
            j.configuredInWorldSpace = false;
            j.swapBodies = false;

            j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
            // Angles are left free: the motors, not limits, decide the pose.
            j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Free;

            j.rotationDriveMode = RotationDriveMode.Slerp;
            j.projectionMode = JointProjectionMode.PositionAndRotation;
            j.enableCollision = false;
            return j;
        }

        /// <summary>
        /// Where a part pivots on its parent, worked out from the collider shapes:
        /// a capsule limb pivots at its end nearest the parent; sphere-on-capsule pivots at the capsule's end;
        /// sphere-on-sphere pivots in the overlap between the two.
        /// </summary>
        private Vector3 ComputeAnchor(int bone, int parent)
        {
            var childCol = puppet[bone].GetComponent<Collider>();
            var parentCol = puppet[parent].GetComponent<Collider>();
            if (!childCol || !parentCol) return puppet[bone].position;

            GetShape(childCol, out Vector3 a1, out Vector3 b1, out float r1);
            GetShape(parentCol, out Vector3 a2, out Vector3 b2, out float r2);

            bool childCapsule = (a1 - b1).sqrMagnitude > 1e-6f;
            bool parentCapsule = (a2 - b2).sqrMagnitude > 1e-6f;
            Vector3 childCentre = (a1 + b1) * 0.5f;
            Vector3 parentRef = parentCapsule ? ClosestOnSegment(childCentre, a2, b2) : (a2 + b2) * 0.5f;

            if (childCapsule)
                return (a1 - parentRef).sqrMagnitude <= (b1 - parentRef).sqrMagnitude ? a1 : b1;
            if (parentCapsule)
                return parentRef;

            float w = r1 / Mathf.Max(r1 + r2, 1e-4f);
            return Vector3.Lerp(childCentre, parentRef, w);
        }

        private static Vector3 ClosestOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-8f) return a;
            return a + ab * Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
        }

        /// <summary>World-space core segment (a..b) and radius of a sphere / capsule collider.</summary>
        private static void GetShape(Collider col, out Vector3 a, out Vector3 b, out float r)
        {
            Transform t = col.transform;
            Vector3 s = t.lossyScale;
            float sx = Mathf.Abs(s.x), sy = Mathf.Abs(s.y), sz = Mathf.Abs(s.z);

            if (col is SphereCollider sphere)
            {
                a = b = t.TransformPoint(sphere.center);
                r = sphere.radius * Mathf.Max(sx, Mathf.Max(sy, sz));
                return;
            }
            if (col is CapsuleCollider cap)
            {
                int dir = cap.direction;
                Vector3 axisLocal = dir == 0 ? Vector3.right : dir == 1 ? Vector3.up : Vector3.forward;
                float along = dir == 0 ? sx : dir == 1 ? sy : sz;
                float radial = dir == 0 ? Mathf.Max(sy, sz) : dir == 1 ? Mathf.Max(sx, sz) : Mathf.Max(sx, sy);
                r = cap.radius * radial;
                float half = Mathf.Max(0f, cap.height * along * 0.5f - r);
                Vector3 c = t.TransformPoint(cap.center);
                Vector3 axis = t.TransformDirection(axisLocal).normalized;
                a = c - axis * half;
                b = c + axis * half;
                return;
            }

            Bounds bd = col.bounds;
            a = b = bd.center;
            r = Mathf.Min(bd.extents.x, Mathf.Min(bd.extents.y, bd.extents.z));
        }

        /// <summary>Hide the original character and stop it colliding: it only exists to play the animation.</summary>
        private void PrepareAnimatedRig()
        {
            foreach (var col in animatedRig.GetComponentsInChildren<Collider>(true)) col.enabled = false;
            foreach (var rb in animatedRig.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }
            foreach (var j in animatedRig.GetComponentsInChildren<Joint>(true)) j.enableCollision = false;

            animatedRenderers = animatedRig.GetComponentsInChildren<Renderer>(true);
            foreach (var r in animatedRenderers) r.enabled = showAnimatedRig;
        }

        private void BuildAnimationGraph()
        {
            var animator = animatedRig.GetComponentInChildren<Animator>(true);
            if (!animator) animator = animatedRig.gameObject.AddComponent<Animator>();

            if (!walkClip)
            {
                var controller = animator.runtimeAnimatorController;
                if (controller != null)
                {
                    foreach (var c in controller.animationClips)
                    {
                        if (c.name.ToLowerInvariant().Contains("walk")) { walkClip = c; break; }
                    }
                    if (!walkClip && controller.animationClips.Length > 0) walkClip = controller.animationClips[0];
                }
            }
            if (!walkClip)
            {
                Debug.LogError("ActiveRagdollCharacter: no walk clip. Assign 'Walk Clip' or give the Animator a controller with one.", this);
                return;
            }

            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.enabled = true;

            graph = PlayableGraph.Create("ActiveRagdollAnimation");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var output = AnimationPlayableOutput.Create(graph, "Animation", animator);

            hasIdle = idleClip != null;
            mixer = AnimationMixerPlayable.Create(graph, hasIdle ? 2 : 1);
            walkPlayable = AnimationClipPlayable.Create(graph, walkClip);
            graph.Connect(walkPlayable, 0, mixer, 0);
            if (hasIdle)
            {
                idlePlayable = AnimationClipPlayable.Create(graph, idleClip);
                graph.Connect(idlePlayable, 0, mixer, 1);
            }
            mixer.SetInputWeight(0, 1f);
            if (hasIdle) mixer.SetInputWeight(1, 0f);
            output.SetSourcePlayable(mixer);
            graph.Play();

            phase = idleNormalizedTime;
        }

        private void OnDestroy()
        {
            if (graph.IsValid()) graph.Destroy();
            if (material) Destroy(material);
        }

        // ---------------------------------------------------------------- Per-physics-step update

        /// <summary>
        /// Plays the animation for this step and pushes it into the joint motors.
        /// signedSpeed: ground-relative speed in the direction the body faces (negative = walking backwards).
        /// </summary>
        public void Evaluate(float dt, float signedSpeed, bool grounded, float facingYaw,
            Vector3 lookDirection, float reachLeft, float reachRight, float limp)
        {
            if (!IsReady || !simulated || !graph.IsValid()) return;

            animatedRig.rotation = Quaternion.Euler(0f, facingYaw + forwardYawOffset, 0f);

            AdvanceAnimation(dt, signedSpeed, grounded);
            graph.Evaluate();

            if (showAnimatedRig)
            {
                // Overlay the ghost on the puppet so differences are easy to see.
                animatedRig.position += Hips.position - anim[(int)Bone.Hips].position;
            }

            UpdateAnimatedHipsHeight();

            for (int i = 1; i < BoneCount; i++)
            {
                if (!joints[i]) continue;
                int p = Parents[i];
                Quaternion animRel = Quaternion.Inverse(anim[p].rotation) * anim[i].rotation;

                if (i == (int)Bone.LeftArm && reachLeft > 0.001f)
                    animRel = Quaternion.Slerp(animRel, ReachRelative(i, p, lookDirection), reachLeft);
                else if (i == (int)Bone.RightArm && reachRight > 0.001f)
                    animRel = Quaternion.Slerp(animRel, ReachRelative(i, p, lookDirection), reachRight);

                // Joint target = inverse of the wanted rotation, expressed from the rest pose.
                joints[i].targetRotation = Quaternion.Inverse(animRel) * restRelative[i];
            }

            SetLimp(limp);
        }

        private void AdvanceAnimation(float dt, float signedSpeed, bool grounded)
        {
            float absSpeed = Mathf.Abs(signedSpeed);
            float idlePhase = idleNormalizedTime;

            if (!grounded)
            {
                phase = MoveTowardsWrapped(phase, airNormalizedTime, settleRate * dt);
            }
            else if (absSpeed > 0.08f)
            {
                phase += signedSpeed / Mathf.Max(0.1f, metersPerCycle) * dt;
            }
            else
            {
                phase = MoveTowardsWrapped(phase, idlePhase, settleRate * dt);
            }
            phase = Mathf.Repeat(phase, 1f);

            walkPlayable.SetTime(phase * walkClip.length);

            if (hasIdle)
            {
                float moving = grounded ? Mathf.Clamp01(absSpeed / 0.6f) : 1f;
                mixer.SetInputWeight(0, moving);
                mixer.SetInputWeight(1, 1f - moving);
                idlePlayable.SetTime(Mathf.Repeat(Time.time, Mathf.Max(0.1f, idleClip.length)));
            }
        }

        private static float MoveTowardsWrapped(float current, float target, float maxDelta)
        {
            float diff = Mathf.Repeat(target - current + 0.5f, 1f) - 0.5f;   // shortest way round, -0.5..0.5
            if (Mathf.Abs(diff) <= maxDelta) return target;
            return current + Mathf.Sign(diff) * maxDelta;
        }

        private void UpdateAnimatedHipsHeight()
        {
            float soleL = anim[(int)Bone.LeftFoot].position.y - footDrop[(int)Bone.LeftFoot];
            float soleR = anim[(int)Bone.RightFoot].position.y - footDrop[(int)Bone.RightFoot];
            float ground = Mathf.Min(soleL, soleR);
            AnimatedHipsHeight = anim[(int)Bone.Hips].position.y - ground;
        }

        /// <summary>Arm rotation (relative to the shoulder) that points the arm along the look direction.</summary>
        private Quaternion ReachRelative(int arm, int shoulder, Vector3 lookDirection)
        {
            Vector3 axisWorld = anim[arm].rotation * armAxisLocal[arm];
            Quaternion desiredWorld = Quaternion.FromToRotation(axisWorld, lookDirection) * anim[arm].rotation;
            return Quaternion.Inverse(anim[shoulder].rotation) * desiredWorld;
        }

        // ---------------------------------------------------------------- Strength / simulation

        /// <summary>0 = joints hold the animation, 1 = nearly limp ragdoll.</summary>
        public void SetLimp(float limp)
        {
            limp = Mathf.Clamp01(limp);
            if (Mathf.Abs(limp - appliedLimp) < 0.005f) return;
            appliedLimp = limp;

            float mult = Mathf.Lerp(1f, limpStrength, limp) * jointStrength;
            float damp = Mathf.Lerp(1f, 0.4f, limp);
            for (int i = 1; i < BoneCount; i++)
            {
                if (!joints[i]) continue;
                joints[i].slerpDrive = new JointDrive
                {
                    positionSpring = DriveSpring[i] * mult,
                    positionDamper = DriveDamper[i] * damp * Mathf.Max(0.15f, mult),
                    maximumForce = float.MaxValue
                };
            }
        }

        /// <summary>Remote copies follow the network (kinematic); the owner simulates.</summary>
        public void SetSimulated(bool simulate)
        {
            if (!IsReady || simulated == simulate) return;
            simulated = simulate;
            if (!simulate) { ReleaseGrip(BodySide.Left); ReleaseGrip(BodySide.Right); }
            for (int i = 0; i < BoneCount; i++)
            {
                if (!bodies[i]) continue;
                bodies[i].isKinematic = !simulate;
                bodies[i].interpolation = simulate ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
            }
        }

        public void SetBoneVisible(Bone bone, bool visible)
        {
            var rends = boneRenderers[(int)bone];
            if (rends == null) return;
            foreach (var r in rends) if (r) r.enabled = visible;
        }

        // ---------------------------------------------------------------- Grip (hands)

        public bool IsGripping(BodySide side) => grips[(int)side]?.joint != null;

        /// <summary>Call every physics step with whether the player is holding that hand's grab button.</summary>
        public void SetGrip(BodySide side, bool wantGrip)
        {
            if (!IsReady || !simulated) return;
            var g = grips[(int)side];
            if (!wantGrip) { ReleaseGrip(side); return; }
            if (g.joint != null) return;

            var hand = g.hand;
            if (hand.lastCollider == null || Time.time - hand.lastContactTime > gripContactWindow) return;
            if (hand.lastCollider.transform.IsChildOf(transform)) return; // don't grab yourself

            Rigidbody target = hand.lastCollider.attachedRigidbody;
            if (target == null || target.isKinematic)
            {
                // Static or animated surface (the creature!): glue a kinematic anchor to it so the grip
                // follows the surface as it moves.
                g.anchor = new GameObject("GripAnchor");
                g.anchor.transform.SetParent(hand.lastCollider.transform, true);
                g.anchor.transform.position = hand.lastPoint;
                var arb = g.anchor.AddComponent<Rigidbody>();
                arb.isKinematic = true;
                target = arb;
            }

            var joint = hand.gameObject.AddComponent<FixedJoint>();
            joint.anchor = hand.transform.InverseTransformPoint(hand.lastPoint);
            joint.connectedBody = target;
            joint.breakForce = gripBreakForce;
            joint.breakTorque = float.PositiveInfinity;
            g.joint = joint;
        }

        public void ReleaseGrip(BodySide side)
        {
            var g = grips[(int)side];
            if (g == null) return;
            if (g.joint) Destroy(g.joint);
            g.joint = null;
            if (g.anchor) Destroy(g.anchor);
            g.anchor = null;
        }

        internal void ReportJointBreak(RagdollPartContact part)
        {
            for (int s = 0; s < 2; s++)
            {
                if (grips[s] == null || grips[s].hand != part) continue;
                grips[s].joint = null;
                if (grips[s].anchor) Destroy(grips[s].anchor);
                grips[s].anchor = null;
                GripBroken?.Invoke((BodySide)s);
            }
        }

        internal void ReportCollision(RagdollPartContact part, Collision c)
        {
            if (c.collider.transform.IsChildOf(transform)) return;
            PartCollision?.Invoke(part.bone, c.relativeVelocity.magnitude, c);
        }
    }

    /// <summary>Lives on every puppet part: forwards collisions and remembers what a hand last touched.</summary>
    public class RagdollPartContact : MonoBehaviour
    {
        [NonSerialized] public ActiveRagdollCharacter owner;
        [NonSerialized] public Bone bone;
        [NonSerialized] public bool isHand;

        [NonSerialized] public Collider lastCollider;
        [NonSerialized] public Vector3 lastPoint;
        [NonSerialized] public float lastContactTime = float.NegativeInfinity;

        private void OnCollisionEnter(Collision c)
        {
            if (owner) owner.ReportCollision(this, c);
            RecordContact(c);
        }

        private void OnCollisionStay(Collision c) => RecordContact(c);

        private void RecordContact(Collision c)
        {
            if (!isHand || c.contactCount == 0) return;
            lastCollider = c.collider;
            lastPoint = c.GetContact(0).point;
            lastContactTime = Time.time;
        }

        // Unity sends this to the object that owns the broken joint (the grip FixedJoint).
        private void OnJointBreak(float breakForce)
        {
            if (owner) owner.ReportJointBreak(this);
        }
    }
}
