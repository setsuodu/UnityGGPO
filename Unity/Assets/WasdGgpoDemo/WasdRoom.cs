using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace WasdDemo {

    /// <summary>
    /// Minimal 2-player TCP room (Street Fighter "room spawner" concept).
    ///
    /// Host (P0): listens, accepts up to 2 TCP clients, when full broadcasts:
    ///   "READY|&lt;ip0&gt;|&lt;ip1&gt;|&lt;port0&gt;|&lt;port1&gt;"
    /// so each peer can pick the other IP and start GGPO.
    ///
    /// Client (P1): connects to RoomHostIp:RoomPort, reports its LAN IP,
    /// waits for READY, then fires OnReady(peerIp, myIndex, peerGgpoPort).
    ///
    /// Default room address is hardcoded; can be changed at runtime before StartHost/StartClient.
    /// Does NOT run gameplay — only endpoint exchange. GGPO stays UDP/P2P.
    /// </summary>
    public sealed class WasdRoom : IDisposable {

        public const string DefaultRoomHostIp = "192.168.1.101";
        public const int DefaultRoomPort = 9000;
        public const int DefaultGgpoPortP0 = 7000;
        public const int DefaultGgpoPortP1 = 7001;

        /// <summary>Called on the Unity main thread when both players are in and IPs are known.</summary>
        public event Action<RoomReadyInfo> OnReady;

        /// <summary>Status line for UI.</summary>
        public string Status { get; private set; } = "Idle";

        public string RoomHostIp = DefaultRoomHostIp;
        public int RoomPort = DefaultRoomPort;
        public int GgpoPortP0 = DefaultGgpoPortP0;
        public int GgpoPortP1 = DefaultGgpoPortP1;

        /// <summary>This machine's LAN IPv4 used when reporting to the room. Auto-detected if empty.</summary>
        public string LocalLanIp = "";

        public bool IsHost { get; private set; }
        public bool IsRunning { get; private set; }

        private TcpListener _listener;
        private readonly List<ClientSlot> _slots = new List<ClientSlot>(2);
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private Thread _acceptThread;
        private volatile bool _stop;

        private struct ClientSlot {
            public TcpClient Tcp;
            public NetworkStream Stream;
            public string ReportedIp;
            public int Index; // 0 = first join = P0, 1 = second = P1
        }

        public struct RoomReadyInfo {
            public int LocalIndex;   // 0 or 1
            public string PeerIp;
            public int LocalGgpoPort;
            public int PeerGgpoPort;
            public string PeerGgpoIp; // same as PeerIp
        }

        // ------------------------------------------------------------------ public API

        /// <summary>P0: start TCP listen. Call from Unity main thread.</summary>
        public void StartHost() {
            if (IsRunning) throw new InvalidOperationException("Room already running");
            IsHost = true;
            IsRunning = true;
            _stop = false;
            if (string.IsNullOrEmpty(LocalLanIp)) LocalLanIp = DetectLanIp();

            try {
                _listener = new TcpListener(IPAddress.Any, RoomPort);
                _listener.Start();
                Status = "Host listening on *:" + RoomPort + "  (my LAN " + LocalLanIp + ")";
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "WasdRoom.Accept" };
                _acceptThread.Start();
            }
            catch (Exception e) {
                IsRunning = false;
                Status = "Host failed: " + e.Message;
                throw;
            }
        }

        /// <summary>P1 (or any joiner): connect to RoomHostIp:RoomPort. Call from Unity main thread.</summary>
        public void StartClient() {
            if (IsRunning) throw new InvalidOperationException("Room already running");
            IsHost = false;
            IsRunning = true;
            _stop = false;
            if (string.IsNullOrEmpty(LocalLanIp)) LocalLanIp = DetectLanIp();

            Status = "Connecting to " + RoomHostIp + ":" + RoomPort + " ...";
            var t = new Thread(ClientConnectLoop) { IsBackground = true, Name = "WasdRoom.Client" };
            t.Start();
        }

        /// <summary>Must be called every frame from a MonoBehaviour Update so OnReady runs on main thread.</summary>
        public void Pump() {
            while (_mainThread.TryDequeue(out var a)) {
                try { a(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        public void Stop() {
            _stop = true;
            IsRunning = false;
            try { _listener?.Stop(); } catch { /* ignore */ }
            _listener = null;
            lock (_slots) {
                foreach (var s in _slots) {
                    try { s.Stream?.Close(); } catch { }
                    try { s.Tcp?.Close(); } catch { }
                }
                _slots.Clear();
            }
            Status = "Stopped";
        }

        public void Dispose() { Stop(); }

        // ------------------------------------------------------------------ host

        private void AcceptLoop() {
            try {
                while (!_stop && _slots.Count < 2) {
                    TcpClient tcp;
                    try {
                        tcp = _listener.AcceptTcpClient();
                    }
                    catch (SocketException) {
                        if (_stop) break;
                        continue;
                    }
                    catch (ObjectDisposedException) { break; }

                    if (_slots.Count >= 2) {
                        try { tcp.Close(); } catch { }
                        continue;
                    }

                    var stream = tcp.GetStream();
                    stream.ReadTimeout = 15000;
                    string reported;
                    try {
                        reported = ReadLine(stream);
                    }
                    catch (Exception e) {
                        Post(() => Status = "Read IP failed: " + e.Message);
                        try { tcp.Close(); } catch { }
                        continue;
                    }

                    if (string.IsNullOrEmpty(reported)) {
                        // fallback: remote endpoint (may be wrong behind NAT)
                        try {
                            reported = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address.ToString();
                        }
                        catch {
                            reported = "0.0.0.0";
                        }
                    }

                    ClientSlot slot;
                    lock (_slots) {
                        slot = new ClientSlot {
                            Tcp = tcp,
                            Stream = stream,
                            ReportedIp = reported.Trim(),
                            Index = _slots.Count
                        };
                        _slots.Add(slot);
                    }

                    int n = _slots.Count;
                    Post(() => Status = "Players " + n + "/2  last=" + slot.ReportedIp);

                    if (n >= 2) {
                        BroadcastReady();
                        break;
                    }
                }
            }
            catch (Exception e) {
                if (!_stop) Post(() => { Status = "Accept error: " + e.Message; IsRunning = false; });
            }
        }

        private void BroadcastReady() {
            string ip0, ip1;
            lock (_slots) {
                ip0 = _slots[0].ReportedIp;
                ip1 = _slots[1].ReportedIp;
            }
            // Protocol: READY|ip0|ip1|ggpoPort0|ggpoPort1
            string msg = "READY|" + ip0 + "|" + ip1 + "|" + GgpoPortP0 + "|" + GgpoPortP1;
            byte[] bytes = Encoding.UTF8.GetBytes(msg + "\n");

            lock (_slots) {
                for (int i = 0; i < _slots.Count; i++) {
                    try {
                        _slots[i].Stream.Write(bytes, 0, bytes.Length);
                        _slots[i].Stream.Flush();
                    }
                    catch (Exception e) {
                        int idx = i;
                        Post(() => Status = "Broadcast to P" + idx + " failed: " + e.Message);
                    }
                }
            }

            // Host process is also a game peer only if it connected as a client to itself.
            // Typical setup: Host machine runs room + one game client that also StartClient to 127.0.0.1
            // OR host game embeds room and registers itself as slot 0 without TCP.
            // Here we only notify via TCP; the Host *game* should either:
            //   (A) also call StartClient() to 127.0.0.1 after StartHost(), or
            //   (B) use NotifyLocalHostReady if room and P0 share process.
            Post(() => Status = "Room full → " + msg);
        }

        /// <summary>
        /// If P0 game and TCP room share one process: after StartHost, call this once so
        /// local P0 also gets OnReady without connecting to itself over TCP.
        /// Pass the IP you want other peers to use for you (usually LocalLanIp).
        /// Second player must still Join via TCP.
        /// </summary>
        public void RegisterLocalHostAsPlayer0() {
            if (!IsHost) throw new InvalidOperationException("Only host can register local P0");
            if (string.IsNullOrEmpty(LocalLanIp)) LocalLanIp = DetectLanIp();

            lock (_slots) {
                if (_slots.Count > 0) return; // already has someone
                // Synthetic slot without real TCP — only for ordering; Broadcast still needs 2 TCP clients
                // Simpler approach for shared process: treat host as already "in" and wait for 1 remote.
            }
            // Use dedicated path: host waits for exactly 1 remote TCP client, then ready with [LocalLanIp, remote]
            // Handled by AcceptLoopHostWithLocalP0 if you set PreferLocalHostPlayer0 = true.
        }

        /// <summary>
        /// Host-in-same-process mode: local player is always P0.
        /// Only ONE remote TCP client is required; then OnReady fires for local + we send READY to remote.
        /// Call StartHostLocalP0() instead of StartHost() for this mode.
        /// </summary>
        public void StartHostLocalP0() {
            if (IsRunning) throw new InvalidOperationException("Room already running");
            IsHost = true;
            IsRunning = true;
            _stop = false;
            if (string.IsNullOrEmpty(LocalLanIp)) LocalLanIp = DetectLanIp();

            Log("HOST StartHostLocalP0  bind=*:" + RoomPort + "  myLan=" + LocalLanIp
                + "  ggpoPorts=" + GgpoPortP0 + "/" + GgpoPortP1);

            try {
                _listener = new TcpListener(IPAddress.Any, RoomPort);
                _listener.Start();
                Status = "Host(P0) listening *:" + RoomPort + "  my LAN " + LocalLanIp;
                Log("HOST TcpListener STARTED on port " + RoomPort);
                _acceptThread = new Thread(AcceptLoopLocalP0) { IsBackground = true, Name = "WasdRoom.AcceptP0" };
                _acceptThread.Start();
            }
            catch (Exception e) {
                IsRunning = false;
                Status = "Host failed: " + e.Message;
                Log("HOST LISTEN FAILED: " + e);
                throw;
            }
        }

        private void AcceptLoopLocalP0() {
            Log("HOST accept thread running, waiting for 1 client...");
            try {
                while (!_stop) {
                    TcpClient tcp;
                    try {
                        tcp = _listener.AcceptTcpClient();
                    }
                    catch (Exception e) {
                        if (_stop) break;
                        Log("HOST Accept exception: " + e.Message);
                        continue;
                    }

                    string ep = "?";
                    try { ep = tcp.Client.RemoteEndPoint != null ? tcp.Client.RemoteEndPoint.ToString() : "?"; }
                    catch { }
                    Log("HOST accepted TCP from " + ep);

                    var stream = tcp.GetStream();
                    stream.ReadTimeout = 15000;
                    string remoteIp;
                    try {
                        remoteIp = ReadLine(stream);
                        Log("HOST recv client LAN report: \"" + remoteIp + "\"");
                    }
                    catch (Exception e) {
                        Log("HOST ReadLine failed: " + e.Message);
                        try { tcp.Close(); } catch { }
                        continue;
                    }
                    if (string.IsNullOrEmpty(remoteIp)) {
                        try { remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address.ToString(); }
                        catch { remoteIp = "0.0.0.0"; }
                        Log("HOST empty report, fallback RemoteEndPoint IP=" + remoteIp);
                    }
                    remoteIp = remoteIp.Trim();

                    // Same-machine: client often reports same LAN IP as host, or connected via 127.0.0.1
                    bool loopbackConn = false;
                    try {
                        var a = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
                        loopbackConn = IPAddress.IsLoopback(a);
                    }
                    catch { }
                    string peerForHost = remoteIp;
                    if (loopbackConn || IpsEqual(remoteIp, LocalLanIp) || IsLoopbackIp(remoteIp)) {
                        peerForHost = "127.0.0.1";
                        Log("HOST same-machine detected (loopbackConn=" + loopbackConn
                            + " sameLan=" + IpsEqual(remoteIp, LocalLanIp) + ") → peer GGPO IP forced 127.0.0.1");
                    }

                    // Tell client: ip0=host, ip1=what client reported (client uses this to know who is who)
                    // Also pass a flag for same-machine via using 127.0.0.1 as host side when loopback
                    string hostIpForMsg = loopbackConn ? "127.0.0.1" : LocalLanIp;
                    string clientIpForMsg = (loopbackConn || IpsEqual(remoteIp, LocalLanIp)) ? "127.0.0.1" : remoteIp;
                    string msg = "READY|" + hostIpForMsg + "|" + clientIpForMsg + "|" + GgpoPortP0 + "|" + GgpoPortP1;
                    Log("HOST send → " + msg);
                    byte[] bytes = Encoding.UTF8.GetBytes(msg + "\n");
                    try {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                        Log("HOST READY bytes written OK");
                    }
                    catch (Exception e) {
                        Log("HOST Send READY failed: " + e.Message);
                        Post(() => Status = "Send READY failed: " + e.Message);
                        try { tcp.Close(); } catch { }
                        continue;
                    }

                    var localInfo = new RoomReadyInfo {
                        LocalIndex = 0,
                        PeerIp = peerForHost,
                        LocalGgpoPort = GgpoPortP0,
                        PeerGgpoPort = GgpoPortP1,
                        PeerGgpoIp = peerForHost
                    };
                    Log("HOST OnReady P0  peerIp=" + peerForHost
                        + "  localGgpo=" + GgpoPortP0 + "  peerGgpo=" + GgpoPortP1);
                    Post(() => {
                        Status = "Ready as P0  peer=" + peerForHost;
                        OnReady?.Invoke(localInfo);
                    });
                    break;
                }
            }
            catch (Exception e) {
                Log("HOST accept loop error: " + e);
                if (!_stop) Post(() => { Status = "Host loop error: " + e.Message; IsRunning = false; });
            }
        }

        // ------------------------------------------------------------------ client

        private void ClientConnectLoop() {
            Log("CLIENT connecting to " + RoomHostIp + ":" + RoomPort + "  myLan=" + LocalLanIp);
            try {
                using (var tcp = new TcpClient()) {
                    tcp.Connect(RoomHostIp, RoomPort);
                    string lep = "?";
                    try { lep = tcp.Client.LocalEndPoint != null ? tcp.Client.LocalEndPoint.ToString() : "?"; } catch { }
                    Log("CLIENT TCP connected  localEP=" + lep + "  remote=" + RoomHostIp + ":" + RoomPort);

                    var stream = tcp.GetStream();
                    stream.ReadTimeout = 60000;
                    stream.WriteTimeout = 10000;

                    Log("CLIENT send LAN report: \"" + LocalLanIp + "\"");
                    WriteLine(stream, LocalLanIp);

                    Post(() => Status = "Joined room, waiting for READY...");
                    Log("CLIENT waiting for READY line...");

                    string line = ReadLine(stream);
                    Log("CLIENT recv: \"" + line + "\"");
                    if (string.IsNullOrEmpty(line) || !line.StartsWith("READY|")) {
                        Log("CLIENT bad message (not READY)");
                        Post(() => { Status = "Bad room message: " + line; IsRunning = false; });
                        return;
                    }

                    var parts = line.Split('|');
                    if (parts.Length < 5) {
                        Log("CLIENT malformed READY parts=" + parts.Length);
                        Post(() => { Status = "Malformed READY"; IsRunning = false; });
                        return;
                    }

                    string ip0 = parts[1].Trim();
                    string ip1 = parts[2].Trim();
                    int.TryParse(parts[3], out int port0);
                    int.TryParse(parts[4], out int port1);
                    if (port0 <= 0) port0 = GgpoPortP0;
                    if (port1 <= 0) port1 = GgpoPortP1;
                    Log("CLIENT parse ip0=" + ip0 + " ip1=" + ip1 + " port0=" + port0 + " port1=" + port1
                        + "  myLan=" + LocalLanIp);

                    // Join path is always P1 when using StartHostLocalP0 on the other side
                    int localIndex = 1;
                    string peerIp = ip0;
                    int localPort = port1;
                    int peerPort = port0;

                    // Same-machine: both ends should use 127.0.0.1 for GGPO
                    if (IsLoopbackIp(ip0) || IsLoopbackIp(ip1) || IpsEqual(ip0, ip1)
                        || IpsEqual(ip0, LocalLanIp) || RoomHostIp == "127.0.0.1" || RoomHostIp == "localhost") {
                        peerIp = "127.0.0.1";
                        Log("CLIENT same-machine → peer GGPO IP forced 127.0.0.1");
                    }

                    var info = new RoomReadyInfo {
                        LocalIndex = localIndex,
                        PeerIp = peerIp,
                        LocalGgpoPort = localPort,
                        PeerGgpoPort = peerPort,
                        PeerGgpoIp = peerIp
                    };

                    Log("CLIENT OnReady P" + localIndex + "  peerIp=" + peerIp
                        + "  localGgpo=" + localPort + "  peerGgpo=" + peerPort);
                    Post(() => {
                        Status = "Ready as P" + localIndex + "  peer=" + peerIp;
                        OnReady?.Invoke(info);
                    });
                }
            }
            catch (Exception e) {
                Log("CLIENT error: " + e);
                Post(() => { Status = "Client error: " + e.Message; IsRunning = false; });
            }
        }

        private static void Log(string msg) {
            Debug.Log("[WasdRoom] " + msg);
        }

        private static bool IsLoopbackIp(string ip) {
            if (string.IsNullOrEmpty(ip)) return false;
            ip = ip.Trim();
            return ip == "127.0.0.1" || ip == "::1" || ip == "localhost";
        }

        // ------------------------------------------------------------------ helpers

        private void Post(Action a) { _mainThread.Enqueue(a); }

        private static string ReadLine(NetworkStream stream) {
            var sb = new StringBuilder(64);
            var buf = new byte[1];
            while (true) {
                int n = stream.Read(buf, 0, 1);
                if (n <= 0) break;
                char c = (char)buf[0];
                if (c == '\n') break;
                if (c != '\r') sb.Append(c);
            }
            return sb.ToString();
        }

        private static void WriteLine(NetworkStream stream, string s) {
            byte[] data = Encoding.UTF8.GetBytes(s + "\n");
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        private static bool IpsEqual(string a, string b) {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Best-effort first non-loopback IPv4.</summary>
        public static string DetectLanIp() {
            try {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses) {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork) {
                            string s = addr.Address.ToString();
                            if (!s.StartsWith("127.")) return s;
                        }
                    }
                }
            }
            catch { /* ignore */ }
            return "127.0.0.1";
        }
    }
}
