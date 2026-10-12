# Scroll.app (macOS) — reverse-engineering notes and ScrollStrip improvement plan

Target: `/Applications/Scroll.app`, Scroll 2.12 (build 40), `com.knollsoft.Scroll`.
Binary: `Contents/MacOS/Scroll`, universal. The arm64 slice was analysed; sha256 of the universal file is `8dbd55f5…7e25`.

## Method and confidence

The analysis was done in two passes.

**Pass 1 (manual)**
- Mach-O imports and ObjC/Swift metadata.
- Full `objdump` arm64 disassembly, split into functions using `LC_FUNCTION_STARTS`.
- Constant recovery from `mov`/`movk` immediates.
- Reading `defaults read com.knollsoft.Scroll`.

**Pass 2 (Hopper)**
- Hopper decompiled the key functions through the REA CLI, using the snapshot `scroll.snap`. Every constant was checked against the raw disassembly.
- The REA MCP server runs on Node 20.12.2, which lacks `zlib.crc32`, and returned unreadable results. The CLI runs on Node 24 with `HOPPER_LAUNCHER_PATH` set.

**Runtime probe**
- `MTDeviceGetSensorSurfaceDimensions` on this Mac's built-in trackpad returns 14267 × 8509, i.e. 1/100 mm. Only the Magic Mouse path uses those units.

The binary is stripped. Hopper names only the ObjC entry points (e.g. `-[ScrollCornerTouch scrollWithTimer:]`, `-[SettingsViewController toggleDeviceEdge:]`). The roles of internal functions come from their APIs, the config keys they read, and their data flow.

Labels: **[O]** observed in code or data · **[I]** inferred · **[?]** unresolved.

---

## 1. Architecture

There are two separate input paths.

| Path | Source | Used for |
|---|---|---|
| **Trackpad** | `CGEventTap` (callback `0x10002b3ac`). On NSEvent type 29 (gesture, subtype ≠ 6), the frame is built from `NSEvent.allTouches` (`normalizedPosition`, `phase`, `identity`, `deviceSize`) and `timestamp` (`0x10002373c`, `0x100022988`). It then goes to the dispatcher `0x10001fb2c`. | Edge scroll, corner actions, modifier scroll [O] |
| **Magic Mouse** | Private `MultitouchSupport` (`MTRegisterContactFrameCallbackWithRefcon`) → `0x10001b23c` → `0x10000f4c0` / `0x10001dff4`. Gated by `mouseScrollingByScroll`. | Mouse scroll replacement, allowed-area restrictions [O] |

The same event tap also does three things [O]:

- It **pins the cursor**. On every mouseMoved event it calls `CGWarpMouseCursorPosition(savedPoint)` while a scroll is active.
- It tracks modifier flags from flagsChanged events.
- It zeroes `kCGScrollWheelEventDeltaAxis1/2` (fields 11/12) on native scroll events from devices Scroll has taken over (`0x100014e24`).

**Output** [O]:
- `CGEventCreateScrollWheelEvent2(src, kCGScrollEventUnitPixel, 1|2, dy, dx, 0)`, then `CGEventPost(kCGHIDEventTap)`.
- **No scroll or momentum phase fields are set.** The events are plain pixel wheel events, and Scroll implements inertia itself. Windows has the same constraint, so the model transfers directly.

---

## 2. Trackpad dispatcher (`0x10001fb2c`) [O]

Frame fields:
- touch dict.
- timestamp.
- phase histogram.
- `allEnded`: no began or moving touches, and at least one ended.
- touch count.

The dispatcher does the following, in order:

1. **Strict single finger.**
   - Logic runs only when `count == 1`. The touch is simply the first element; it is not tracked by identity.
   - With 0 or ≥ 2 touches, every recognizer gets its reset witness.
   - [?] Whether resting thumbs are already excluded from `allTouches` is unresolved. The NSTouch thumb heuristics at `0x100022d34`/`0x100023284` exist, but they were not traced into this count.
2. **Zone hit-test every frame** (`0x100033374`). It is stateless.
3. **Corner check first.**
   - `zone & 0xF` (1/2/4/8) looks up the corner config and calls `0x100012838`.
   - A corner can be a **shortcut** or a **hold-to-scroll** action (§5).
4. **Edge check.** `zone & 0x30` (left 0x10 / right 0x20) looks up the edge config and calls the edge emitter `0x100018454`.
5. **State update and zone restriction** (§3).
6. **Modifier scroll.**
   - Fixed key 6: hold a configured modifier and move one finger anywhere to scroll in 2-D.
   - Handled by `0x10000a098`, gain `scrollSpeed + 4`.

**Config entries.** Each zone entry is `{action:u8, requiredModifiers:u64, flag:u8}`; 5 means "none".

- **Edge action** values 1 and 2 pick the direction. The emitter uses `+v` for 2 and `−v` otherwise.
- **Modifiers.** If `flag == 0`, the zone fires only when the current modifier flags equal `requiredModifiers`, and no other recognizer is active. So **each zone can require a modifier**.
- **Stored example.** The user's `trackpadConfig` is `[1,{option:2},3,{option:0},6,{option:2},4,{option:0},0,{option:0}]`, a map of zone-id → option.

---

## 3. Edge zone geometry [O]

```
T = scrollZoneWidth   (default 35.0; "Device edge" checkbox sets 5.0 — 0x1000271d4)
W,H = NSTouch.deviceSize (points, 1/72")
right edge : W - x*W < T     (bottom corner y*H < T → 0x28, top corner H - y*H < T → 0x24)
left edge  :     x*W < T     (corners 0x12 / 0x11)
```

- **Units:** the default 35 pt ≈ **12.3 mm**. "Device edge" mode is 5 pt ≈ **1.8 mm**. The zone is a **physical** band, independent of trackpad size. The Magic Mouse path uses 1225 in MT units (1/100 mm), i.e. 12.25 mm. [I: same physical intent]
- **Edges only.** Only left and right edges exist; there is no top or bottom horizontal-scroll strip. Edge output is always vertical.
- **Leaving the zone ends the scroll immediately, with no inertia.**
  - If the zone lookup fails or the gate fails while scrolling, the dispatcher clears the cursor pin and the scrolling flag (`0x10001ff40`).
- **`scrollZoneRestrictions`** sets which states are blocked:
  - 0 = none.
  - 1 = no edge scroll if the touch *began* outside a zone.
  - 2 = additionally, once the finger leaves a zone, it must lift before zones work again.
  - Tracked in state byte +0x60: 1 = began outside, 2 = was outside.

## 4. Edge scroll emitter (`0x100018454`) [O]

```
on each single-touch frame in an edge zone:
    if !ended and (t - t_prev) < 0.008 s: return          // keep previous sample → ≥ 8 ms velocity window
    v = (y_prev - y_cur) / dt                              // normalized y only, units/s — no x, no angle test
    if flag and !scrolling and |v| <= scrollZoneMinVelocity: return   // key has no fallback → default 0 (gate off)
    if !scrolling: cancel inertia; save cursor point (pin); scrolling = true
    wheel = (action==2 ? v : -v) * (scrollSpeed + 2)       // scrollSpeed default 6 → gain 8
    post pixel scroll(wheelCount 1, wheel1 = Int32(wheel)), copying modifier flags
        (always when flag, else only if includeFlagsOnScroll)
    remember wheel as lastWheel
on allEnded (lift) frame:
    if scrolling (or the min-velocity check passes on this frame): InertiaLaunch(lastWheel, 0)
    else: reset
```

- There is **no tap guard** apart from the optional min-velocity gate. A tap produces `v ≈ 0`, so effectively no scroll.
- There is no straight-line or axis test on the trackpad edge path. The 30°/45° `atan2` lock (`0x10001f578`; π/6 while history < 8 samples, then π/4) is used only by the Magic Mouse recognizer.
- The per-event delta is a velocity × gain. At a ~120 Hz frame rate this is linear in displacement: `Δpx ≈ Δnorm × 120 × 8` at the default speed.

## 5. Corner hold-to-scroll (`ScrollCornerTouch`, `0x100012838` / `0x100012a70` / `0x100012c98`) [O]

- While a finger rests in a corner bound to a scroll action, an `NSTimer` repeats `scrollWithTimer:`.
  - Interval: `max(0.003, 0.018 / scrollSpeed)` s.
  - Step: `max(1, Int(scrollSpeed − 5))` px per tick.
- `allEnded` (lift) invalidates the timer.
- Corners can instead be bound to keyboard shortcuts (`topLeftRecorder`, …).

## 6. Inertia [O]

**Launch (`0x100007888`)**, with input `lastWheel` (px/event):
```
if |vx| < 0.2|vy|: vx = 0   elif |vy| < 0.2|vx|: vy = 0    // dominant-axis snap
v *= (|v| > 30) ? 8 : 3                                     // per component: boost, extra for flicks
```

**Loop (`0x10000723c`)** on `com.knollsoft.Scroll.inertiaScrollerQueue`:
```
every asyncAfter(1/60 s):
    if |vx| <= inertiaTerminus(1.1) and |vy| <= 1.1: stop
    v *= powf(1 - inertiaDecelerationRate(0.08), dt_real * 60)   // frame-rate independent
    main.async { post pixel scroll(v) }
```

- Cancel (`0x100007c64`) runs at scroll start and on every emitted tracking frame, so touching again stops the coast.
- **Coast distance.** The geometric sum is ≈ `v / 0.08 = 12.5 × v`. With the ×3 or ×8 launch gain, the total coast is **37× or 100× the last frame's delta**. This strong boost is what makes Scroll's flick feel long.

## 7. Other behaviours

- **Per-app disable or allow lists**, using `FrontAppListener`. The dispatcher is skipped for disabled apps. [O]
- **Restart on wake**, a zero-touch watchdog ("Touches are not being detected. Restarting."), and device re-registration with retries. [O strings]
- **NSTouch-path thumb heuristics** (`0x100022d34`, `0x100023284`):
  - A touch in the outer 15 %, or beyond 85 %, of the pad that is ≥ 130–140 pt (≈ 46–49 mm) from other touches is classed as a thumb.
  - It is released once it moves into the central 30–70 % band.
- **MT-path filters** (Magic Mouse):
  - Resting: `|v| ≤ 0.1` over recent frames.
  - Light-touch filter: default 0.4.
  - Large-touch filter: default 8.0 (+10).
  - Palm shape: axis ratio 1.3–3.
  - Resting thumb: ≥ 35 / 40 units (1/100 mm) from others. [O constants]

---

## 8. Gap analysis vs ScrollStrip

| Area | Scroll | ScrollStrip today | Gap / action |
|---|---|---|---|
| Zone size | Physical, 12.3 mm default (1.8 mm "device edge") | 10 % of pad | Switch to mm when the PTP reports physical units |
| Zones | Left and right edges (vertical); 4 corners (hold-to-scroll or shortcut); per-zone direction and required modifier | Right (vertical) and bottom (horizontal) | Add left edge, corners, per-zone modifier; keep bottom (Windows users expect it) |
| Entry rule | Zone checked every frame; optional "must begin in zone" | Enters whenever an idle single contact is in the zone, including sliding in from the centre | Add a began-in-zone restriction (default on) |
| Leaving zone | Immediate stop, no inertia | Keeps scrolling until lift | Optional: stop when the finger leaves the band (with hysteresis) |
| Tap guard | None (min-velocity gate, default off) | 2 % travel guard | Keep ours; Windows PTPs emit more jitter |
| Timing | Frame timestamps; ≥ 8 ms velocity window | `Stopwatch` at `WM_INPUT` receipt, per report | Use PTP **Scan Time** and the 8 ms window |
| Gain | `Δnorm/dt × (speed+2)` px | `Δ × 2000 × speed` wheel units | Fine; unify units with inertia |
| Inertia launch | Last frame's delta, axis snap, ×3 / ×8 if > 30 | Mean of 100 ms buffer, no boost | Adopt last-frame (or 2-frame EWMA) + piecewise boost |
| Inertia decay | `(1-0.08)^(dt·60)`, real dt, stop ≤ 1.1 | Fixed per jittery 16 ms `System.Timers` tick | dt-based decay on a high-resolution timer |
| Multi-touch | Strict `count == 1`; resets recognizers | Second contact cancels (+50 ms phantom grace) | Equivalent; additionally drop PTP **Confidence = 0** contacts first |
| Cursor | Re-warped to the saved point on every mouse move | `ClipCursor` 1×1 rect | Equivalent (ours is simpler) |
| Native suppression | Zeroes native scroll deltas in the tap | none | Optional `WH_MOUSE_LL` filter; low priority |
| Robustness | Wake restart, watchdog, per-app lists | Parser reset on device change | Add resume handling and per-app exclusions |

---

## 9. Plan for ScrollStrip (Windows precision touchpad)

### Phase 1 — Input fidelity (`HidContactParser`, `TouchpadListener`)

1. **Scan Time** (Digitizer usage `0x56`, 100 µs units, 16-bit wrap). Unwrap it into a monotonic `double TimestampSec` per frame, and use it for all dt math.
2. **Confidence** (usage `0x47`). Drop non-confident contacts before counting fingers; this is free firmware palm rejection.
3. **Hybrid-mode frame assembly.** Accumulate per-report contacts until Contact Count is reached, then emit one frame. Today each report is treated as a whole frame.
4. **Physical size** from `HIDP_VALUE_CAPS` (`PhysicalMax`, `Units`, `UnitsExp`) to get pad mm, with the % fallback kept.

### Phase 2 — Recognizer (`EdgeScrollController`)

5. **Zones in mm.**
   - Right and left edges (vertical), default ≈ 12 mm.
   - Bottom edge (horizontal) kept.
   - Settings migrate from %.
6. **Zone lifecycle.**
   - Evaluate the zone each frame.
   - *Began-in-zone* restriction (default on): only a contact whose first frame is in the zone can scroll. This fixes a finger sliding in from the centre.
   - Optional *stop when leaving zone*, with ~2 mm hysteresis.
7. **Velocity tracking.**
   - Skip frames with `dt < 8 ms` but keep the reference sample, so velocity always spans ≥ 8 ms.
   - `wheel = Δnorm/dt × dt_emit × gain`, accumulated as today.
   - Keep the 2 % tap guard, and optionally add a min-velocity start gate (default off, like Scroll).
8. **Strict single finger** after confidence filtering. Keep the 50 ms phantom-contact grace (a Windows-specific artefact).

### Phase 3 — Inertia (new `InertiaScroller`)

9. **Launch:**
   - Last-frame rate (or a 2-frame EWMA) in wheel-units per 1/60 s.
   - Then piecewise boost: ×3 normally, ×8 above a "flick" threshold. Calibrate the threshold to Scroll's 30 px per event at default speed.
   - Axis snap is not needed for single-axis edges.
10. **Loop:**
    - Dedicated thread on `CreateWaitableTimerExW(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)` at ~60 Hz.
    - `v *= pow(1 - decel, dt*60)` using the real dt.
    - Post `v` through the fractional accumulator.
    - Stop below a terminus.
    - Map `InertiaStrength` 1–10 onto `decel` (5 → 0.08, Scroll's default).
11. **Cancel** on any new contact in a zone, as today.

### Phase 4 — Zone actions

12. **Corner hold-to-scroll**: a contact resting in a corner bound to scroll starts a repeating timer.
    - Interval: `max(3 ms, 18 ms / speed)`.
    - Step: `max(1, speed − 5)`, scaled to wheel units.
    - The timer stops on lift.
13. **Corner shortcuts**: a tap in a corner sends a configured key chord through `SendInput`.
14. **Per-zone required modifier**: a zone fires only while the configured modifier set is held (`GetAsyncKeyState`). This allows e.g. "Shift + right edge = horizontal".

### Phase 5 — Robustness and UX

15. Re-register Raw Input on `WM_POWERBROADCAST` resume. Add a no-frames watchdog.
16. Per-app exclusion via `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` → process name.
17. Optional native-wheel suppression via `WH_MOUSE_LL`. This drops non-injected wheel events while scrolling and covers only the legacy-wheel path.
18. Settings UI:
    - Zone width (mm) and a "device edge" preset.
    - Per-zone direction, action and modifier.
    - Speed, inertia strength, and a began-in-zone toggle.

### Verification

- A `--record` CSV mode (scan time, contacts, confidence, zone, state, emitted delta) for offline tuning.
- Unit tests for the recognizer and inertia with an injectable clock and scroll sink. Cover:
  - tap
  - slow drag
  - flick
  - slide in from the centre
  - leave the zone mid-scroll
  - second finger mid-scroll
  - phantom contact at lift
  - corner hold

### Still unresolved

- [?] Whether `NSEvent.allTouches` includes resting or thumb touches before the `count == 1` check, i.e. whether Scroll tolerates a resting thumb.
- [?] What a second finger does mid-scroll: reset silently or launch inertia (recognizer witness slot 3 was not decompiled).
- [?] The UI meaning of the zone entry `flag` byte.
