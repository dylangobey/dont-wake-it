using UnityEngine;

namespace DontWakeIt.Player
{
    public sealed class GroundTracker
    {
        public Collider Collider { get; private set; }
        public Vector3 Velocity { get; private set; }
        public Vector3 Normal { get; private set; } = Vector3.up;

        public float YawDelta { get; private set; }
        public bool HasGround => Collider != null;

        private Transform tracked;
        private Vector3 localPoint;
        private Vector3 lastWorldPoint;
        private Quaternion lastRotation;

        public void Sample(RaycastHit hit, float dt)
        {
            Collider = hit.collider;
            Normal = hit.normal;
            YawDelta = 0f;

            Rigidbody body = hit.collider.attachedRigidbody;
            if(body && !body.isKinematic)
            {
                Velocity = body.GetPointVelocity(hit.point);
                YawDelta = Mathf.Rad2Deg * body.angularVelocity.y * dt;
                tracked = null;
                return;
            }

            Transform t = hit.collider.transform;
            Vector3 velocity = Vector3.zero;

            if(t == tracked && dt > 0f)
            {
                Vector3 pointNow = t.TransformPoint(localPoint);
                velocity = (pointNow - lastWorldPoint) / dt;

                Quaternion delta = t.rotation * Quaternion.Inverse(lastRotation);
                Vector3 fwd = delta * Vector3.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 1e-6f)
                    YawDelta = Vector3.SignedAngle(Vector3.forward, fwd, Vector3.up);
            }

            tracked = t;
            localPoint = t.InverseTransformPoint(hit.point);
            lastWorldPoint = hit.point;
            lastRotation = t.rotation;
            Velocity = velocity;
        }

        public void Clear(bool keepVelocity)
        {
            Collider = null;
            tracked = null;
            YawDelta = 0f;
            Normal = Vector3.up;
            if(!keepVelocity) Velocity = Vector3.zero;
        }
    }
}