using PurrNet;
using PurrNet.Transports;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DontWakeIt.Networking
{
    public enum BackendKind
    {
        Direct,
        Steam
    }

    public sealed class RoomOperation
    {
        public bool done;
        public bool success;
        public string error;
        public string code;

        public void Succeed(string roomCode = null) { code = roomCode; success = true; done = true; }
        public void Fail(string message) { error = message; success = false; done = true; }
    }

    public interface IRoomBackend
    {
        BackendKind Kind { get; }
        string DisplayName { get; }

        bool IsAvailable(out string reason);

        GenericTransport Bind(NetworkManager manager);

        bool PrepareHost(out string error);

        IEnumerator Advertise(string code, int maxPlayers, RoomOperation operation);

        IEnumerator Resolve(string input, RoomOperation operation);

        void StopAdvertising();

        void Tick();

        void Shutdown();
    }

    public static class RoomBackends
    {
        private static readonly Dictionary<BackendKind, IRoomBackend> Map = new Dictionary<BackendKind, IRoomBackend>();
        private static readonly List<IRoomBackend> List = new List<IRoomBackend>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatistics()
        {
            Map.Clear();
            List.Clear();
        }

        public static void Register(IRoomBackend backend)
        {
            if(Map.TryGetValue(backend.Kind, out var old))
            {
                List.Remove(old);
                old.Shutdown();
            }
            Map[backend.Kind] = backend;
            List.Add(backend);
        }

        public static bool TryGet(BackendKind kind, out IRoomBackend backend) => Map.TryGetValue(kind, out backend);

        public static IReadOnlyList<IRoomBackend> All => List;
    }
}
