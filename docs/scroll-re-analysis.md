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

### Phase 1 — Input fidelity (`HidContactParser`, `TouchpadListener`) — **done**

1. **Scan Time** (Digitizer usage `0x56`, 100 µs units, 16-bit wrap). Unwrap it into a monotonic `double TimestampSec` per frame, and use it for all dt math.
   - **A declared Scan Time cannot be trusted** (see below). `ScanTimeClock` cross-checks it against `Stopwatch` and abandons the counter after 16 consecutive frames that do not advance it, so frame time degrades to the wall clock instead of freezing.
2. **Confidence** (usage `0x47`). Drop non-confident contacts before counting fingers; this is free firmware palm rejection.
   - Read in the same `HidP_GetUsages` call as Tip Switch. Contacts default to confident when the descriptor does not declare the usage.
3. **Hybrid-mode frame assembly.** Accumulate per-report contacts until Contact Count is reached, then emit one frame. Today each report is treated as a whole frame.
   - `TouchFrameAssembler` buffers slots until Contact Count is reached. `Parse` returns every declared finger collection in link-collection order and never filters, so slot indices stay aligned and the assembler can trim to the count.
   - A single `WM_INPUT` can batch several reports (`dwCount`); the listener now walks all of them. It previously read only the first.
4. **Physical size** from `HIDP_VALUE_CAPS` (`PhysicalMax`, `Units`, `UnitsExp`) to get pad mm, with the % fallback kept.
   - `TryPhysicalMm` decodes the Unit system and length nibbles (SI cm, English inch) and the Unit Exponent nibble. Surfaced on `TouchFrame`; zones still use percentages until Phase 2.

#### Measured on Windows hardware [O]

From `--record` on the development machine (ELAN PTP, built-in):

| Quantity | Value | Consequence |
|---|---|---|
| Pad size | 160.0 × 114.9 mm | Scroll's 12.3 mm default edge = 7.7 % of width here, vs the current 10 % ≈ 16 mm |
| Frame rate | ~89 Hz (p50 dt 11.2 ms) | Not the 120 Hz the macOS gain math assumes. Phase 3 must derive its tick scale from the measured rate, not a constant |
| Sub-8 ms frames | 4.7 % of contact frames, 19 of 2296 under 1 ms | Batched reports. The Phase 2 ≥ 8 ms velocity window is load-bearing: `Δ/dt` on a 0.02 ms frame is ~500× too large |
| Confidence | set on every contact | Nothing wrongly dropped |
| Contact slots | packed, stable `contact_id` | No fixed-slot firmware; the Contact Count prefix rule holds |

**Scan Time may be declared and never advance.** This pad reports usage `0x56` in every frame with a constant value of 0. Trusting it froze frame time, which in turn froze the 50 ms phantom-contact grace in `TryEnterEdge`: the first gesture scrolled, launched inertia, and every later touch was then rejected until an idle gap longer than the 3.2768 s wrap window let the wall-clock fallback advance the clock. Symptom was "scrolling works briefly, then stops, then works again if you wait". The recording showed it as 1229 frames with a single confident contact in the right edge zone and the state still `Idle`; after the fix, 4.

The lesson generalises beyond this usage: **any optional HID field may be present and meaningless**, so a field that drives timing needs an independent sanity check.

### Phase 2 — Recognizer (`EdgeScrollController`) — **done**

5. **Zones in mm.**
   - Right and left edges (vertical), default ≈ 12 mm.
   - Bottom edge (horizontal) kept.
   - Settings migrate from %.
   - Built as one `EdgeZoneWidthMm` (default 12.0, range 1.5–30) shared by every edge, like Scroll's single `scrollZoneWidth`. The left edge is new and off by default; it shares the right edge's speed and invert settings. Side edges win over the bottom edge at the corners.
   - No migration was needed. The per-edge percentages stay in `settings.json` as the fallback for pads with no physical units. Without units, the exit hysteresis is approximated as 1 % of the pad per mm.
6. **Zone lifecycle.**
   - Evaluate the zone each frame.
   - *Began-in-zone* restriction (default on): only a contact whose first frame is in the zone can scroll. This fixes a finger sliding in from the centre.
     - Eligibility is decided on a contact's first frame and held until all fingers lift. A second finger landing and lifting does not reset it, so the original contact can re-enter.
   - Optional *stop when leaving zone*, with ~2 mm hysteresis. It ends the scroll with no inertia, as Scroll does.
   - **Cursor lock moved from zone entry to the first emitted scroll.** `TryEnterEdge` used to call `LockCursor()` on entering `EdgeContact`, before the tap guard had passed, so a finger that merely rested or drifted in the zone pinned the cursor. This was observed as ~250 ms of frozen cursor during ordinary pointer movement along the bottom edge. [O, Windows]
   - Any touch still cancels inertia, in a zone or not (unchanged).
7. **Velocity tracking.**
   - Skip frames with `dt < 8 ms` but keep the reference sample, so velocity always spans ≥ 8 ms.
   - `wheel = Δnorm/dt × dt_emit × gain`, accumulated as today.
     - **Deviation:** emission stays displacement × gain (`Δnorm × 2000 × speed`), which keeps scroll distance independent of frame rate. Scroll's velocity × gain shrinks the output on slow frames. Velocity (`Δnorm/dt`, pad lengths/s) is measured separately and kept for the Phase 3 launch.
   - Movement below the dead zone also keeps the reference, so slow drags accumulate instead of being discarded. A finger at rest for more than 50 ms resamples the reference, so a flick after a pause is not averaged over the pause.
   - Keep the 2 % tap guard, and optionally add a min-velocity start gate (default off, like Scroll).
     - The gate is `MinStartVelocity`, set in `settings.json` only.
8. **Strict single finger** after confidence filtering. Keep the 50 ms phantom-contact grace (a Windows-specific artefact).

#### Measured after Phase 2 [O]

| Quantity | Value |
|---|---|
| Innermost accepted right-edge contact | 11.8 mm from the edge (limit 12.0) |
| Speed while scrolling | p50 1.8, p90 4.7, max 13.9 pad lengths/s |
| Velocity spikes from batched frames | none |

These speeds give Phase 3 its flick threshold directly, so no conversion from Scroll's 30 px per event is needed.

### Phase 3 — Inertia (new `InertiaScroller`) — **done**

9. **Launch:**
   - Last-frame rate (or a 2-frame EWMA) in wheel-units per 1/60 s.
     - Built as an EWMA (α = 2/3) of the ≥ 8 ms velocity samples from Phase 2. It replaces the old mean over a 100 ms buffer.
     - No coast if the last sample is older than 50 ms, i.e. the finger rested before lifting.
   - Then piecewise boost: ×3 normally, ×8 above a "flick" threshold. Calibrate the threshold to Scroll's 30 px per event at default speed.
     - Threshold is 3.75 pad lengths/s. Scroll's wheel value is `v × (speed + 2)`, so 30 px/event at the default gain of 8 is a normalized speed of 30 / 8. No pixel conversion is needed. On the development pad this is roughly the fastest 15 % of drags.
     - The boost switches on abruptly: a release at 3.7 pad/s coasts ~53 notches, one at 3.8 ~145. Scroll does the same. A ramp between ×3 and ×8 is the fix if this ever feels inconsistent.
   - Axis snap is not needed for single-axis edges.
10. **Loop:**
    - Dedicated thread on `CreateWaitableTimerExW(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)` at ~60 Hz.
      - One-shot 16.67 ms due time each tick. It falls back to a standard waitable timer before Windows 10 1803, then to `Thread.Sleep`. Each step's dt is capped at 50 ms so a stalled thread cannot jump.
    - `v *= pow(1 - decel, dt*60)` using the real dt.
    - Post `v` through the fractional accumulator.
      - **Deviation:** each step posts `v × dt × 60`, not `v`. A late tick then neither shortens nor lengthens the coast. With an exact 60 Hz timer the two are identical.
    - Stop below a terminus.
      - 2 wheel units per tick, the counterpart of Scroll's 1.1 px.
    - Map `InertiaStrength` 1–10 onto `decel` (5 → 0.08, Scroll's default).
      - `decel = 0.08 × 2^((5 − s) / 4)`, so 1 → 0.16 and 10 → 0.034.
    - Physics (`InertiaPhysics`) is separate from the thread and clock, so it can be unit-tested with synthetic dt.
    - Steps are posted under the scroller's lock, so no step can land after `Stop()` returns.
11. **Cancel** on any new contact in a zone, as today.
    - In fact on any single touch after the 50 ms phantom grace, in a zone or not — the existing behaviour, kept.

#### Measured after Phase 3 [O]

Simulated coast at speed multiplier 1.5, strength 5, compared with the pre-Phase-3 model:

| Release speed | Before | After |
|---|---|---|
| 1.8 pad/s (median drag) | 19 notches, 1.31 s | 26 notches, 0.98 s |
| 3.7 pad/s | 40 notches, 1.54 s | 53 notches, 1.13 s |
| 4.7 pad/s (p90, flick) | 51 notches, 1.62 s | 180 notches, 1.38 s |

Ordinary releases coast slightly further and stop sooner; flicks go ~3.5× further, which is Scroll's long-flick character.

On hardware: 175 coasts from 185 scroll episodes. The other 10 were rested or two-finger releases, correctly not coasting. 45 of 175 were flicks. Launch velocity median 419, p90 2954, max 5480 wheel units per tick, with no outliers.

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

- A `--record` CSV mode (scan time, contacts, confidence, zone, state, emitted delta) for offline tuning. **Done.** `--record`, `--record=PATH` or `--record PATH`; bare `--record` writes `%APPDATA%\ScrollStrip\record-<timestamp>.csv`. One row per contact:

  `seq, time_s, dt_ms, clock, scan_time_raw, reported_count, finger_count, contact_index, contact_id, norm_x, norm_y, tip, confident, pad_w_mm, pad_h_mm, zone, tracked_zone, state, velocity, emitted_delta, inertia_launch`

  `clock` is `ScanTime` or `WallClock`, which is what distinguishes a stubbed counter from a misread one. `zone` is the geometric hit test on every frame; `tracked_zone` is the zone the active scroll belongs to; `velocity` is in pad lengths/s along the scroll axis; `inertia_launch` is the launch velocity, in wheel units per 1/60 s, on the frame a coast starts. `emitted_delta` covers the gesture path only — inertia ticks on its own timer between frames and cannot be attributed to a frame.
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
