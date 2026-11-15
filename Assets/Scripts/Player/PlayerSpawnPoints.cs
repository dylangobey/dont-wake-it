using System.Collections.Generic;
using PurrNet;
using UnityEngine;

namespace DontWakeIt.Player
{
    /// <summary>
    /// Chooses spawn points for the PlayerSpawner so two players are never placed on top of each other
    /// (overlapping ragdoll bodies get shoved apart violently and can be flung off the map).
    ///
    /// Add this to the same GameObject as the PlayerSpawner. It uses the PlayerSpawner's spawn points
    /// (or its own list, if you fill one in), skips any point that is occupied or was just used, and when
    /// every point is taken it places extra players on a ring around one so bodies still never overlap.
    /// </summary>
    [RequireComponent(typeof(PlayerSpawner))]
    public class PlayerSpawnPoints : MonoBehaviour, IProvideSpawnPoints
    {
        [Tooltip("Optional. If empty, the PlayerSpawner's own spawn points are used.")]
        [SerializeField] private List<Transform> points = new List<Transform>();
        [Tooltip("A point counts as taken if another player is within this many metres.")]
        [SerializeField] private float minSeparation = 1.5f;
        [Tooltip("When every point is taken, extra players are placed on a ring this far around a point.")]
        [SerializeField] private float ringRadius = 1.6f;
        [Tooltip("Seconds a fresh spawn keeps its spot reserved (the new body may not exist yet).")]
        [SerializeField] private float reserveSeconds = 3f;
        [SerializeField] private bool logSpawns = true;

        private PlayerSpawner spawner;
        private int cursor;
        private int overflow;
        private readonly List<Reservation> recent = new List<Reservation>();
        private readonly Collider[] hits = new Collider[48];

        private struct Reservation
        {
            public Vector3 position;
            public float time;
        }

        private void Awake()
        {
            spawner = GetComponent<PlayerSpawner>();
            spawner.SetRespawnPointProvider(this);
        }

        private void OnDestroy()
        {
            if (spawner) spawner.ResetSpawnPointProvider();
        }

        public SpawnPoint NextSpawnPoint(PlayerID player, SceneID scene)
        {
            IReadOnlyList<Transform> list = points.Count > 0 ? (IReadOnlyList<Transform>)points : spawner.SpawnPoints;

            if (list.Count == 0)
            {
                Debug.LogWarning("PlayerSpawnPoints: no spawn points set; spawning at this object.", this);
                return Reserve(transform.position, transform.rotation);
            }

            Physics.SyncTransforms();
            PruneReservations();

            // 1) The next point that is actually free.
            for (int k = 0; k < list.Count; k++)
            {
                int i = (cursor + k) % list.Count;
                Transform point = list[i];
                if (!point || !IsFree(point.position)) continue;

                cursor = (i + 1) % list.Count;
                return Reserve(point.position, point.rotation);
            }

            // 2) Everything is taken: search outward on a ring around the next point for a free spot.
            Transform anchor = null;
            for (int k = 0; k < list.Count && !anchor; k++) anchor = list[(cursor + k) % list.Count];
            if (!anchor) return Reserve(transform.position, transform.rotation);
            cursor++;

            Vector3 best = anchor.position;
            for (int attempt = 0; attempt < 24; attempt++)
            {
                float angle = (overflow++ * 137.5f) % 360f;                     // golden angle: even spread
                float radius = ringRadius * (1f + attempt / 8);
                Vector3 candidate = anchor.position + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * radius;
                best = candidate;
                if (IsFree(candidate)) break;
            }
            return Reserve(best, anchor.rotation);
        }

        private bool IsFree(Vector3 position)
        {
            float sqr = minSeparation * minSeparation;
            foreach (var r in recent)
                if ((r.position - position).sqrMagnitude < sqr) return false;

            int count = Physics.OverlapSphereNonAlloc(position + Vector3.up, minSeparation, hits, ~0,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
                if (hits[i] && hits[i].GetComponentInParent<RagdollPlayerController>())
                    return false;

            return true;
        }

        private SpawnPoint Reserve(Vector3 position, Quaternion rotation)
        {
            recent.Add(new Reservation { position = position, time = Time.time });
            if (logSpawns) Debug.Log("PlayerSpawnPoints: spawning a player at " + position, this);
            return new SpawnPoint { position = position, rotation = rotation };
        }

        private void PruneReservations()
        {
            for (int i = recent.Count - 1; i >= 0; i--)
                if (Time.time - recent[i].time > reserveSeconds)
                    recent.RemoveAt(i);
        }
    }
}