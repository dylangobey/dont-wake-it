using PurrNet;
using PurrNet.Modules;
using PurrNet.Transports;
using System;
using System.Collections;
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

    [DefaultExecutionOrder(-400)]
    public class ConnectionManager : MonoBehaviour
    {
        public static ConnectionManager Instance { get; private set; }

        public static bool InputBlocked { get; private set; }

        [Header("Scenes")]
        [SerializeField] private string menuScene = "MainMenu";
        [SerializeField] private string gameScene = "SampleScene";
        [SerializeField] private bool loadMenuOnStart = true;

        [Header("Room")]
        [SerializeField] private RoomCodeSettings codeSettings = new RoomCodeSettings();
        [Range(1, 16)][SerializeField] private int maxPlayers = 6;

        [Header("Direct / LAN")]
        [SerializeField] private ushort gamePort = 5000;
        [SerializeField] private int discoveryPort = 47777;
        [SerializeField] private float searchSeconds = 4f;

        [SerializeField] private float connectTimeout = 15f;

        public ConnectionPhase Phase { get; private set; } = ConnectionPhase.Idle;
        public string RoomCode { get; private set; }
        public BackendKind? ActiveBackend { get; private set; }
        public bool IsHost { get; private set; }
        public string Status { get; private set; }
        public bool StatusIsError { get; private set; }

        public RoomCodeSettings CodeSettings => codeSettings;
        public int MaxPlayers => maxPlayers;
        public int PlayerCount => networkManager ? networkManager.playerCount : 0;
        public string GameScene => gameScene;

        public event Action Changed;

        private NetworkManager networkManager;
        private IRoomBackend active;
        private Coroutine routine;
        private bool pauseOpen;

        private void Awake()
        {
            if(Instance && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            networkManager = GetComponent<NetworkManager>();
            if (!networkManager) networkManager = FindFirstObjectByType<NetworkManager>();
            if (!networkManager)
            {
                Debug.LogError("ConnectionManager needs a NetworkManager on the same GameObject.", this);
                enabled = false;
                return;
            }

            DontDestroyOnLoad(transform.root.gameObject);
            RoomBackends.Register(new UdpRoomBackend(gamePort, discoveryPort, searchSeconds));
        }

        private void Start()
        {
            if (!networkManager) return;

            networkManager.onClientConnectionState += OnClientState;
            if (loadMenuOnStart && !string.IsNullOrEmpty(menuScene) && SceneManager.GetActiveScene().name != menuScene && Application.CanStreamedLevelBeLoaded(menuScene)) SceneManager.LoadScene(menuScene);
        }

        private void OnDestroy()
        {
            if (Instance != this) return;

            if (networkManager) networkManager.onClientConnectionState -= OnClientState;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            foreach (var backend in RoomBackends.All) backend.Shutdown();
            InputBlocked = false;
            Instance = null;
        }

        private void OnApplicationQuit()
        {
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
            if (state != ConnectionState.Disconnected) return;

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
            InputBlocked = open;
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