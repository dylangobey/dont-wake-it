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
        // Palette: a moonlit village at night, with lamplight.
        private static readonly Color Cream = new Color(1f, 0.945f, 0.84f);
        private static readonly Color Amber = new Color(1f, 0.72f, 0.3f);
        private static readonly Color AmberDark = new Color(0.2f, 0.11f, 0.05f);
        private static readonly Color Lavender = new Color(0.75f, 0.7f, 0.88f);
        private static readonly Color Plum = new Color(0.17f, 0.12f, 0.3f, 0.78f);
        private static readonly Color PlumDeep = new Color(0.09f, 0.07f, 0.18f, 0.85f);
        private static readonly Color Ghost = new Color(1f, 1f, 1f, 0.04f);
        private static readonly Color ErrorColor = new Color(1f, 0.6f, 0.5f);

        private Font font;       // body text
        private Font display;    // title and big numbers (a serif, if the OS has one)
        private Canvas canvas;

        private GameObject mainPanel;
        private GameObject lobbyPanel;

        private Text statusText;
        private Text hintText;
        private Text lobbyInfoText;
        private RectTransform codeTiles;
        private RectTransform playerPips;
        private string shownCode;
        private int shownPips = -1;
        private InputField codeInput;
        private Button hostButton, joinButton, startButton;
        private Button directButton, steamButton;
        private Text directButtonLabel, steamButtonLabel;
        private Image micFillImage;

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
            foreach (var tex in textures) if (tex) Destroy(tex);
        }

        private void Update()
        {
            if (micFill)
            {
                menuLevel = Mathf.Lerp(menuLevel, menuLevelRaw, 1f - Exp(-15f));
                micFill.anchorMax = new Vector2(Mathf.Clamp01(menuLevel), 1f);
                // Quiet is safe (green), loud wakes it (red).
                if (micFillImage)
                    micFillImage.color = menuLevel < 0.5f
                        ? Color.Lerp(new Color(0.55f, 0.85f, 0.6f), Amber, menuLevel * 2f)
                        : Color.Lerp(Amber, new Color(1f, 0.4f, 0.35f), (menuLevel - 0.5f) * 2f);
            }

            AnimateScene();

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

            bool directOn = selected == BackendKind.Direct;
            SetButtonColor(directButton, directOn ? Amber : Ghost);
            SetButtonColor(steamButton, selected == BackendKind.Steam ? Amber : Ghost);
            directButtonLabel.color = directOn ? AmberDark : Cream;
            steamButtonLabel.color = !hasSteam ? new Color(Lavender.r, Lavender.g, Lavender.b, 0.5f) : directOn ? Cream : AmberDark;
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
                statusText.color = connection.StatusIsError ? ErrorColor : Lavender;
            }

            // Lobby.
            if (inLobby)
            {
                ShowCode(connection.RoomCode);
                ShowPips(connection.PlayerCount, connection.MaxPlayers);
                lobbyInfoText.text = (connection.ActiveBackend == BackendKind.Steam ? "Steam" : "Direct / LAN") +
                                     "   ·   " + connection.PlayerCount + " of " + connection.MaxPlayers + " here" +
                                     (connection.IsHost ? string.Empty : "\nWaiting for the host to wake up the game...");
                startButton.gameObject.SetActive(connection.IsHost);
            }
        }

        private void SetStatus(string message, bool error)
        {
            if (!statusText) return;
            statusText.text = message;
            statusText.color = error ? ErrorColor : Cream;
        }

        // ---------------------------------------------------------------- Scene (the sleeping creature and its village)

        private static float Exp(float rate) => Mathf.Exp(rate * Time.unscaledDeltaTime);

        private readonly System.Collections.Generic.List<Texture2D> textures = new System.Collections.Generic.List<Texture2D>();
        private Sprite roundedSprite, circleSprite, glowSprite, gradientSprite, arcSprite;

        private readonly System.Collections.Generic.List<Image> stars = new System.Collections.Generic.List<Image>();
        private readonly System.Collections.Generic.List<float> starPhase = new System.Collections.Generic.List<float>();
        private readonly System.Collections.Generic.List<Image> windows = new System.Collections.Generic.List<Image>();
        private readonly System.Collections.Generic.List<Text> sleepZ = new System.Collections.Generic.List<Text>();
        private RectTransform creatureBody;
        private Image moonGlow;

        private void AnimateScene()
        {
            float t = Time.unscaledTime;

            for (int i = 0; i < stars.Count; i++)
            {
                var c = stars[i].color;
                c.a = 0.25f + 0.65f * (0.5f + 0.5f * Mathf.Sin(t * (0.6f + starPhase[i] * 0.4f) + starPhase[i] * 20f));
                stars[i].color = c;
            }

            for (int i = 0; i < windows.Count; i++)
            {
                float flicker = 0.78f + 0.22f * Mathf.PerlinNoise(t * 0.7f, i * 7.3f);
                windows[i].color = new Color(1f, 0.78f, 0.42f, flicker);
            }

            if (creatureBody)
            {
                // Slow breathing: the whole hill rises and falls.
                float breath = Mathf.Sin(t * 0.85f);
                creatureBody.localScale = new Vector3(1f + breath * 0.004f, 1f + breath * 0.022f, 1f);
            }

            if (moonGlow)
            {
                var c = moonGlow.color;
                c.a = 0.28f + 0.05f * Mathf.Sin(t * 0.5f);
                moonGlow.color = c;
            }

            // z z Z drifting up from the sleeper.
            for (int i = 0; i < sleepZ.Count; i++)
            {
                float cycle = 5f;
                float p = Mathf.Repeat(t / cycle + i / (float)sleepZ.Count, 1f);
                var rt = sleepZ[i].rectTransform;
                rt.anchoredPosition = new Vector2(250f + Mathf.Sin(p * 6f) * 22f + p * 70f, 330f + p * 190f);
                rt.localScale = Vector3.one * (0.7f + p * 0.9f);
                var c = sleepZ[i].color;
                c.a = Mathf.Sin(p * Mathf.PI) * 0.75f;
                sleepZ[i].color = c;
            }
        }

        private void MakeSprites()
        {
            roundedSprite = MakeRoundedSprite();
            circleSprite = MakeCircleSprite(128, 0f);
            glowSprite = MakeCircleSprite(128, 1f);
            gradientSprite = MakeGradientSprite();
            arcSprite = MakeArcSprite();
        }

        private Texture2D NewTexture(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            textures.Add(tex);
            return tex;
        }

        private Sprite MakeRoundedSprite()
        {
            const int size = 64, radius = 30;
            var tex = NewTexture(size, size);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = Mathf.Abs(x + 0.5f - size * 0.5f), py = Mathf.Abs(y + 0.5f - size * 0.5f);
                    float half = size * 0.5f - radius;
                    float dx = Mathf.Max(px - half, 0f), dy = Mathf.Max(py - half, 0f);
                    float d = Mathf.Sqrt(dx * dx + dy * dy) - radius;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d)));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
        }

        /// <summary>glow = 0: a crisp disc. glow = 1: a soft radial falloff.</summary>
        private Sprite MakeCircleSprite(int size, float glow)
        {
            var tex = NewTexture(size, size);
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                    float hard = Mathf.Clamp01(r - d);
                    float soft = Mathf.Pow(Mathf.Clamp01(1f - d / r), 2.2f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Lerp(hard, soft, glow)));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private Sprite MakeGradientSprite()
        {
            const int h = 256;
            var tex = NewTexture(2, h);
            var top = new Color(0.055f, 0.06f, 0.19f);
            var mid = new Color(0.19f, 0.12f, 0.34f);
            var bottom = new Color(0.62f, 0.3f, 0.4f);
            for (int y = 0; y < h; y++)
            {
                float t = y / (h - 1f);   // 0 = bottom
                Color c = t < 0.45f ? Color.Lerp(bottom, mid, t / 0.45f) : Color.Lerp(mid, top, (t - 0.45f) / 0.55f);
                tex.SetPixel(0, y, c);
                tex.SetPixel(1, y, c);
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 2, h), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>The lower half of a thick ring: a closed, sleeping eye.</summary>
        private Sprite MakeArcSprite()
        {
            const int w = 160, h = 80;
            var tex = NewTexture(w, h);
            var centre = new Vector2(w * 0.5f, h - 6f);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), centre);
                    float ring = Mathf.Clamp01(Mathf.Min(d - 52f, 72f - d) + 0.5f);
                    float below = y + 0.5f < centre.y ? 1f : 0f;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, ring * below));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Font PickDisplayFont(Font fallback)
        {
            string[] wanted = { "Georgia", "Palatino Linotype", "Palatino", "Constantia", "Cambria", "Baskerville", "Times New Roman" };
            var installed = Font.GetOSInstalledFontNames();
            foreach (var name in wanted)
            {
                foreach (var have in installed)
                {
                    if (string.Equals(have, name, StringComparison.OrdinalIgnoreCase))
                        return Font.CreateDynamicFontFromOSFont(have, 32);
                }
            }
            return fallback;
        }

        private Image MakeImage(Transform parent, string name, Sprite sprite, Color color, bool sliced = false)
        {
            var rt = NewRect(name, parent);
            var image = rt.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            image.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
            image.raycastTarget = false;
            return image;
        }

        private static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
        }

        private void BuildScene(RectTransform root)
        {
            var bottomCentre = new Vector2(0.5f, 0f);

            // Sky.
            var sky = MakeImage(root, "Sky", gradientSprite, Color.white);
            Stretch(sky.rectTransform);

            // Stars.
            var rng = new System.Random(7);
            for (int i = 0; i < 70; i++)
            {
                float size = 2f + (float)rng.NextDouble() * 4f;
                var star = MakeImage(root, "Star", circleSprite, new Color(1f, 0.96f, 0.85f, 0.7f));
                Place(star.rectTransform, new Vector2(0f, 0f), new Vector2(0.5f, 0.5f),
                    new Vector2((float)rng.NextDouble() * 1920f, 420f + (float)rng.NextDouble() * 640f), new Vector2(size, size));
                stars.Add(star);
                starPhase.Add((float)rng.NextDouble());
            }

            // Moon.
            moonGlow = MakeImage(root, "Moon Glow", glowSprite, new Color(1f, 0.95f, 0.8f, 0.3f));
            Place(moonGlow.rectTransform, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(-300f, -190f), new Vector2(620f, 620f));
            var moon = MakeImage(root, "Moon", circleSprite, new Color(1f, 0.96f, 0.84f));
            Place(moon.rectTransform, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(-300f, -190f), new Vector2(150f, 150f));
            var bite = MakeImage(root, "Moon Shadow", circleSprite, new Color(0.9f, 0.86f, 0.74f));
            Place(bite.rectTransform, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(-325f, -165f), new Vector2(46f, 46f));

            // Far hills.
            var farHill = MakeImage(root, "Far Hill", circleSprite, new Color(0.3f, 0.2f, 0.42f));
            Place(farHill.rectTransform, bottomCentre, new Vector2(0.5f, 0.5f), new Vector2(-620f, -170f), new Vector2(1700f, 760f));

            // The creature: ears, the hill of its back, and a sleeping face.
            const float cx = 420f, cy = -40f, a = 1150f, b = 470f;
            var earColor = new Color(0.22f, 0.15f, 0.4f);
            var bodyColor = new Color(0.17f, 0.12f, 0.32f);

            var group = NewRect("Creature", root);
            Place(group, bottomCentre, new Vector2(0.5f, 0f), new Vector2(cx, cy - b), new Vector2(a * 2f, b * 2f));
            creatureBody = group;
            group.pivot = new Vector2(0.5f, 0f);
            group.anchoredPosition = new Vector2(cx, cy - b);

            var earL = MakeImage(group, "Ear L", circleSprite, earColor);
            Place(earL.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-470f, 500f), new Vector2(240f, 290f));
            var earR = MakeImage(group, "Ear R", circleSprite, earColor);
            Place(earR.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(470f, 500f), new Vector2(240f, 290f));

            var body = MakeImage(group, "Body", circleSprite, bodyColor);
            Place(body.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(a * 2f, b * 2f));

            var eyeColor = new Color(0.07f, 0.05f, 0.14f);
            var eyeL = MakeImage(group, "Eye L", arcSprite, eyeColor);
            Place(eyeL.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-170f, 250f), new Vector2(130f, 65f));
            var eyeR = MakeImage(group, "Eye R", arcSprite, eyeColor);
            Place(eyeR.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(170f, 250f), new Vector2(130f, 65f));
            var snout = MakeImage(group, "Snout", circleSprite, new Color(0.24f, 0.17f, 0.42f));
            Place(snout.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 170f), new Vector2(260f, 170f));
            var nose = MakeImage(group, "Nose", circleSprite, eyeColor);
            Place(nose.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 205f), new Vector2(54f, 36f));

            // Zzz.
            string[] letters = { "z", "z", "Z" };
            for (int i = 0; i < letters.Length; i++)
            {
                var rt = NewRect("Zzz", group);
                Place(rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(80f, 80f));
                var text = rt.gameObject.AddComponent<Text>();
                text.font = display;
                text.fontStyle = FontStyle.Italic;
                text.fontSize = 54;
                text.alignment = TextAnchor.MiddleCenter;
                text.color = new Color(Cream.r, Cream.g, Cream.b, 0f);
                text.text = letters[i];
                text.raycastTarget = false;
                sleepZ.Add(text);
            }

            // A little village on its back. Each house sits on the curve of the hill.
            float[] offsets = { 250f, 380f, 500f, 640f, 770f, 880f };
            float[] widths = { 70f, 56f, 84f, 60f, 74f, 52f };
            for (int i = 0; i < offsets.Length; i++)
            {
                float o = offsets[i];
                float surface = b * Mathf.Sqrt(Mathf.Max(0f, 1f - (o / a) * (o / a)));   // height above the body's centre
                MakeHouse(group, new Vector2(o, surface - 10f), widths[i], widths[i] * (1f + (i % 3) * 0.12f));
            }

            // The ground in front, drawn last so it overlaps the creature's flank.
            var ground = MakeImage(root, "Ground", circleSprite, new Color(0.07f, 0.05f, 0.14f));
            Place(ground.rectTransform, bottomCentre, new Vector2(0.5f, 0.5f), new Vector2(-150f, -230f), new Vector2(3000f, 560f));
        }

        private void MakeHouse(RectTransform parent, Vector2 position, float width, float height)
        {
            var colour = new Color(0.08f, 0.06f, 0.16f);

            var house = NewRect("House", parent);
            Place(house, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f), position, new Vector2(width, height));

            var wall = MakeImage(house, "Wall", roundedSprite, colour, true);
            Stretch(wall.rectTransform);
            wall.pixelsPerUnitMultiplier = 5f;

            var roof = MakeImage(house, "Roof", roundedSprite, colour, true);
            Place(roof.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(width * 0.82f, width * 0.82f));
            roof.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 45f);
            roof.pixelsPerUnitMultiplier = 5f;

            var window = MakeImage(house, "Window", roundedSprite, new Color(1f, 0.78f, 0.42f), true);
            Place(window.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -2f), new Vector2(width * 0.3f, width * 0.34f));
            window.pixelsPerUnitMultiplier = 8f;
            windows.Add(window);
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

            display = PickDisplayFont(font);
            MakeSprites();
            BuildScene(root);

            // ---- Main screen: a column on the left, over the night sky.
            mainPanel = MakeColumn("Main", root).gameObject;
            var main = mainPanel.transform;

            MakeText(main, "Don't", 104, TextAnchor.LowerLeft, Cream, 112f, display);
            MakeText(main, "Wake It", 104, TextAnchor.UpperLeft, Amber, 124f, display);
            MakeText(main, "Night crew. Quiet hands.", 24, TextAnchor.MiddleLeft, Lavender, 40f, font, FontStyle.Italic);
            MakeSpacer(main, 22f);

            // Direct / Steam as one rounded switch.
            var switchRow = MakePill("Backend Switch", main, 60f, PlumDeep);
            var switchLayout = switchRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            switchLayout.padding = new RectOffset(6, 6, 6, 6);
            switchLayout.spacing = 4f;
            switchLayout.childControlWidth = switchLayout.childControlHeight = true;
            switchLayout.childForceExpandWidth = switchLayout.childForceExpandHeight = true;
            directButton = MakePillButton(switchRow, "Direct / LAN", () => OnSelectBackend(BackendKind.Direct), out directButtonLabel, Ghost, Cream, 48f, 22);
            steamButton = MakePillButton(switchRow, "Steam", () => OnSelectBackend(BackendKind.Steam), out steamButtonLabel, Ghost, Cream, 48f, 22);

            MakeSpacer(main, 8f);
            hostButton = MakePillButton(main, "Host a room", OnHost, out _, Amber, AmberDark, 68f, 30);

            MakeSpacer(main, 6f);
            MakeText(main, "or join one", 20, TextAnchor.MiddleLeft, Lavender, 28f, font, FontStyle.Italic);

            var joinRow = MakeRow(main, 60f);
            codeInput = MakeInput(joinRow, "Room code");
            joinButton = MakePillButton(joinRow, "Join", OnJoin, out _, Plum, Cream, 60f, 26);
            joinButton.GetComponent<LayoutElement>().preferredWidth = 150f;
            joinButton.GetComponent<LayoutElement>().flexibleWidth = 0f;

            hintText = MakeText(main, string.Empty, 17, TextAnchor.MiddleLeft, new Color(Lavender.r, Lavender.g, Lavender.b, 0.75f), 26f, font);

            // Microphone: how loud you are right now, in the colours of "will it wake".
            MakeSpacer(main, 8f);
            var micRow = MakeRow(main, 40f);
            micLabel = MakeText(micRow, string.Empty, 17, TextAnchor.MiddleLeft, Lavender, 40f, font);
            micButton = MakePillButton(micRow, "Change", OnChangeMic, out _, Plum, Cream, 40f, 17);
            micButton.GetComponent<LayoutElement>().preferredWidth = 110f;
            micButton.GetComponent<LayoutElement>().flexibleWidth = 0f;
            micFill = MakeMeter(main, out micFillImage);

            statusText = MakeText(main, string.Empty, 20, TextAnchor.MiddleLeft, Cream, 52f, font);
            MakeSpacer(main, 2f);
            var quit = MakePillButton(main, "Quit", () => { if (connection) connection.Quit(); else Application.Quit(); },
                out _, Ghost, Lavender, 44f, 20);
            quit.GetComponent<LayoutElement>().flexibleWidth = 0f;
            quit.GetComponent<LayoutElement>().preferredWidth = 130f;

            // ---- Lobby screen.
            lobbyPanel = MakeColumn("Lobby", root).gameObject;
            var lobby = lobbyPanel.transform;

            MakeText(lobby, "Tell your friends", 26, TextAnchor.LowerLeft, Lavender, 40f, font, FontStyle.Italic);
            MakeText(lobby, "Room code", 64, TextAnchor.MiddleLeft, Cream, 80f, display);
            codeTiles = MakeRow(lobby, 104f);
            codeTiles.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = false;
            codeTiles.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            codeTiles.GetComponent<HorizontalLayoutGroup>().spacing = 10f;

            MakeSpacer(lobby, 4f);
            playerPips = MakeRow(lobby, 22f);
            playerPips.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = false;
            playerPips.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            playerPips.GetComponent<HorizontalLayoutGroup>().spacing = 8f;

            lobbyInfoText = MakeText(lobby, string.Empty, 21, TextAnchor.UpperLeft, Lavender, 62f, font);
            MakeSpacer(lobby, 10f);
            startButton = MakePillButton(lobby, "Start the shift", () => { if (connection) connection.StartGame(); }, out _, Amber, AmberDark, 68f, 30);
            var copyRow = MakeRow(lobby, 54f);
            MakePillButton(copyRow, "Copy code", OnCopyCode, out _, Plum, Cream, 54f, 22);
            MakePillButton(copyRow, "Leave", () => { if (connection) connection.Leave(); }, out _, Plum, Cream, 54f, 22);
        }

        private void ShowCode(string code)
        {
            code = code ?? "-";
            if (code == shownCode || !codeTiles) return;
            shownCode = code;

            foreach (Transform child in codeTiles) Destroy(child.gameObject);
            foreach (char c in code)
            {
                var tile = MakeImage(codeTiles, "Tile", roundedSprite, Cream, true);
                tile.pixelsPerUnitMultiplier = 1.4f;
                var le = tile.gameObject.AddComponent<LayoutElement>();
                le.preferredWidth = 74f;
                le.preferredHeight = 96f;

                var rt = NewRect("Char", tile.transform);
                Stretch(rt);
                var text = rt.gameObject.AddComponent<Text>();
                text.font = display;
                text.fontSize = 66;
                text.fontStyle = FontStyle.Bold;
                text.alignment = TextAnchor.MiddleCenter;
                text.color = new Color(0.16f, 0.1f, 0.28f);
                text.text = c.ToString();
                text.raycastTarget = false;
            }
        }

        private void ShowPips(int present, int max)
        {
            int key = present * 100 + max;
            if (key == shownPips || !playerPips) return;
            shownPips = key;

            foreach (Transform child in playerPips) Destroy(child.gameObject);
            for (int i = 0; i < max; i++)
            {
                var pip = MakeImage(playerPips, "Pip", circleSprite,
                    i < present ? Amber : new Color(1f, 1f, 1f, 0.16f));
                var le = pip.gameObject.AddComponent<LayoutElement>();
                le.preferredWidth = le.preferredHeight = 20f;
            }
        }

        // ---------------------------------------------------------------- UI helpers

        /// <summary>The left-hand column both screens live in: no box, just content over the sky.</summary>
        private RectTransform MakeColumn(string name, RectTransform parent)
        {
            var rt = NewRect(name + " Column", parent);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(150f, 0f);
            rt.sizeDelta = new Vector2(600f, 0f);

            var layout = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            rt.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return rt;
        }

        private RectTransform MakePill(string name, Transform parent, float height, Color color)
        {
            var image = MakeImage(parent, name, roundedSprite, color, true);
            image.pixelsPerUnitMultiplier = 60f / Mathf.Max(20f, height);
            image.raycastTarget = false;
            image.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            return image.rectTransform;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        private Text MakeText(Transform parent, string content, int size, TextAnchor anchor, Color color, float height,
            Font useFont = null, FontStyle style = FontStyle.Normal)
        {
            var rt = NewRect("Text", parent);
            var text = rt.gameObject.AddComponent<Text>();
            text.font = useFont ? useFont : font;
            text.fontSize = size;
            text.fontStyle = style;
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

        private static RectTransform MakeRow(Transform parent, float height)
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

        private Button MakePillButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick,
            out Text labelText, Color fill, Color textColor, float height, int fontSize)
        {
            var image = MakeImage(parent, label + " Button", roundedSprite, fill, true);
            image.pixelsPerUnitMultiplier = 60f / Mathf.Max(20f, height);
            image.raycastTarget = true;
            var rt = image.rectTransform;

            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            colors.disabledColor = new Color(0.7f, 0.7f, 0.7f, 0.55f);
            button.colors = colors;
            button.onClick.AddListener(onClick);
            rt.gameObject.AddComponent<HoverLift>();

            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleWidth = 1f;

            var textRt = NewRect("Label", rt);
            Stretch(textRt);
            labelText = textRt.gameObject.AddComponent<Text>();
            labelText.font = fontSize >= 28 ? display : font;
            labelText.fontStyle = fontSize >= 28 ? FontStyle.Bold : FontStyle.Normal;
            labelText.fontSize = fontSize;
            labelText.alignment = TextAnchor.MiddleCenter;
            labelText.color = textColor;
            labelText.text = label;
            labelText.raycastTarget = false;
            return button;
        }

        private InputField MakeInput(Transform parent, string placeholder)
        {
            var image = MakeImage(parent, "Code Input", roundedSprite, PlumDeep, true);
            image.pixelsPerUnitMultiplier = 1f;
            image.raycastTarget = true;
            var rt = image.rectTransform;

            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.preferredHeight = 60f;

            var textRt = NewRect("Text", rt);
            Stretch(textRt, 26f);
            var text = textRt.gameObject.AddComponent<Text>();
            text.font = display;
            text.fontSize = 30;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = Cream;
            text.supportRichText = false;

            var phRt = NewRect("Placeholder", rt);
            Stretch(phRt, 26f);
            var ph = phRt.gameObject.AddComponent<Text>();
            ph.font = font;
            ph.fontSize = 22;
            ph.fontStyle = FontStyle.Italic;
            ph.alignment = TextAnchor.MiddleLeft;
            ph.color = new Color(Lavender.r, Lavender.g, Lavender.b, 0.55f);
            ph.text = placeholder;

            var input = rt.gameObject.AddComponent<InputField>();
            input.targetGraphic = image;
            input.textComponent = text;
            input.placeholder = ph;
            input.caretColor = Amber;
            input.selectionColor = new Color(Amber.r, Amber.g, Amber.b, 0.4f);
            input.characterLimit = 24;   // long enough for "255.255.255.255:65535"
            input.onValidateInput = (_, __, c) => char.IsLetterOrDigit(c) || c == '.' || c == ':' || c == '-'
                ? char.ToUpperInvariant(c)
                : '\0';
            return input;
        }

        /// <summary>A rounded level bar; the returned rect is the part that grows with the microphone level.</summary>
        private RectTransform MakeMeter(Transform parent, out Image fillImage)
        {
            var background = MakeImage(parent, "Mic Meter", roundedSprite, PlumDeep, true);
            background.pixelsPerUnitMultiplier = 5f;
            background.gameObject.AddComponent<LayoutElement>().preferredHeight = 12f;

            fillImage = MakeImage(background.transform, "Fill", roundedSprite, new Color(0.55f, 0.85f, 0.6f), true);
            fillImage.pixelsPerUnitMultiplier = 5f;
            var fill = fillImage.rectTransform;
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
