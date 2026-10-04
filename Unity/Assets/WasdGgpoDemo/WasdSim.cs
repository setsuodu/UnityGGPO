using System;
using Unity.Collections;
using UnityEngine;

namespace WasdDemo {

    /// <summary>Entire game state. Plain ints only -> bit-exact on every machine.</summary>
    public struct WasdState {
        public int frame;
        public Vector2Int p0;   // milli-units (1000 = 1 world unit)
        public Vector2Int p1;

        public Vector2Int Get(int i) { return i == 0 ? p0 : p1; }
    }

    /// <summary>Deterministic simulation: 2 squares, WASD, solid body-vs-body push, arena walls.</summary>
    public static class WasdSim {
        public const int NUM_PLAYERS = 2;

        public const int INPUT_W = 1 << 0;
        public const int INPUT_A = 1 << 1;
        public const int INPUT_S = 1 << 2;
        public const int INPUT_D = 1 << 3;

        public const int UNIT = 1000;       // milli-units per world unit
        public const int SPEED = 100;       // per frame  (=6 units/s @60fps)
        public const int SIZE = 1000;       // square edge
        public const int HALF_W = 8000;     // arena 16 x 9 units
        public const int HALF_H = 4500;

        public static WasdState Initial() {
            return new WasdState {
                frame = 0,
                p0 = new Vector2Int(-4000, 0),
                p1 = new Vector2Int(4000, 0),
            };
        }

        public static void Step(ref WasdState s, long in0, long in1) {
            s.p0 = Move(s.p0, in0);
            s.p1 = Move(s.p1, in1);
            Separate(ref s);
            s.p0 = Clamp(s.p0);
            s.p1 = Clamp(s.p1);
            s.frame++;
        }

        private static Vector2Int Move(Vector2Int p, long input) {
            if ((input & INPUT_W) != 0) p.y += SPEED;
            if ((input & INPUT_S) != 0) p.y -= SPEED;
            if ((input & INPUT_A) != 0) p.x -= SPEED;
            if ((input & INPUT_D) != 0) p.x += SPEED;
            return p;
        }

        // Players are solid: push apart along the axis of least penetration.
        // This makes the LOCAL player's position depend on the REMOTE input,
        // so a wrong remote prediction visibly corrects the local PlayerView too.
        private static void Separate(ref WasdState s) {
            int dx = s.p1.x - s.p0.x;
            int dy = s.p1.y - s.p0.y;
            int ox = SIZE - Mathf.Abs(dx);
            int oy = SIZE - Mathf.Abs(dy);
            if (ox <= 0 || oy <= 0) return;

            if (ox <= oy) {
                int sgn = dx >= 0 ? 1 : -1;
                int h0 = ox / 2, h1 = ox - h0;
                s.p0.x -= sgn * h0;
                s.p1.x += sgn * h1;
            }
            else {
                int sgn = dy >= 0 ? 1 : -1;
                int h0 = oy / 2, h1 = oy - h0;
                s.p0.y -= sgn * h0;
                s.p1.y += sgn * h1;
            }
        }

        private static Vector2Int Clamp(Vector2Int p) {
            p.x = Mathf.Clamp(p.x, -HALF_W + SIZE / 2, HALF_W - SIZE / 2);
            p.y = Mathf.Clamp(p.y, -HALF_H + SIZE / 2, HALF_H - SIZE / 2);
            return p;
        }

        // ---- save / load (GGPO snapshots) ----

        private const int INT_COUNT = 5;

        public static NativeArray<byte> ToBytes(in WasdState s) {
            var ints = new[] { s.frame, s.p0.x, s.p0.y, s.p1.x, s.p1.y };
            var bytes = new byte[INT_COUNT * sizeof(int)];
            Buffer.BlockCopy(ints, 0, bytes, 0, bytes.Length);
            return new NativeArray<byte>(bytes, Allocator.Persistent);
        }

        public static WasdState FromBytes(NativeArray<byte> data) {
            var bytes = data.ToArray();
            var ints = new int[INT_COUNT];
            Buffer.BlockCopy(bytes, 0, ints, 0, INT_COUNT * sizeof(int));
            return new WasdState {
                frame = ints[0],
                p0 = new Vector2Int(ints[1], ints[2]),
                p1 = new Vector2Int(ints[3], ints[4]),
            };
        }

        /// <summary>FNV-1a over the state. Equal (frame, checksum) on both peers == no desync.</summary>
        public static uint Checksum(in WasdState s) {
            unchecked {
                uint h = 2166136261;
                h = (h ^ (uint)s.frame) * 16777619;
                h = (h ^ (uint)s.p0.x) * 16777619;
                h = (h ^ (uint)s.p0.y) * 16777619;
                h = (h ^ (uint)s.p1.x) * 16777619;
                h = (h ^ (uint)s.p1.y) * 16777619;
                return h;
            }
        }
    }
}
