// Only compiled when the Steamworks.NET package is installed (see DontWakeIt.Steam.asmdef) and on
// platforms Steam supports.
#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

#if !DISABLESTEAMWORKS
using System;
using System.Collections;
using PurrNet;
using PurrNet.Steam;
using PurrNet.Transports;
using Steamworks;
using UnityEngine;

namespace DontWakeIt.Networking.Steam
{
    /// <summary>
    /// Steam connections: PurrNet's SteamTransport (Steam P2P, no port forwarding) plus a Steam lobby whose
    /// metadata holds the room code. Joining searches Steam for a lobby with that code, then connects to
    /// the lobby owner's SteamID.
    ///
    /// For testing, use app id 480 (Spacewar) in steam_appid.txt next to the Assets folder. Replace it with
    /// your own app id once the game is on Steam.
    /// </summary>
    public sealed class SteamRoomBackend : IRoomBackend
    {
        private const string CodeKey = "dwi_code";
        private const string GameKey = "dwi_game";
        private const string GameValue = "dont-wake-it";
        private const float SteamTimeout = 10f;

        private SteamTransport transport;
        private bool initialised;

        private CSteamID lobby;
        private bool inLobby;

        private CallResult<LobbyCreated_t> createResult;
        private CallResult<LobbyMatchList_t> listResult;
        private CallResult<LobbyEnter_t> enterResult;

        private RoomOperation pendingOperation;
        private string pendingCode;
        private CSteamID foundLobby;

        public BackendKind Kind => BackendKind.Steam;
        public string DisplayName => "Steam";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => RoomBackends.Register(new SteamRoomBackend());

        // ---------------------------------------------------------------- Setup

        public bool IsAvailable(out string reason) => EnsureInitialised(out reason);

        private bool EnsureInitialised(out string reason)
        {
            reason = null;
            if (initialised) return true;

            try
            {
                initialised = SteamAPI.Init();
            }
            catch (Exception e)
            {
                reason = "Steam couldn't start: " + e.Message;
                return false;
            }

            if (!initialised)
            {
                reason = "Steam isn't running, or steam_appid.txt is missing next to the Assets folder.";
                return false;
            }

            SteamNetworkingUtils.InitRelayNetworkAccess();
            return true;
        }

        public GenericTransport TransportOverride { get; set; }

        public GenericTransport Bind(NetworkManager manager)
        {
            transport = TransportOverride as SteamTransport;
            if (TransportOverride && !transport)
                Debug.LogWarning("The Steam transport you assigned isn't a SteamTransport; looking for one instead.");

            if (!transport) transport = manager.GetComponentInChildren<SteamTransport>(true);
            if (!transport)
            {
                transport = manager.gameObject.AddComponent<SteamTransport>();
                Debug.LogWarning("No SteamTransport found, so one was added to the NetworkManager object. " +
                                 "Add your own, or assign one on the ConnectionManager.");
            }
            return transport;
        }

        public bool PrepareHost(out string error)
        {
            if (!EnsureInitialised(out error)) return false;

            transport.peerToPeer = true;
            transport.dedicatedServer = false;
            transport.address = SteamUser.GetSteamID().m_SteamID.ToString();   // the host's own client
            return true;
        }

        /// <summary>Steam only delivers results while callbacks are pumped.</summary>
        public void Tick()
        {
            if (initialised) SteamAPI.RunCallbacks();
        }

        public void Shutdown()
        {
            StopAdvertising();
            if (initialised)
            {
                SteamAPI.Shutdown();
                initialised = false;
            }
        }

        // ---------------------------------------------------------------- Hosting

        public IEnumerator Advertise(string code, int maxPlayers, RoomOperation operation)
        {
            pendingOperation = operation;
            pendingCode = code;

            createResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            createResult.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, Mathf.Max(1, maxPlayers)));

            yield return WaitDone(operation);
            if (!operation.done) operation.Fail("Steam didn't respond when creating the room.");
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                pendingOperation.Fail("Steam couldn't create the room (" + result.m_eResult + ").");
                return;
            }

            lobby = new CSteamID(result.m_ulSteamIDLobby);
            inLobby = true;
            SteamMatchmaking.SetLobbyData(lobby, CodeKey, pendingCode);
            SteamMatchmaking.SetLobbyData(lobby, GameKey, GameValue);
            SteamMatchmaking.SetLobbyJoinable(lobby, true);
            pendingOperation.Succeed(pendingCode);
        }

        public void StopAdvertising()
        {
            if (inLobby)
            {
                try { SteamMatchmaking.LeaveLobby(lobby); }
                catch (Exception) { /* Steam already shut down */ }
                inLobby = false;
            }
        }

        // ---------------------------------------------------------------- Joining

        public IEnumerator Resolve(string input, RoomOperation operation)
        {
            string code = DontWakeIt.Networking.RoomCode.Normalize(input);
            if (!DontWakeIt.Networking.RoomCode.IsPlausible(code))
            {
                operation.Fail("Enter a valid room code.");
                yield break;
            }

            // 1) Find the lobby that carries this code.
            var search = new RoomOperation();
            pendingOperation = search;

            SteamMatchmaking.AddRequestLobbyListStringFilter(CodeKey, code, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(GameKey, GameValue, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(1);

            listResult = CallResult<LobbyMatchList_t>.Create(OnLobbyList);
            listResult.Set(SteamMatchmaking.RequestLobbyList());

            yield return WaitDone(search);
            if (!search.success)
            {
                operation.Fail(search.error ?? "Steam didn't respond while searching for the room.");
                yield break;
            }

            // 2) Join it so Steam tells us who owns it.
            var enter = new RoomOperation();
            pendingOperation = enter;

            enterResult = CallResult<LobbyEnter_t>.Create(OnLobbyEnter);
            enterResult.Set(SteamMatchmaking.JoinLobby(foundLobby));

            yield return WaitDone(enter);
            if (!enter.success)
            {
                operation.Fail(enter.error ?? "Couldn't join the room.");
                yield break;
            }

            // 3) Connect the transport to the host.
            CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobby);
            transport.peerToPeer = true;
            transport.dedicatedServer = false;
            transport.address = owner.m_SteamID.ToString();
            operation.Succeed(code);
        }

        private void OnLobbyList(LobbyMatchList_t result, bool ioFailure)
        {
            if (ioFailure)
            {
                pendingOperation.Fail("Steam couldn't search for rooms.");
                return;
            }
            if (result.m_nLobbiesMatching == 0)
            {
                pendingOperation.Fail("No room found with that code.");
                return;
            }

            foundLobby = SteamMatchmaking.GetLobbyByIndex(0);
            pendingOperation.Succeed();
        }

        private void OnLobbyEnter(LobbyEnter_t result, bool ioFailure)
        {
            if (ioFailure ||
                result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                pendingOperation.Fail("Couldn't join the room (it may be full or closed).");
                return;
            }

            lobby = new CSteamID(result.m_ulSteamIDLobby);
            inLobby = true;
            pendingOperation.Succeed();
        }

        private static IEnumerator WaitDone(RoomOperation operation)
        {
            float end = Time.unscaledTime + SteamTimeout;
            while (!operation.done && Time.unscaledTime < end)
                yield return null;
        }
    }
}
#endif
