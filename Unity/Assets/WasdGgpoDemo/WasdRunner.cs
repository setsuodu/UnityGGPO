using System;
using Unity.Collections;
using UnityEngine;
using UnityGGPO;

namespace WasdDemo {

    /// <summary>One completed rollback + re-simulation, as seen from the game's side.</summary>
    public struct RollbackInfo {
        public int loadFrame;       // frame GGPO rewound to (first mispredicted frame)
        public int targetFrame;     // frame we were at when the rollback started (== frame after resim)
        public int depth;           // targetFrame - loadFrame  (= frames re-simulated)
        public WasdState before;    // what the player SAW (predicted) at targetFrame
        public WasdState after;     // corrected state at targetFrame
        public float snapLocal;     // world units the local square jumped
        public float snapRemote;    // world units the remote square jumped
    }

    /// <summary>
    /// Minimal GGPO driver on top of UnityGGPO.GGPO.Session (2 players, p2p).
    /// Everything GGPO does to the game goes through the callbacks below, so this is
    /// also where rollbacks are observed:  OnLoadGameState == "a prediction was wrong".
    /// </summary>
    public sealed class WasdRunner {
        public const int HISTORY = 64;                                  // ring buffer, power of 2
        // Anything older than head - FinalLag can never be rolled back (GGPO prediction window).
        // Only used for the desync checksum + self-check, NOT for the ghost (the ghost follows real latency).
        public static readonly int FinalLag = GGPO.MAX_PREDICTION_FRAMES;
        private const double FRAME_MS = 1000.0 / 60.0;

        // ---- game ----
        public WasdState State;                                         // current (speculative) state
        public readonly WasdState[] History = new WasdState[HISTORY];   // History[f & 63] = state after frame f

        public readonly int LocalIndex;
        private readonly int localHandle;
        private readonly int remoteHandle;

        // ---- session status ----
        public bool Running;
        public bool PeerDisconnected;
        public string Status = "Connecting...";
        public int FramesAhead;                                         // set by GGPO timesync event

        // ---- rollback stats ----
        public int Rollbacks;
        public int VisibleSnaps;
        public int LastDepth, MaxDepth, ResimFrames;
        public int Stalls;                                              // AddLocalInput -> PREDICTION_THRESHOLD
        public int FinalViolations;                                     // see OnLoadGameState

        // ---- latency-driven ghost ----
        // The remote input for frame f is sent when the peer's head is f - delay and arrives one-way-latency later,
        // when (clocks aligned by GGPO timesync) our head is ~ f - delay + oneWay. So the newest frame whose inputs
        // are ALL real is  C = head - (oneWay - delay),  and everything after C is prediction.
        // oneWay comes from GGPO's measured round trip (i.e. whatever clumsy is doing right now).
        public readonly int FrameDelay;                                 // assumed equal on both peers
        public float PingMs = -1;                                       // smoothed GGPO round-trip
        public int OneWayFrames;                                        // ping/2 in frames
        public int LatencyFrames;                                       // est. head - C, clamped to the barrier (0..7)
        public int ConfirmedFrame { get { return Math.Max(0, State.frame - LatencyFrames); } }
        public float LastSnapLocal, LastSnapRemote, MaxSnapRemote;
        public int PeriodicFrame = -1;
        public uint PeriodicChecksum;

        public event Action<RollbackInfo> OnRollback;

        private int resimTarget = -1;
        private WasdState preRollback;
        private int loadedFrame;
        private int lastFinalChecked = -1;

        public WasdRunner(int localIndex, int localPort, string remoteIp, int remotePort, int frameDelay) {
            LocalIndex = localIndex;
            FrameDelay = Math.Max(0, frameDelay);
            State = WasdSim.Initial();
            for (int i = 0; i < HISTORY; i++) History[i] = new WasdState { frame = -1 };
            History[0] = State;

            Check(GGPO.Session.StartSession(
                OnBeginGame, OnAdvanceFrame, OnLoadGameState, OnLogGameState, OnSaveGameState, OnFreeBuffer,
                OnConnected, OnSynchronizing, OnSynchronized, OnRunning,
                OnInterrupted, OnResumed, OnDisconnected, OnTimesync,
                "wasddemo", WasdSim.NUM_PLAYERS, localPort), "StartSession", true);

            GGPO.Session.SetDisconnectTimeout(5000);
            GGPO.Session.SetDisconnectNotifyStart(1500);

            for (int i = 0; i < WasdSim.NUM_PLAYERS; i++) {
                var p = new GGPOPlayer { player_num = i + 1 };   // player_num is 1-based; inputs[i] == player_num i+1
                if (i == localIndex) {
                    p.type = GGPOPlayerType.GGPO_PLAYERTYPE_LOCAL;
                    p.ip_address = "";
                    p.port = 0;
                    Check(GGPO.Session.AddPlayer(p, out localHandle), "AddPlayer(local)", true);
                    Check(GGPO.Session.SetFrameDelay(localHandle, frameDelay), "SetFrameDelay", false);
                }
                else {
                    p.type = GGPOPlayerType.GGPO_PLAYERTYPE_REMOTE;
                    p.ip_address = remoteIp;
                    p.port = (ushort)remotePort;
                    Check(GGPO.Session.AddPlayer(p, out remoteHandle), "AddPlayer(remote)", true);
                }
            }
        }

        // ================= driven by the MonoBehaviour =================

        /// <summary>Pump the network. This is where rollbacks (load + re-sim callbacks) happen.</summary>
        public void Idle() {
            GGPO.Session.Idle(0);
        }

        /// <summary>Try to advance one frame with this local input.</summary>
        public bool Tick(long localInput) {
            int r = GGPO.Session.AddLocalInput(localHandle, localInput);
            if (!GGPO.SUCCEEDED(r)) {
                if (r == GGPO.ERRORCODE_PREDICTION_THRESHOLD) Stalls++;   // too far ahead of the peer
                return false;                                             // (also NOT_SYNCHRONIZED while connecting)
            }
            try {
                var inputs = GGPO.Session.SynchronizeInput(WasdSim.NUM_PLAYERS, out _);
                Advance(inputs);
                return true;
            }
            catch (Exception e) {
                Debug.LogException(e);
                return false;
            }
        }

        public bool TryGetStats(out GGPONetworkStats stats) {
            stats = null;
            if (!Running || PeerDisconnected) return false;
            return GGPO.SUCCEEDED(GGPO.Session.GetNetworkStats(remoteHandle, out stats));
        }

        /// <summary>Poll GGPO's network stats and refresh the latency estimate that drives the ghost.</summary>
        public bool UpdateNetStats(out GGPONetworkStats stats) {
            if (!TryGetStats(out stats)) return false;
            PingMs = PingMs < 0 ? stats.ping : PingMs * 0.7f + stats.ping * 0.3f;   // ping samples are jittery
            OneWayFrames = Mathf.RoundToInt(PingMs * 0.5f / (float)FRAME_MS);
            LatencyFrames = Mathf.Clamp(OneWayFrames - FrameDelay, 0, GGPO.MAX_PREDICTION_FRAMES - 1);
            return true;
        }

        /// <summary>
        /// Checksum of the newest FINALIZED frame that is a multiple of 90.
        /// Both peers must show identical (frame, checksum) pairs; any difference == desync.
        /// </summary>
        public void PollFinalizedChecksum() {
            int g = State.frame - FinalLag;
            for (int f = Math.Max(lastFinalChecked + 1, g - 40); f <= g; f++) {
                if (f > 0 && f % 90 == 0) {
                    var h = History[f & (HISTORY - 1)];
                    if (h.frame == f) { PeriodicFrame = f; PeriodicChecksum = WasdSim.Checksum(h); }
                }
                lastFinalChecked = f;
            }
        }

        public void Shutdown() {
            if (GGPO.Session.IsStarted()) GGPO.Session.CloseSession();
        }

        // ================= the one place the game advances =================

        // Called both for normal frames (from Tick) and for every re-simulated frame
        // during a rollback (from GGPO, via OnAdvanceFrame).
        private void Advance(long[] inputs) {
            WasdSim.Step(ref State, inputs[0], inputs[1]);
            History[State.frame & (HISTORY - 1)] = State;      // re-sim overwrites the mispredicted entries

            if (resimTarget >= 0 && State.frame == resimTarget) FinishRollback();

            Check(GGPO.Session.AdvanceFrame(), "AdvanceFrame", false);
        }

        private void FinishRollback() {
            resimTarget = -1;
            var info = new RollbackInfo {
                loadFrame = loadedFrame,
                targetFrame = State.frame,
                depth = State.frame - loadedFrame,
                before = preRollback,
                after = State,
            };
            int me = LocalIndex, other = 1 - LocalIndex;
            info.snapLocal = Dist(preRollback.Get(me), State.Get(me));
            info.snapRemote = Dist(preRollback.Get(other), State.Get(other));

            LastSnapLocal = info.snapLocal;
            LastSnapRemote = info.snapRemote;
            MaxSnapRemote = Mathf.Max(MaxSnapRemote, info.snapRemote);
            if (info.snapLocal > 0.001f || info.snapRemote > 0.001f) VisibleSnaps++;

            OnRollback?.Invoke(info);
        }

        private static float Dist(Vector2Int a, Vector2Int b) {
            return Vector2.Distance(new Vector2(a.x, a.y), new Vector2(b.x, b.y)) / WasdSim.UNIT;
        }

        // ================= GGPO callbacks (called from native, never throw) =================

        private bool OnBeginGame(string name) { return true; }

        private bool OnAdvanceFrame(int flags) {
            try {
                var inputs = GGPO.Session.SynchronizeInput(WasdSim.NUM_PLAYERS, out _);
                Advance(inputs);
                return true;
            }
            catch (Exception e) { Debug.LogException(e); return false; }
        }

        // GGPO says: "frame N turned out to be predicted wrong, go back to the state saved at N".
        private bool OnLoadGameState(NativeArray<byte> data) {
            try {
                var pre = State;                         // what was on screen (speculative)
                var loaded = WasdSim.FromBytes(data);
                State = loaded;

                int depth = pre.frame - loaded.frame;
                if (depth > 0) {
                    Rollbacks++;
                    LastDepth = depth;
                    MaxDepth = Math.Max(MaxDepth, depth);
                    ResimFrames += depth;

                    // History[head - FinalLag] may only change if we are rewound to an EARLIER frame.
                    // If this ever fires, FinalLag is smaller than the plugin's real prediction window,
                    // so the periodic checksums are not truly "finalized".
                    if (loaded.frame < pre.frame - FinalLag) FinalViolations++;

                    preRollback = pre;
                    loadedFrame = loaded.frame;
                    resimTarget = pre.frame;
                }
                return true;
            }
            catch (Exception e) { Debug.LogException(e); return false; }
        }

        private bool OnSaveGameState(out NativeArray<byte> data, out int checksum, int frame) {
            data = WasdSim.ToBytes(State);
            checksum = Utils.CalcFletcher32(data);
            return true;
        }

        private bool OnLogGameState(string filename, NativeArray<byte> data) { return true; }

        private void OnFreeBuffer(NativeArray<byte> data) {
            if (data.IsCreated) data.Dispose();
        }

        // ---- events ----

        private bool OnConnected(int player) { Status = "Peer found, synchronizing..."; return true; }

        private bool OnSynchronizing(int player, int count, int total) {
            Status = "Synchronizing " + (100 * count / Math.Max(1, total)) + "%";
            return true;
        }

        private bool OnSynchronized(int player) { Status = "Synchronized"; return true; }

        private bool OnRunning() { Running = true; Status = "Running"; return true; }

        private bool OnInterrupted(int player, int timeout) { Status = "Connection interrupted (" + timeout + "ms to drop)"; return true; }

        private bool OnResumed(int player) { Status = "Running"; return true; }

        private bool OnDisconnected(int player) { PeerDisconnected = true; Status = "Peer disconnected"; return true; }

        private bool OnTimesync(int framesAhead) { FramesAhead = framesAhead; return true; }

        private static void Check(int result, string what, bool throwOnFail) {
            if (GGPO.SUCCEEDED(result)) return;
            string msg = what + " failed: " + GGPO.GetErrorCodeMessage(result);
            if (throwOnFail) throw new Exception(msg);
            Debug.LogWarning(msg);
        }
    }
}
