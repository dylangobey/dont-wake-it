using System;
using System.Collections;
using PurrNet;
using PurrNet.Modules;
using PurrNet.Transports;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace DontWakeIt.Networking
{
    public enum ConnectionPhase
    {
        Idle,
        Hosting,
        Joining,
        InLobby,
        InGame
    }

    /// <summary>
    /// Hosts and joins rooms by room code, on top of PurrNet's NetworkManager.
    ///
    /// Lives on the same GameObject as the NetworkManager, in the Boot scene (which is never reloaded).
    /// The main menu talks to this: Host(kind), Join(kind, code), StartGame(), Leave().
    ///
    /// Flow:  Host -> start server -> advertise the room code -> start the host's own client -> lobby.
    ///        Join -> resolve the code to an address -> start client -> lobby.
    ///        The host presses Start Game, which loads the game scene for everyone through PurrNet.
    /// </summary>
    [DefaultExecutionOrder(-400)]
    public class ConnectionManager : MonoBehaviour
    {
        public static ConnectionManager Instance { get; private set; }

        /// <summary>True while the in-game pause menu is open. Player input should be ignored.</summary>
        public static bool InputBlocked => PauseOpen || UiBlockers > 0;

        /// <summary>True while the pause menu is open.</summary>
        public static bool PauseOpen { get; private set; }

        /// <summary>Other UI (the tool locker...) that wants the player's controls off while it's open: ++ on open, -- on close.</summary>
        public static int UiBlockers { get; set; }

        [Header("PurrNet (provide your own)")]
        [Tooltip("Your NetworkManager. If empty, one on this GameObject or in the scene is used.")]
        [SerializeField] private NetworkManager networkManager;
        [Tooltip("Optional. The UDPTransport for Direct / LAN. If empty, one on your NetworkManager is used (or added).")]
        [SerializeField] private GenericTransport directTransport;
        [Tooltip("Optional. The SteamTransport for Steam. If empty, one on your NetworkManager is used (or added).")]
        [SerializeField] private GenericTransport steamTransport;

        [Header("Scenes (must be in Build Settings)")]
        [SerializeField] private string menuScene = "MainMenu";
        [SerializeField] private string gameScene = "SampleScene";
        [Tooltip("When this starts in the Boot scene, go straight to the main menu.")]
        [SerializeField] private bool loadMenuOnStart = true;

        [Header("Room")]
        [SerializeField] private RoomCodeSettings codeSettings = new RoomCodeSettings();
        [Range(1, 16)][SerializeField] private int maxPlayers = 6;
        [Tooltip("Players can talk to each other in the lobby. The host loads the menu scene through PurrNet so that " +
                 "each player gets a networked voice object there (needs a PlayerSpawner with the Lobby Voice prefab " +
                 "in the menu scene: Tools > Dont Wake It > Voice Setup).")]
        [SerializeField] private bool lobbyVoice = true;

        [Header("Direct / LAN")]
        [Tooltip("UDP port players connect to.")]
        [SerializeField] private ushort gamePort = 5000;
        [Tooltip("UDP port the host listens on to answer room-code searches.")]
        [SerializeField] private int discoveryPort = 47777;
        [SerializeField] private float searchSeconds = 4f;

        [Header("Timeouts")]
        [SerializeField] private float connectTimeout = 15f;

        public ConnectionPhase Phase { get; private set; } = ConnectionPhase.Idle;
        public string RoomCode { get; private set; }
        public BackendKind? ActiveBackend { get; private set; }
        public bool IsHost { get; private set; }
        public string Status { get; private set; } = string.Empty;
        public bool StatusIsError { get; private set; }

        public RoomCodeSettings CodeSettings => codeSettings;
        public int MaxPlayers => maxPlayers;
        public int PlayerCount => networkManager ? networkManager.playerCount : 0;
        public string GameScene => gameScene;

        /// <summary>Raised whenever phase, room code or status changes.</summary>
        public event Action Changed;

        private IRoomBackend active;
        private Coroutine routine;
        private bool pauseOpen;
        private bool quitting;

        // ---------------------------------------------------------------- Lifecycle

        private void Awake()
        {
            if (Instance && Instance != this)
            {
                Destroy(this);   // only this component: whatever else is on the object is yours
                return;
            }
            Instance = this;

            if (!networkManager) networkManager = GetComponent<NetworkManager>();
            if (!networkManager) networkManager = FindFirstObjectByType<NetworkManager>();
            if (!networkManager)
            {
                Debug.LogError("ConnectionManager has no NetworkManager. Drag yours into the 'Network Manager' field " +
                               "(it must exist in the scene when this starts, e.g. in the Boot scene).", this);
                enabled = false;
                return;
            }

            DontDestroyOnLoad(transform.root.gameObject);

            var direct = new UdpRoomBackend(gamePort, discoveryPort, searchSeconds) { TransportOverride = directTransport };
            RoomBackends.Register(direct);
            if (RoomBackends.TryGet(BackendKind.Steam, out var steam)) steam.TransportOverride = steamTransport;
        }

        private void Start()
        {
            if (!networkManager) return;

            networkManager.onClientConnectionState += OnClientState;
            SceneManager.sceneLoaded += OnSceneLoaded;

            if (loadMenuOnStart && !string.IsNullOrEmpty(menuScene) &&
                SceneManager.GetActiveScene().name != menuScene && Application.CanStreamedLevelBeLoaded(menuScene))
                SceneManager.LoadScene(menuScene);
        }

        private void OnDestroy()
        {
            if (Instance != this) return;

            if (networkManager) networkManager.onClientConnectionState -= OnClientState;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            foreach (var backend in RoomBackends.All) backend.Shutdown();
            PauseOpen = false;
            Instance = null;
        }

        private void OnApplicationQuit()
        {
            quitting = true;
            foreach (var backend in RoomBackends.All) backend.Shutdown();
        }

        private void Update()
        {
            foreach (var backend in RoomBackends.All) backend.Tick();

            if (Phase == ConnectionPhase.InGame && Keyboard.current != null &&
                Keyboard.current.escapeKey.wasPressedThisFrame)
                SetPause(!pauseOpen);
        }

        // ---------------------------------------------------------------- Public API

        public bool IsBackendAvailable(BackendKind kind, out string reason)
        {
            if (!RoomBackends.TryGet(kind, out var backend))
            {
                reason = kind + " isn't set up in this build.";
                return false;
            }
            return backend.IsAvailable(out reason);
        }

        public bool HasBackend(BackendKind kind) => RoomBackends.TryGet(kind, out _);

        public void Host(BackendKind kind)
        {
            if (Phase != ConnectionPhase.Idle) return;
            routine = StartCoroutine(HostRoutine(kind));
        }

        public void Join(BackendKind kind, string input)
        {
            if (Phase != ConnectionPhase.Idle) return;
            routine = StartCoroutine(JoinRoutine(kind, input));
        }

        /// <summary>Host only: load the game scene for everyone in the room.</summary>
        public void StartGame()
        {
            if (!IsHost || Phase != ConnectionPhase.InLobby) return;

            var scenes = networkManager.sceneModule;
            if (scenes == null)
            {
                SetStatus("The server isn't ready to load scenes yet.", true);
                return;
            }

            SetStatus("Loading game...", false);
            scenes.LoadSceneAsync(gameScene, new PurrSceneSettings
            {
                mode = LoadSceneMode.Single,
                physicsMode = LocalPhysicsMode.None,
                isPublic = true
            });
        }

        /// <summary>
        /// Makes the menu scene a PurrNet scene so lobby players can be spawned in it (for voice chat).
        /// Clients that join later are told to load it by PurrNet automatically.
        /// </summary>
        private void LoadLobbyScene()
        {
            var scenes = networkManager.sceneModule;
            if (scenes == null || string.IsNullOrEmpty(menuScene) || !Application.CanStreamedLevelBeLoaded(menuScene))
                return;

            scenes.LoadSceneAsync(menuScene, new PurrSceneSettings
            {
                mode = LoadSceneMode.Single,
                physicsMode = LocalPhysicsMode.None,
                isPublic = true
            });
        }

        /// <summary>Leave the room and go back to the main menu.</summary>
        public void Leave()
        {
            Teardown();
            SetStatus(string.Empty, false);
            ReturnToMenu();
        }

        public void Quit()
        {
            Teardown();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ---------------------------------------------------------------- Host / join

        private IEnumerator HostRoutine(BackendKind kind)
        {
            if (!RoomBackends.TryGet(kind, out var backend))
            {
                Fail(kind + " isn't set up in this build.");
                yield break;
            }
            if (!backend.IsAvailable(out string reason))
            {
                Fail(reason);
                yield break;
            }

            active = backend;
            ActiveBackend = kind;
            IsHost = true;
            SetPhase(ConnectionPhase.Hosting, "Starting room...");

            GenericTransport transport = backend.Bind(networkManager);
            if (!backend.PrepareHost(out string prepareError))
            {
                Fail(prepareError);
                yield break;
            }

            string error = null;
            try
            {
                networkManager.transport = transport;
                networkManager.StartServer();
            }
            catch (Exception e)
            {
                error = e.Message;
            }
            if (error != null) { Fail(error); yield break; }

            yield return WaitFor(() => networkManager.serverState == ConnectionState.Connected, connectTimeout);
            if (networkManager.serverState != ConnectionState.Connected)
            {
                Fail("The server didn't start.");
                yield break;
            }

            string code = DontWakeIt.Networking.RoomCode.Generate(codeSettings);
            var advertise = new RoomOperation();
            yield return backend.Advertise(code, maxPlayers, advertise);
            if (!advertise.success)
            {
                Fail(advertise.error ?? "Couldn't create the room.");
                yield break;
            }
            RoomCode = code;

            try { networkManager.StartClient(); }
            catch (Exception e) { error = e.Message; }
            if (error != null) { Fail(error); yield break; }

            yield return WaitForClient(connectTimeout);
            if (networkManager.clientState != ConnectionState.Connected)
            {
                Fail("The host couldn't join its own room.");
                yield break;
            }

            SetPhase(ConnectionPhase.InLobby, "Room ready. Share the code!");
            if (lobbyVoice) LoadLobbyScene();
        }

        private IEnumerator JoinRoutine(BackendKind kind, string input)
        {
            if (!RoomBackends.TryGet(kind, out var backend))
            {
                Fail(kind + " isn't set up in this build.");
                yield break;
            }
            if (!backend.IsAvailable(out string reason))
            {
                Fail(reason);
                yield break;
            }

            active = backend;
            ActiveBackend = kind;
            IsHost = false;
            SetPhase(ConnectionPhase.Joining, "Looking for room...");

            GenericTransport transport = backend.Bind(networkManager);

            var resolve = new RoomOperation();
            yield return backend.Resolve(input, resolve);
            if (!resolve.success)
            {
                Fail(resolve.error ?? "Room not found.");
                yield break;
            }

            string error = null;
            try
            {
                networkManager.transport = transport;
                networkManager.StartClient();
            }
            catch (Exception e)
            {
                error = e.Message;
            }
            if (error != null) { Fail(error); yield break; }

            SetStatus("Connecting...", false);
            yield return WaitForClient(connectTimeout);
            if (networkManager.clientState != ConnectionState.Connected)
            {
                Fail("Couldn't connect to the room.");
                yield break;
            }

            RoomCode = resolve.code;
            SetPhase(ConnectionPhase.InLobby, "Joined. Waiting for the host to start...");
        }

        private IEnumerator WaitForClient(float timeout)
        {
            bool sawConnecting = false;
            yield return WaitFor(() =>
            {
                var state = networkManager.clientState;
                if (state == ConnectionState.Connecting) sawConnecting = true;
                return state == ConnectionState.Connected || (sawConnecting && state == ConnectionState.Disconnected);
            }, timeout);
        }

        private static IEnumerator WaitFor(Func<bool> condition, float timeout)
        {
            float end = Time.unscaledTime + timeout;
            while (!condition() && Time.unscaledTime < end)
                yield return null;
        }

        // ---------------------------------------------------------------- State handling

        private void OnClientState(ConnectionState state)
        {
            if (quitting || state != ConnectionState.Disconnected) return;

            // Lost the connection after we were in a room (host quit, network dropped...).
            if (Phase == ConnectionPhase.InLobby || Phase == ConnectionPhase.InGame)
            {
                Teardown();
                SetStatus("Disconnected from the room.", true);
                ReturnToMenu();
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return;

            if (scene.name == gameScene && (Phase == ConnectionPhase.InLobby || Phase == ConnectionPhase.InGame))
            {
                SetPause(false);
                SetPhase(ConnectionPhase.InGame, string.Empty);
            }
        }

        private void Fail(string message)
        {
            Teardown();
            SetStatus(message, true);
        }

        /// <summary>Stops everything and returns to Idle, without changing scene.</summary>
        private void Teardown()
        {
            if (routine != null)
            {
                StopCoroutine(routine);
                routine = null;
            }

            // Set Idle first so the disconnect callbacks below don't treat this as a dropped connection.
            Phase = ConnectionPhase.Idle;
            SetPause(false);

            active?.StopAdvertising();
            active = null;

            if (networkManager)
            {
                if (networkManager.clientState != ConnectionState.Disconnected) networkManager.StopClient();
                if (networkManager.serverState != ConnectionState.Disconnected) networkManager.StopServer();
            }

            ActiveBackend = null;
            RoomCode = null;
            IsHost = false;
            Changed?.Invoke();
        }

        private void ReturnToMenu()
        {
            if (!string.IsNullOrEmpty(menuScene) && SceneManager.GetActiveScene().name != menuScene)
                SceneManager.LoadScene(menuScene);
        }

        private void SetPhase(ConnectionPhase phase, string status)
        {
            Phase = phase;
            SetStatus(status, false);
        }

        private void SetStatus(string message, bool isError)
        {
            Status = message ?? string.Empty;
            StatusIsError = isError;
            if (isError && !string.IsNullOrEmpty(message)) Debug.LogWarning("[Connection] " + message, this);
            Changed?.Invoke();
        }

        // ---------------------------------------------------------------- In-game pause menu

        private void SetPause(bool open)
        {
            pauseOpen = open;
            PauseOpen = open;
            if (Phase == ConnectionPhase.InGame)
            {
                Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = open;
            }
        }

        private void OnGUI()
        {
            if (Phase != ConnectionPhase.InGame) return;

            if (!string.IsNullOrEmpty(RoomCode))
                GUI.Label(new Rect(Screen.width - 220f, 8f, 212f, 24f), "Room code: " + RoomCode);

            if (!pauseOpen) return;

            const float w = 240f, h = 170f;
            var box = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.Box(box, "Paused");
            GUILayout.BeginArea(new Rect(box.x + 20f, box.y + 34f, w - 40f, h - 44f));
            if (GUILayout.Button("Resume", GUILayout.Height(32f))) SetPause(false);
            if (GUILayout.Button("Leave room", GUILayout.Height(32f))) Leave();
            if (GUILayout.Button("Quit game", GUILayout.Height(32f))) Quit();
            GUILayout.EndArea();
        }
    }
}
