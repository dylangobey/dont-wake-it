using System;
using DontWakeIt.Networking;
using DontWakeIt.Voice;
using PurrNet.Voice;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace DontWakeIt.UI
{
    /// <summary>
    /// Basic main menu, built entirely in code (no UI assets to wire up).
    ///
    ///  Main screen: pick Direct/LAN or Steam, Host a room, or type a room code and Join.
    ///  Lobby screen: shows the room code and player count; the host presses Start Game.
    ///
    /// Talks to the ConnectionManager, which lives in the Boot scene. Start the game from the Boot scene
    /// (or via the ConnectionManager's menu auto-load) so it exists.
    /// </summary>
    public class MainMenuUI : MonoBehaviour
    {
        [SerializeField] private string title = "DON'T WAKE IT";

        private static readonly Color PanelColor = new Color(0.08f, 0.09f, 0.12f, 0.92f);
        private static readonly Color ButtonColor = new Color(0.22f, 0.25f, 0.33f, 1f);
        private static readonly Color SelectedColor = new Color(0.25f, 0.5f, 0.85f, 1f);
        private static readonly Color DisabledColor = new Color(0.15f, 0.16f, 0.2f, 1f);

        private Font font;
        private Canvas canvas;

        private GameObject mainPanel;
        private GameObject lobbyPanel;

        private Text statusText;
        private Text hintText;
        private Text lobbyCodeText;
        private Text lobbyInfoText;
        private InputField codeInput;
        private Button hostButton, joinButton, startButton;
        private Button directButton, steamButton;
        private Text steamButtonLabel;

        // Microphone selector + level meter
        private Text micLabel;
        private Button micButton;
        private RectTransform micFill;
        private Device testMic;
        private volatile float menuLevelRaw;
        private float menuLevel;
        private float nextMicCheck;

        private BackendKind selected = BackendKind.Direct;
        private ConnectionManager connection;
        private float nextRefresh;

        // ---------------------------------------------------------------- Lifecycle

        private void Start()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            EnsureEventSystem();
            BuildUi();

            connection = ConnectionManager.Instance;
            if (connection)
            {
                connection.Changed += Refresh;
            }
            else
            {
                SetStatus("No ConnectionManager found. Press Play from the Boot scene.", true);
            }
            Refresh();

            // In a lobby the voice chat owns the microphone, so only test it before joining a room.
            if (connection == null || connection.Phase == ConnectionPhase.Idle) StartMicTest();
        }

        private void OnDestroy()
        {
            if (connection) connection.Changed -= Refresh;
            StopMicTest();
        }

        private void Update()
        {
            if (micFill)
            {
                menuLevel = Mathf.Lerp(menuLevel, menuLevelRaw, 1f - Mathf.Exp(-15f * Time.unscaledDeltaTime));
                micFill.anchorMax = new Vector2(Mathf.Clamp01(menuLevel), 1f);
            }

            // A microphone plugged in after the menu opened.
            if (Time.unscaledTime >= nextMicCheck)
            {
                nextMicCheck = Time.unscaledTime + 1f;
                bool idle = connection == null || connection.Phase == ConnectionPhase.Idle;
                if (idle && testMic == null && MicrophoneSettings.HasMicrophone) StartMicTest();
            }

            // Player count changes without any event, so poll gently while in the lobby.
            if (connection && connection.Phase == ConnectionPhase.InLobby && Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + 0.5f;
                Refresh();
            }
        }

        private static void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>()) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            go.transform.SetParent(null);
        }

        // ---------------------------------------------------------------- Actions

        private void OnHost()
        {
            StopMicTest();   // free the microphone for the in-game voice chat
            if (connection) connection.Host(selected);
        }

        private void OnJoin()
        {
            StopMicTest();
            if (connection) connection.Join(selected, codeInput.text);
        }

        // ---------------------------------------------------------------- Microphone

        private void StartMicTest()
        {
            StopMicTest();
            testMic = MicrophoneSettings.Resolve();
            if (testMic != null)
            {
                testMic.onSampleReady += OnMenuSample;
                testMic.Start();
            }
            RefreshMicLabel();
        }

        private void StopMicTest()
        {
            if (testMic != null)
            {
                testMic.onSampleReady -= OnMenuSample;
                testMic.Stop();
                testMic = null;
            }
            menuLevelRaw = 0f;
        }

        private void OnMenuSample(ArraySegment<float> samples)
        {
            menuLevelRaw = MicrophoneSettings.Loudness(samples);
        }

        private void OnChangeMic()
        {
            MicrophoneSettings.Next();
            StartMicTest();
        }

        private void RefreshMicLabel()
        {
            if (!micLabel) return;

            if (testMic == null)
            {
                micLabel.text = "No microphone found: you'll hear others, but they won't hear you.";
                micButton.interactable = false;
                return;
            }

            micLabel.text = "Mic: " + MicrophoneSettings.Describe(testMic) +
                            (AudioDevices.hasPermission ? string.Empty : "  (needs permission)");
            micButton.interactable = MicrophoneSettings.Devices.Count > 1;
        }

        private void OnSelectBackend(BackendKind kind)
        {
            selected = kind;
            Refresh();
        }

        private void OnCopyCode()
        {
            if (connection != null && !string.IsNullOrEmpty(connection.RoomCode))
                GUIUtility.systemCopyBuffer = connection.RoomCode;
        }

        // ---------------------------------------------------------------- Refresh

        private void Refresh()
        {
            if (!mainPanel) return;

            var phase = connection ? connection.Phase : ConnectionPhase.Idle;
            if (phase != ConnectionPhase.Idle && testMic != null) StopMicTest();
            else if (phase == ConnectionPhase.Idle && testMic == null && MicrophoneSettings.HasMicrophone && micLabel) StartMicTest();
            bool inLobby = phase == ConnectionPhase.InLobby;
            bool busy = phase == ConnectionPhase.Hosting || phase == ConnectionPhase.Joining;

            mainPanel.SetActive(!inLobby && phase != ConnectionPhase.InGame);
            lobbyPanel.SetActive(inLobby);

            // Backend toggles.
            bool hasSteam = connection && connection.HasBackend(BackendKind.Steam);
            if (selected == BackendKind.Steam && !hasSteam) selected = BackendKind.Direct;

            SetButtonColor(directButton, selected == BackendKind.Direct ? SelectedColor : ButtonColor);
            SetButtonColor(steamButton, !hasSteam ? DisabledColor : selected == BackendKind.Steam ? SelectedColor : ButtonColor);
            steamButton.interactable = hasSteam && !busy;
            directButton.interactable = !busy;
            steamButtonLabel.text = hasSteam ? "Steam" : "Steam (not installed)";

            hostButton.interactable = connection && !busy;
            joinButton.interactable = connection && !busy;
            codeInput.interactable = !busy;

            hintText.text = selected == BackendKind.Steam
                ? "Enter the room code from your friend."
                : "Enter a room code (same network) or an IP address.";

            // Status line.
            if (connection)
            {
                statusText.text = connection.Status;
                statusText.color = connection.StatusIsError ? new Color(1f, 0.45f, 0.4f) : new Color(0.8f, 0.85f, 0.95f);
            }

            // Lobby.
            if (inLobby)
            {
                lobbyCodeText.text = connection.RoomCode ?? "-";
                lobbyInfoText.text = (connection.ActiveBackend == BackendKind.Steam ? "Steam" : "Direct / LAN") +
                                     "    Players: " + connection.PlayerCount + " / " + connection.MaxPlayers +
                                     (connection.IsHost ? "" : "\nWaiting for the host to start...");
                startButton.gameObject.SetActive(connection.IsHost);
            }
        }

        private void SetStatus(string message, bool error)
        {
            if (!statusText) return;
            statusText.text = message;
            statusText.color = error ? new Color(1f, 0.45f, 0.4f) : Color.white;
        }

        // ---------------------------------------------------------------- UI building

        private void BuildUi()
        {
            var canvasGo = new GameObject("MenuCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var root = canvasGo.GetComponent<RectTransform>();

            // ---- Main panel
            mainPanel = MakePanel("MainPanel", root, 560f).gameObject;
            var main = mainPanel.transform;

            MakeText(main, title, 54, TextAnchor.MiddleCenter, Color.white, 90f);
            MakeText(main, "Night crew. Quiet hands.", 20, TextAnchor.MiddleCenter, new Color(0.7f, 0.75f, 0.85f), 30f);
            MakeSpacer(main, 10f);

            var backendRow = MakeRow(main, 52f);
            directButton = MakeButton(backendRow, "Direct / LAN", () => OnSelectBackend(BackendKind.Direct), out _);
            steamButton = MakeButton(backendRow, "Steam", () => OnSelectBackend(BackendKind.Steam), out steamButtonLabel);

            hostButton = MakeButton(main, "Host Room", OnHost, out _, 56f);
            MakeSpacer(main, 6f);
            MakeText(main, "— or join a room —", 18, TextAnchor.MiddleCenter, new Color(0.7f, 0.75f, 0.85f), 26f);

            var joinRow = MakeRow(main, 52f);
            codeInput = MakeInput(joinRow, "ROOM CODE");
            joinButton = MakeButton(joinRow, "Join", OnJoin, out _);
            joinButton.GetComponent<LayoutElement>().preferredWidth = 140f;
            joinButton.GetComponent<LayoutElement>().flexibleWidth = 0f;

            hintText = MakeText(main, string.Empty, 16, TextAnchor.MiddleCenter, new Color(0.6f, 0.65f, 0.75f), 24f);

            MakeSpacer(main, 4f);
            var micRow = MakeRow(main, 40f);
            micLabel = MakeText(micRow, string.Empty, 17, TextAnchor.MiddleLeft, new Color(0.8f, 0.85f, 0.95f), 40f);
            micButton = MakeButton(micRow, "Change", OnChangeMic, out _, 40f);
            micButton.GetComponent<LayoutElement>().preferredWidth = 120f;
            micButton.GetComponent<LayoutElement>().flexibleWidth = 0f;
            micFill = MakeMeter(main);

            statusText = MakeText(main, string.Empty, 20, TextAnchor.MiddleCenter, Color.white, 56f);
            MakeSpacer(main, 6f);
            MakeButton(main, "Quit", () => { if (connection) connection.Quit(); else Application.Quit(); }, out _, 44f);

            // ---- Lobby panel
            lobbyPanel = MakePanel("LobbyPanel", root, 560f).gameObject;
            var lobby = lobbyPanel.transform;

            MakeText(lobby, "ROOM CODE", 22, TextAnchor.MiddleCenter, new Color(0.7f, 0.75f, 0.85f), 34f);
            lobbyCodeText = MakeText(lobby, "-", 84, TextAnchor.MiddleCenter, Color.white, 110f);
            lobbyInfoText = MakeText(lobby, string.Empty, 22, TextAnchor.MiddleCenter, Color.white, 70f);
            MakeSpacer(lobby, 8f);
            MakeButton(lobby, "Copy Code", OnCopyCode, out _, 46f);
            startButton = MakeButton(lobby, "Start Game", () => { if (connection) connection.StartGame(); }, out _, 60f);
            MakeButton(lobby, "Leave", () => { if (connection) connection.Leave(); }, out _, 46f);
        }

        private RectTransform MakePanel(string name, RectTransform parent, float width)
        {
            var rt = NewRect(name, parent);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(width, 0f);

            var bg = rt.gameObject.AddComponent<Image>();
            bg.color = PanelColor;

            var layout = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 12f;
            layout.padding = new RectOffset(32, 32, 28, 28);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = rt.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return rt;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        private Text MakeText(Transform parent, string content, int size, TextAnchor anchor, Color color, float height)
        {
            var rt = NewRect("Text", parent);
            var text = rt.gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = color;
            text.text = content;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;

            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            return text;
        }

        private static void MakeSpacer(Transform parent, float height)
        {
            var rt = NewRect("Spacer", parent);
            rt.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
        }

        private static Transform MakeRow(Transform parent, float height)
        {
            var rt = NewRect("Row", parent);
            var layout = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 12f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            rt.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            return rt;
        }

        private Button MakeButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick,
            out Text labelText, float height = 52f)
        {
            var rt = NewRect(label + " Button", parent);
            var image = rt.gameObject.AddComponent<Image>();
            image.color = ButtonColor;

            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.6f);
            button.colors = colors;
            button.onClick.AddListener(onClick);

            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleWidth = 1f;

            var textRt = NewRect("Label", rt);
            Stretch(textRt);
            labelText = textRt.gameObject.AddComponent<Text>();
            labelText.font = font;
            labelText.fontSize = 24;
            labelText.alignment = TextAnchor.MiddleCenter;
            labelText.color = Color.white;
            labelText.text = label;
            labelText.raycastTarget = false;
            return button;
        }

        private InputField MakeInput(Transform parent, string placeholder)
        {
            var rt = NewRect("Code Input", parent);
            var image = rt.gameObject.AddComponent<Image>();
            image.color = new Color(0.12f, 0.13f, 0.18f, 1f);

            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.preferredHeight = 52f;

            var textRt = NewRect("Text", rt);
            Stretch(textRt, 14f);
            var text = textRt.gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = 26;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = Color.white;
            text.supportRichText = false;

            var phRt = NewRect("Placeholder", rt);
            Stretch(phRt, 14f);
            var ph = phRt.gameObject.AddComponent<Text>();
            ph.font = font;
            ph.fontSize = 26;
            ph.fontStyle = FontStyle.Italic;
            ph.alignment = TextAnchor.MiddleLeft;
            ph.color = new Color(1f, 1f, 1f, 0.35f);
            ph.text = placeholder;

            var input = rt.gameObject.AddComponent<InputField>();
            input.targetGraphic = image;
            input.textComponent = text;
            input.placeholder = ph;
            input.characterLimit = 24;   // long enough for "255.255.255.255:65535"
            input.onValidateInput = (_, __, c) => char.IsLetterOrDigit(c) || c == '.' || c == ':' || c == '-'
                ? char.ToUpperInvariant(c)
                : '\0';
            return input;
        }

        /// <summary>A thin level bar; the returned rect is the part that grows with the microphone level.</summary>
        private static RectTransform MakeMeter(Transform parent)
        {
            var background = NewRect("Mic Meter", parent);
            background.gameObject.AddComponent<Image>().color = new Color(0.12f, 0.13f, 0.18f, 1f);
            background.gameObject.AddComponent<LayoutElement>().preferredHeight = 10f;

            var fill = NewRect("Fill", background);
            fill.gameObject.AddComponent<Image>().color = new Color(0.35f, 0.8f, 0.5f, 1f);
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = new Vector2(0f, 1f);
            fill.offsetMin = fill.offsetMax = Vector2.zero;
            return fill;
        }

        private static void Stretch(RectTransform rt, float padding = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padding, 0f);
            rt.offsetMax = new Vector2(-padding, 0f);
        }

        private static void SetButtonColor(Button button, Color color)
        {
            if (button && button.targetGraphic) button.targetGraphic.color = color;
        }
    }
}
