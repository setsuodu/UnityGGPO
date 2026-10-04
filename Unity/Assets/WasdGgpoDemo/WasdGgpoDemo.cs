using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using UnityGGPO;
using Debug = UnityEngine.Debug;

namespace WasdDemo {

    /// <summary>
    /// Drop this on ONE empty GameObject in an empty scene and press Play. No prefabs, no UI setup.
    ///
    ///   solid square       = PlayerView: the state GGPO is running right now (P0 = blue, P1 = orange, fixed by index).
    ///                        The remote square is PREDICTED (peer's last input is assumed to continue).
    ///   hollow square      = Ghost: the same two players, but taken from History[head - 8 frames].
    ///                        GGPO cannot roll back past that, so it is the non-predicted, finalized timeline.
    ///   red hollow + line  = what you were SHOWN before a rollback  ->  where the corrected state put it.
    ///
    /// Command line (for two builds on one box):  -player 0|1  -lport N  -rip IP  -rport N  -delay N  -bot  -autostart
    /// </summary>
    public class WasdGgpoDemo : MonoBehaviour {

        [Header("Session")]
        public int playerIndex = 0;
        public int localPort = 7000;
        public string remoteIp = "127.0.0.1";
        public int remotePort = 7001;
        [Tooltip("GGPO local input delay (frames). Higher = fewer rollbacks, more input lag.")]
        public int frameDelay = 2;
        public bool autoStart = false;

        [Header("Test helpers")]
        [Tooltip("Scripted WASD square-wave instead of the keyboard (key: B). Gives repeatable mispredictions.")]
        public bool bot = false;
        public bool showGhost = true;
        [Tooltip("Write every rollback to <persistentDataPath>/wasd_rollback_P{n}.csv")]
        public bool logToFile = true;
        [Tooltip("Forward the native GGPO plugin log to the Unity console (very verbose).")]
        public bool pluginLog = false;

        private const double FRAME_MS = 1000.0 / 60.0;
        private const float MARKER_LIFE = 2.5f;
        // Fixed colors by player index (not local/remote): P0 = blue, P1 = orange.
        private static readonly Color Player0Color = new Color(0.25f, 0.65f, 1f);
        private static readonly Color Player1Color = new Color(1f, 0.6f, 0.15f);
        private static readonly Color RollbackColor = new Color(1f, 0.2f, 0.25f);

        private static GGPO.LogDelegate pluginLogDelegate;   // keep alive (native holds a raw pointer)

        private WasdRunner runner;
        private readonly Stopwatch clock = new Stopwatch();
        private double nextTickMs;
        private string error = "";

        // setup-panel text fields
        private string sLocalPort, sRemoteIp, sRemotePort, sDelay;

        // views
        private Sprite filledSprite, hollowSprite;
        private SpriteRenderer[] playerSR = new SpriteRenderer[2];
        private SpriteRenderer[] ghostSR = new SpriteRenderer[2];
        private SpriteRenderer[] lagLineSR = new SpriteRenderer[2];   // ghost -> PlayerView: how much is predicted
        private readonly List<SpriteRenderer> borders = new List<SpriteRenderer>();
        private float flash;                                          // arena border flashes red on every rollback
        private static readonly Color WallColor = new Color(0.35f, 0.38f, 0.45f);
        private readonly List<Marker> markers = new List<Marker>();
        private readonly List<string> rollbackLog = new List<string>();

        private GGPONetworkStats lastStats;
        private float nextStatsAt;
        private string csvPath;
        private GUIStyle hudStyle, boxStyle;

        private class Marker {
            public SpriteRenderer box, line;
            public float age;
        }

        // ============================================================ lifecycle

        private void Awake() {
            Application.runInBackground = true;        // two windows on one PC: the unfocused one must keep talking
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 120;
            ParseCommandLine();
            SyncFieldsToStrings();
        }

        private void Start() {
            SetupCamera();
            BuildViews();
            if (autoStart) StartSession();
        }

        private void OnDestroy() { StopSession(); }
        private void OnApplicationQuit() { StopSession(); }

        private void ParseCommandLine() {
            var a = Environment.GetCommandLineArgs();
            bool hasPlayer = false, hasLport = false, hasRport = false;
            for (int i = 0; i < a.Length; i++) {
                string next = i + 1 < a.Length ? a[i + 1] : "";
                switch (a[i]) {
                    case "-player": hasPlayer = int.TryParse(next, out playerIndex); break;
                    case "-lport": hasLport = int.TryParse(next, out localPort); break;
                    case "-rport": hasRport = int.TryParse(next, out remotePort); break;
                    case "-rip": remoteIp = next; break;
                    case "-delay": int.TryParse(next, out frameDelay); break;
                    case "-bot": bot = true; break;
                    case "-autostart": autoStart = true; break;
                }
            }
            playerIndex = Mathf.Clamp(playerIndex, 0, 1);
            // "-player 1" alone should just work on localhost: P0 = 7000 -> 7001, P1 = 7001 -> 7000
            if (hasPlayer && !hasLport) localPort = 7000 + playerIndex;
            if (hasPlayer && !hasRport) remotePort = 7000 + (1 - playerIndex);
        }

        private void SyncFieldsToStrings() {
            sLocalPort = localPort.ToString();
            sRemoteIp = remoteIp;
            sRemotePort = remotePort.ToString();
            sDelay = frameDelay.ToString();
        }

        // ============================================================ session

        private void StartSession() {
            error = "";
            int.TryParse(sLocalPort, out localPort);
            int.TryParse(sRemotePort, out remotePort);
            int.TryParse(sDelay, out frameDelay);
            remoteIp = sRemoteIp;
            try {
                if (pluginLog) {
                    pluginLogDelegate = s => Debug.Log("[ggpo] " + s);
                    GGPO.SetLogDelegate(pluginLogDelegate);
                }
                runner = new WasdRunner(playerIndex, localPort, remoteIp, remotePort, Mathf.Max(0, frameDelay));
                runner.OnRollback += HandleRollback;
                clock.Restart();
                nextTickMs = 0;
                lastStats = null;

                if (logToFile) {
                    csvPath = Path.Combine(Application.persistentDataPath, "wasd_rollback_P" + playerIndex + ".csv");
                    File.WriteAllText(csvPath, "time_s,target_frame,load_frame,depth,snap_local_u,snap_remote_u,ping_ms,est_depth_from_ping\n");
                    Debug.Log("[wasd] rollback log: " + csvPath);
                }
            }
            catch (Exception e) {
                error = e.Message;
                Debug.LogException(e);
                StopSession();
            }
        }

        private void StopSession() {
            if (runner != null) {
                runner.OnRollback -= HandleRollback;
                runner.Shutdown();
                runner = null;
            }
            if (pluginLog) GGPO.SetLogDelegate(null);
        }

        // ============================================================ per render-frame

        private void Update() {
            HandleHotkeys();
            TickMarkers(Time.deltaTime);
            if (runner == null) return;

            // 1) network: packets in, confirmations, and (if a prediction was wrong) rollback + re-sim.
            runner.Idle();

            // 2) GGPO timesync: slow down if we are running ahead of the peer.
            double now = clock.Elapsed.TotalMilliseconds;
            if (runner.FramesAhead > 0) {
                nextTickMs += Math.Min(runner.FramesAhead, 8) * FRAME_MS;
                runner.FramesAhead = 0;
            }

            // 3) fixed 60 Hz simulation steps.
            int ticks = 0;
            while (now >= nextTickMs && ticks < 2) {
                runner.Tick(ReadLocalInput());
                nextTickMs += FRAME_MS;
                ticks++;
            }
            if (now - nextTickMs > 250) nextTickMs = now;   // don't build up a catch-up spiral after a stall

            runner.PollFinalizedChecksum();

            if (Time.unscaledTime >= nextStatsAt) {
                nextStatsAt = Time.unscaledTime + 0.25f;
                runner.UpdateNetStats(out lastStats);
            }

            RenderState();
        }

        private void HandleHotkeys() {
            if (KeyPressed(KeyCode.B)) bot = !bot;
            if (KeyPressed(KeyCode.G)) showGhost = !showGhost;
        }

        private long ReadLocalInput() {
            if (bot) return BotInput(runner.State.frame, runner.LocalIndex);
            return ReadKeyboard();
        }

        // Square wave: 40 frames each of D,S,A,W. P1 is half a cycle out of phase, so the two squares
        // run toward each other and bump (exercises the push code) and every direction change is a
        // guaranteed misprediction on the peer.
        private static long BotInput(int frame, int playerIdx) {
            switch (((frame / 40) + playerIdx * 2) & 3) {
                case 0: return WasdSim.INPUT_D;
                case 1: return WasdSim.INPUT_S;
                case 2: return WasdSim.INPUT_A;
                default: return WasdSim.INPUT_W;
            }
        }

        private static long ReadKeyboard() {
            long v = 0;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k == null) return 0;
            if (k.wKey.isPressed || k.upArrowKey.isPressed) v |= WasdSim.INPUT_W;
            if (k.sKey.isPressed || k.downArrowKey.isPressed) v |= WasdSim.INPUT_S;
            if (k.aKey.isPressed || k.leftArrowKey.isPressed) v |= WasdSim.INPUT_A;
            if (k.dKey.isPressed || k.rightArrowKey.isPressed) v |= WasdSim.INPUT_D;
#else
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) v |= WasdSim.INPUT_W;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) v |= WasdSim.INPUT_S;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) v |= WasdSim.INPUT_A;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) v |= WasdSim.INPUT_D;
#endif
            return v;
        }

        private static bool KeyPressed(KeyCode code) {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k == null) return false;
            if (code == KeyCode.B) return k.bKey.wasPressedThisFrame;
            if (code == KeyCode.G) return k.gKey.wasPressedThisFrame;
            return false;
#else
            return Input.GetKeyDown(code);
#endif
        }

        // ============================================================ rollback evidence

        private void HandleRollback(RollbackInfo r) {
            string line = string.Format("f{0}  rewound to f{1}  depth {2}   snap you {3:0.00}u  remote {4:0.00}u",
                r.targetFrame, r.loadFrame, r.depth, r.snapLocal, r.snapRemote);
            rollbackLog.Add(line);
            if (rollbackLog.Count > 7) rollbackLog.RemoveAt(0);

            flash = 0.45f;
            int me = runner.LocalIndex, other = 1 - me;
            AddMarker(r.before.Get(me), r.after.Get(me));
            AddMarker(r.before.Get(other), r.after.Get(other));

            if (logToFile && csvPath != null) {
                try {
                    File.AppendAllText(csvPath, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0:0.000},{1},{2},{3},{4:0.000},{5:0.000},{6},{7}\n",
                        clock.Elapsed.TotalSeconds, r.targetFrame, r.loadFrame, r.depth,
                        r.snapLocal, r.snapRemote, lastStats != null ? lastStats.ping : -1, runner.LatencyFrames));
                }
                catch (Exception) { /* logging is best effort */ }
            }
        }

        private void AddMarker(Vector2Int from, Vector2Int to) {
            Vector3 a = ToWorld(from), b = ToWorld(to);
            float d = Vector3.Distance(a, b);
            if (d < 0.01f) return;     // prediction happened to be right for this square

            var m = new Marker();
            m.box = NewSprite(hollowSprite, RollbackColor, 20);
            m.box.transform.position = a;
            m.line = NewSprite(filledSprite, RollbackColor, 19);
            m.line.transform.position = (a + b) * 0.5f;
            m.line.transform.localScale = new Vector3(d, 0.05f, 1f);
            m.line.transform.rotation = Quaternion.Euler(0, 0, Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg);
            markers.Add(m);
        }

        private void TickMarkers(float dt) {
            for (int i = markers.Count - 1; i >= 0; i--) {
                var m = markers[i];
                m.age += dt;
                if (m.age >= MARKER_LIFE) {
                    Destroy(m.box.gameObject);
                    Destroy(m.line.gameObject);
                    markers.RemoveAt(i);
                    continue;
                }
                float a = 1f - m.age / MARKER_LIFE;
                var c = RollbackColor; c.a = a;
                m.box.color = c;
                m.line.color = c;
            }
        }

        // ============================================================ rendering

        private static Vector3 ToWorld(Vector2Int p) {
            return new Vector3(p.x / (float)WasdSim.UNIT, p.y / (float)WasdSim.UNIT, 0f);
        }

        private void RenderState() {
            int me = runner.LocalIndex;
            for (int i = 0; i < 2; i++) {
                playerSR[i].transform.position = ToWorld(runner.State.Get(i));
                playerSR[i].gameObject.SetActive(true);
            }

            // Ghost = the newest frame whose inputs are ALL real (head - measured latency). Before the session
            // is Running there are no stats, LatencyFrames is 0 and the ghost sits exactly under the PlayerView.
            int g = runner.ConfirmedFrame;
            bool haveGhost = showGhost && runner.History[g & (WasdRunner.HISTORY - 1)].frame == g;
            for (int i = 0; i < 2; i++) {
                ghostSR[i].gameObject.SetActive(haveGhost);
                lagLineSR[i].gameObject.SetActive(haveGhost);
                if (!haveGhost) continue;
                Vector3 gp = ToWorld(runner.History[g & (WasdRunner.HISTORY - 1)].Get(i));
                Vector3 pp = ToWorld(runner.State.Get(i));
                ghostSR[i].transform.position = gp;

                float d = Vector3.Distance(gp, pp);       // faint line ghost -> PlayerView == the predicted part
                lagLineSR[i].gameObject.SetActive(d > 0.02f);
                lagLineSR[i].transform.position = (gp + pp) * 0.5f;
                lagLineSR[i].transform.localScale = new Vector3(d, 0.04f, 1f);
                lagLineSR[i].transform.rotation = Quaternion.Euler(0, 0, Mathf.Atan2(pp.y - gp.y, pp.x - gp.x) * Mathf.Rad2Deg);
            }
            // Fixed colors by player index (not by local/remote).
            playerSR[0].color = Player0Color;
            playerSR[1].color = Player1Color;
            ghostSR[0].color = Player0Color;
            ghostSR[1].color = Player1Color;
            lagLineSR[0].color = new Color(Player0Color.r, Player0Color.g, Player0Color.b, 0.5f);
            lagLineSR[1].color = new Color(Player1Color.r, Player1Color.g, Player1Color.b, 0.5f);

            // arena border flashes red whenever a rollback just happened
            flash = Mathf.Max(0f, flash - Time.deltaTime);
            Color wc = Color.Lerp(WallColor, RollbackColor, Mathf.Clamp01(flash / 0.45f));
            for (int i = 0; i < borders.Count; i++) borders[i].color = wc;
        }

        private void SetupCamera() {
            var cam = Camera.main;
            if (cam == null) {
                var go = new GameObject("WasdCamera");
                go.tag = "MainCamera";
                cam = go.AddComponent<Camera>();
            }
            cam.orthographic = true;
            cam.orthographicSize = 5.2f;
            cam.transform.position = new Vector3(0, 0, -10);
            cam.transform.rotation = Quaternion.identity;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.08f, 0.1f);
        }

        private void BuildViews() {
            filledSprite = MakeSprite(false);
            hollowSprite = MakeSprite(true);

            for (int i = 0; i < 2; i++) {
                playerSR[i] = NewSprite(filledSprite, Color.white, 10);
                playerSR[i].name = "PlayerView" + i;
                ghostSR[i] = NewSprite(hollowSprite, Color.white, 5);
                ghostSR[i].name = "Ghost" + i;
                ghostSR[i].transform.localScale = Vector3.one * 1.12f;
                lagLineSR[i] = NewSprite(filledSprite, Color.white, 4);
                lagLineSR[i].name = "LagLine" + i;
                playerSR[i].gameObject.SetActive(false);
                ghostSR[i].gameObject.SetActive(false);
                lagLineSR[i].gameObject.SetActive(false);
            }

            // arena border
            float w = WasdSim.HALF_W / (float)WasdSim.UNIT, h = WasdSim.HALF_H / (float)WasdSim.UNIT, t = 0.06f;
            Border(new Vector3(0, h, 0), new Vector3(2 * w, t, 1));
            Border(new Vector3(0, -h, 0), new Vector3(2 * w, t, 1));
            Border(new Vector3(w, 0, 0), new Vector3(t, 2 * h, 1));
            Border(new Vector3(-w, 0, 0), new Vector3(t, 2 * h, 1));
        }

        private void Border(Vector3 pos, Vector3 scale) {
            var sr = NewSprite(filledSprite, WallColor, 0);
            sr.transform.position = pos;
            sr.transform.localScale = scale;
            borders.Add(sr);
        }

        private SpriteRenderer NewSprite(Sprite s, Color c, int order) {
            var go = new GameObject("s");
            go.transform.SetParent(transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = s;
            sr.color = c;
            sr.sortingOrder = order;
            return sr;
        }

        // 32px sprite, 32 ppu -> exactly 1 world unit. Hollow = 3px outline.
        private static Sprite MakeSprite(bool hollow) {
            const int N = 32, B = 3;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++) {
                    bool edge = x < B || y < B || x >= N - B || y >= N - B;
                    px[y * N + x] = (!hollow || edge) ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
                }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), N);
        }

        // ============================================================ IMGUI (setup panel + HUD)

        private void OnGUI() {
            if (hudStyle == null) {
                hudStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true, wordWrap = false };
                hudStyle.normal.textColor = Color.white;
                boxStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, richText = true, fontSize = 14 };
            }
            if (runner == null) DrawSetup();
            else DrawHud();
        }

        private void DrawSetup() {
            GUILayout.BeginArea(new Rect(20, 20, 340, 330), GUI.skin.box);
            GUILayout.Label("<b>WASD GGPO demo</b>", hudStyle);
            GUILayout.Label("I am player:", hudStyle);
            int p = GUILayout.SelectionGrid(playerIndex, new[] { "P0 (host side)", "P1 (join side)" }, 2);
            if (p != playerIndex) {            // swap the default localhost ports along with the side
                playerIndex = p;
                int a = 7000 + p, b = 7000 + (1 - p);
                sLocalPort = a.ToString();
                sRemotePort = b.ToString();
            }
            GUILayout.Label("local port", hudStyle);
            sLocalPort = GUILayout.TextField(sLocalPort);
            GUILayout.Label("remote ip", hudStyle);
            sRemoteIp = GUILayout.TextField(sRemoteIp);
            GUILayout.Label("remote port", hudStyle);
            sRemotePort = GUILayout.TextField(sRemotePort);
            GUILayout.Label("local frame delay", hudStyle);
            sDelay = GUILayout.TextField(sDelay);
            bot = GUILayout.Toggle(bot, " bot input (same as key B)");
            if (GUILayout.Button("Start GGPO session", GUILayout.Height(30))) StartSession();
            if (error.Length > 0) GUILayout.Label("<color=#ff6060>" + error + "</color>", hudStyle);
            GUILayout.EndArea();
        }

        private void DrawHud() {
            var sb = new StringBuilder(512);
            int head = runner.State.frame;
            string p0 = "#4aa6ff", p1 = "#ff9a26", red = "#ff4050";

            sb.AppendLine("<b>WASD GGPO</b>   you = P" + runner.LocalIndex + "   <b>" + runner.Status + "</b>"
                + (bot ? "   <color=#ffe060>[BOT]</color>" : ""));
            sb.AppendLine("frame " + head + "    ghost frame " + runner.ConfirmedFrame
                + "    local delay " + runner.FrameDelay + "f");
            if (lastStats != null) {
                int raw = runner.OneWayFrames - runner.FrameDelay;
                sb.AppendLine("ping " + lastStats.ping + " ms (avg " + runner.PingMs.ToString("0") + ")  one-way ~" + runner.OneWayFrames
                    + "f  =>  <b>predicting ~" + runner.LatencyFrames + "f ahead of ghost</b>"
                    + (raw > runner.LatencyFrames ? "  <color=#ffe060>(capped by " + WasdRunner.FinalLag + "f barrier)</color>" : ""));
            }
            else sb.AppendLine("ping: waiting for stats...");
            sb.AppendLine("<b>rollbacks " + runner.Rollbacks + "</b>   visible snaps " + runner.VisibleSnaps
                + "   last depth " + runner.LastDepth + "f   max " + runner.MaxDepth + "f   resim " + runner.ResimFrames + "f");
            sb.AppendLine("prediction-barrier stalls " + runner.Stalls);
            sb.AppendLine("last snap  you " + runner.LastSnapLocal.ToString("0.00") + "u   remote "
                + runner.LastSnapRemote.ToString("0.00") + "u   (max remote " + runner.MaxSnapRemote.ToString("0.00") + "u)");
            sb.AppendLine("final-frame violations " + (runner.FinalViolations == 0
                ? "<color=#60e080>0 (ok)</color>" : "<color=" + red + ">" + runner.FinalViolations + " (plugin window > " + WasdRunner.FinalLag + "f!)</color>"));
            if (runner.PeriodicFrame >= 0)
                sb.AppendLine("final checksum f" + runner.PeriodicFrame + " = " + runner.PeriodicChecksum.ToString("X8")
                    + "   <i>(must match on both peers)</i>");

            GUI.Box(new Rect(10, 10, 700, 170), sb.ToString(), boxStyle);

            string legend =
                "<color=" + p0 + ">■</color> P0 (blue)   <color=" + p1 + ">■</color> P1 (orange)   " +
                "□ ghost = newest frame with real inputs (head - latency), line = predicted part\n" +
                "<color=" + red + ">▭</color> where it was drawn before a rollback (only when a rollback moved it), border flashes red   " +
                "WASD move   B bot   G ghost";
            GUI.Box(new Rect(10, Screen.height - 50, 980, 42), legend, boxStyle);

            if (rollbackLog.Count > 0) {
                var lb = new StringBuilder();
                lb.AppendLine("<b>recent rollbacks</b>");
                for (int i = rollbackLog.Count - 1; i >= 0; i--) lb.AppendLine(rollbackLog[i]);
                GUI.Box(new Rect(10, Screen.height - 50 - 24 - 20 * rollbackLog.Count, 560, 24 + 20 * rollbackLog.Count),
                    lb.ToString(), boxStyle);
            }
        }
    }
}
