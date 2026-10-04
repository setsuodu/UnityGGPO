# WASD GGPO demo (rollback test bench)

Three scripts, zero assets. Depends only on the UnityGGPO files you already have
(`GGPO.cs`, `GGPOSession.cs`, `Utils.cs`) + the native `UnityGGPO` plugin, and "Allow unsafe code".

## Setup
1. Copy this folder into the Unity project that already contains UnityGGPO.
2. New empty scene -> empty GameObject -> add **WasdGgpoDemo**.
3. Build a standalone player (or use two Editors). Run one as P0 and one as P1.
   Same machine, no arguments needed beyond:
   - A: `-player 0 -bot -autostart`
   - B: `-player 1 -bot -autostart`
   (default ports P0 7000->7001, P1 7001->7000, delay 2). Other machines: add `-rip <peer ip>`.

## What you see
| Thing | Meaning |
|---|---|
| solid blue / orange square | **PlayerView**: the state GGPO is running now. Blue = you, orange = remote (predicted) |
| hollow square | **Ghost**: the state at `head - latency`, i.e. the newest frame whose inputs are all real. `latency = ping/2 (GGPO's measured RTT, i.e. what clumsy adds) - frame delay`, capped at 7 by the prediction barrier. Same idea as the CS lag-compensation hitbox view |
| faint line ghost -> solid | the part of the picture that is pure prediction |
| red hollow square + line (fades 2.5 s), border flashes red | where the PlayerView was *before* a rollback -> where the corrected state put it. Only drawn when a rollback actually moved a square |
| HUD `rollbacks / depth / resim` | counts of `OnLoadGameState` (a prediction was wrong) and re-simulated frames |
| HUD `prediction-barrier stalls` | `AddLocalInput` returned PREDICTION_THRESHOLD (peer is > 8 frames behind) |
| HUD `final-frame violations` | must stay 0. >0 means the plugin's prediction window is bigger than 8 |
| HUD `final checksum fN` | must be identical on both peers (desync check) |

Players are solid (they push each other), so a wrong prediction of the remote input also
corrects **your own** square, not just the orange one.

Both peers must use the same `-delay` (the ghost assumes the peer's frame delay equals yours).
The ghost offset is an estimate from ping (about +-1 frame). Cross-check: HUD `predicting ~Nf` should be close to `last depth` of real rollbacks.

Keys: WASD/arrows move, `B` bot on/off, `G` ghost on/off.

## clumsy test (Windows)
Filter: `udp and (udp.DstPort == 7000 or udp.DstPort == 7001)`, Lag = 200 ms, tick **Outbound** only
(loopback packets only show up as outbound; ticking both can double the delay).

Expected:
- Holding one key steadily -> prediction is right -> **0 rollbacks**, no red markers.
- Changing direction (bot does this every 40 frames) -> remote square keeps going the old way, then
  snaps when the real input arrives: red marker, `rollbacks` +1, `last depth` ~ latency in frames.
- 200 ms = 12 frames is longer than GGPO's 8-frame prediction window, so `stalls` will climb:
  that is GGPO's barrier, not a bug. Try 100 ms (6 frames) to see pure predict/rollback without stalls.
- Every rollback is also appended to `<persistentDataPath>/wasd_rollback_P{n}.csv`.

Why you may see few red markers: a rollback only happens when the peer's input CHANGED versus the prediction
("keep doing what you did") and arrives after you already simulated that frame. Holding a key = 0 rollbacks. With
latency below the frame delay (no clumsy) = 0 rollbacks. The `rollbacks` counter vs `visible snaps` tells them apart.

Pass/fail for "prediction + rollback really happen": with lag on and bot on, `rollbacks` > 0,
`visible snaps` > 0, `final-frame violations` = 0, and the final checksums match on both sides.
