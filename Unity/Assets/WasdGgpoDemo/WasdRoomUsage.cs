using UnityEngine;

namespace WasdDemo {

    /// <summary>
    /// Example wiring: drop on a GameObject, or copy the pattern into WasdGgpoDemo.
    ///
    /// Host (P0 machine, ideally IP 192.168.1.101):
    ///   - set roomHostIp if needed
    ///   - StartHostLocalP0()  → TCP listen, local = P0, wait 1 joiner
    ///   - OnReady → start GGPO as P0 with info.PeerIp
    ///
    /// Client (P1):
    ///   - roomHostIp = host machine IP (default 192.168.1.101)
    ///   - StartClient()
    ///   - OnReady → start GGPO as P1 with info.PeerIp
    /// </summary>
    public class WasdRoomUsage : MonoBehaviour {

        [Header("Room TCP (hardcoded default, editable at runtime)")]
        public string roomHostIp = WasdRoom.DefaultRoomHostIp; // 192.168.1.101
        public int roomPort = WasdRoom.DefaultRoomPort;         // 9000

        [Header("GGPO ports (convention)")]
        public int ggpoPortP0 = WasdRoom.DefaultGgpoPortP0;     // 7000
        public int ggpoPortP1 = WasdRoom.DefaultGgpoPortP1;     // 7001

        private WasdRoom room;

        private void OnDestroy() {
            room?.Dispose();
            room = null;
        }

        private void Update() {
            room?.Pump(); // required: delivers OnReady on main thread
        }

        // ---- UI buttons call these ----

        public void OnClickHost() {
            room?.Dispose();
            room = new WasdRoom {
                RoomHostIp = roomHostIp,
                RoomPort = roomPort,
                GgpoPortP0 = ggpoPortP0,
                GgpoPortP1 = ggpoPortP1
            };
            room.OnReady += HandleReady;
            // Same process = P0: only need 1 remote TCP joiner
            room.StartHostLocalP0();
            Debug.Log("[room] " + room.Status);
        }

        public void OnClickJoin() {
            room?.Dispose();
            room = new WasdRoom {
                RoomHostIp = roomHostIp, // must point at host machine
                RoomPort = roomPort,
                GgpoPortP0 = ggpoPortP0,
                GgpoPortP1 = ggpoPortP1
            };
            room.OnReady += HandleReady;
            room.StartClient();
            Debug.Log("[room] " + room.Status);
        }

        private void HandleReady(WasdRoom.RoomReadyInfo info) {
            Debug.Log("[room] READY local=P" + info.LocalIndex
                      + " peer=" + info.PeerIp
                      + " localGgpoPort=" + info.LocalGgpoPort
                      + " peerGgpoPort=" + info.PeerGgpoPort);

            // ---- hook your existing GGPO here ----
            // Example:
            // playerIndex = info.LocalIndex;
            // localPort   = info.LocalGgpoPort;
            // remoteIp    = info.PeerIp;
            // remotePort  = info.PeerGgpoPort;
            // StartSession();
            //
            // Or:
            // runner = new WasdRunner(info.LocalIndex, info.LocalGgpoPort,
            //                         info.PeerIp, info.PeerGgpoPort, frameDelay);
        }
    }
}
