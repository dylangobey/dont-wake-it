using System.Collections;
using System.Collections.Generic;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace DontWakeIt.Networking
{
    public enum BackendKind
    {
        /// <summary>Plain UDP. Room codes are found by LAN broadcast; an IP address also works.</summary>
        Direct,
        /// <summary>Steam lobbies + Steam P2P. Room codes are stored in the lobby.</summary>
        Steam
    }

    /// <summary>Result of an asynchronous room operation (advertise / find a room).</summary>
    public sealed class RoomOperation
    {
        public bool done;
        public bool success;
        public string error;
        /// <summary>The normalised room code (null when joining by direct address).</summary>
        public string code;

        public void Succeed(string roomCode = null) { code = roomCode; success = true; done = true; }
        public void Fail(string message) { error = message; success = false; done = true; }
    }

    /// <summary>
    /// One way of connecting players: it owns a PurrNet transport and knows how to turn a room code
    /// into something that transport can connect to.
    /// </summary>
    public interface IRoomBackend
    {
        BackendKind Kind { get; }
        string DisplayName { get; }

        /// <summary>Can this backend be used right now? (e.g. Steam is running)</summary>
        bool IsAvailable(out string reason);

        /// <summary>Optional: a transport to use instead of searching for one. Set by the ConnectionManager.</summary>
        GenericTransport TransportOverride { get; set; }

        /// <summary>
        /// Finds this backend's transport: the override if set, otherwise one on / under the NetworkManager,
        /// otherwise one is added to the NetworkManager's GameObject (with a warning).
        /// </summary>
        GenericTransport Bind(NetworkManager manager);

        /// <summary>Configures the transport for hosting (ports, the host's own client address).</summary>
        bool PrepareHost(out string error);

        /// <summary>Makes the room findable by its code. Call once the server is running.</summary>
        IEnumerator Advertise(string code, int maxPlayers, RoomOperation operation);

        /// <summary>Turns what the player typed (a code, or an address) into transport settings.</summary>
        IEnumerator Resolve(string input, RoomOperation operation);

        /// <summary>Stops advertising / leaves the lobby.</summary>
        void StopAdvertising();

        /// <summary>Called every frame by the ConnectionManager.</summary>
        void Tick();

        void Shutdown();
    }

    /// <summary>Registry of available backends. Optional ones (Steam) register themselves at startup.</summary>
    public static class RoomBackends
    {
        private static readonly Dictionary<BackendKind, IRoomBackend> Map = new Dictionary<BackendKind, IRoomBackend>();
        private static readonly List<IRoomBackend> List = new List<IRoomBackend>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Map.Clear();
            List.Clear();
        }

        public static void Register(IRoomBackend backend)
        {
            if (Map.TryGetValue(backend.Kind, out var old))
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
