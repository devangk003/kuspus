# KusPus — Floating Pill · Design Specification

A lightweight Windows dictation utility. Press a hotkey, speak, and the transcript is pasted into whatever app is focused. The pill is the only UI the user sees 99 % of the time.

This document is the source of truth for visuals, geometry, motion, and behavior. It is implementation-agnostic — translate the tokens below into whatever UI framework you are building with.

> **Relationship to TECH_SPEC §24** — this document supersedes TECH_SPEC §24 where they conflict. Geometry (200×56 vs 360×64), state machine (5 explicit states vs ad-hoc), motion model (damped target/value vs raw amplitude bind), accent line, and confirmation choreography all originate here. TECH_SPEC §24 is to be revised by the user; until then, this file wins for pill work.

---

## 1. Anatomy

### 1.1 Surface

| Property | Value |
|---|---|
| Width | **200 px** (logical) |
| Height | **56 px** |
| Corner radius | **8 px** (Win11 surface — *not* iOS super-ellipse) |
| Material | Mica / Acrylic on Win11. Solid translucent fill as fallback. |
| Drop shadow | Two-layer: large diffuse + tight contact (see §3.3) |
| Inner highlight | 1 px top edge, very subtle (see §3.3) |
| Border | 1 px hairline, low-contrast (see §3.3) |

### 1.2 Position

| Property | Value |
|---|---|
| Anchor | Bottom-center of the active monitor |
| Vertical offset | **40 px above the taskbar** (or 40 px above the work-area bottom edge if the taskbar is hidden) |
| Re-centering | Every time the pill appears |
| Z-order | Always-on-top |
| Hit testing | **Click-through** — mouse events pass through to the window underneath |
| Focus | Never steals keyboard focus |
| Taskbar / Alt-Tab | Hidden from both |

### 1.3 Content rules

- **No chrome.** No titlebar, no close button, no app icon inside the pill, no window-frame shadow.
- **No internal padding bleeds.** Use a single fixed surface size across all states so the pill doesn't jump when content changes.
- **Horizontal inner padding** is **32 px** on each side, so all state content sits within a **136 px content track** (the same width as the visualizer).

---

## 2. The Five States

The pill has exactly five states. **`hidden` is its resting state** — the pill should be invisible whenever the app is not actively in a dictation cycle.

### 2.1 State machine

```
            hotkey pressed
   hidden ──────────────────▶ recording
                                │
                  hotkey released / VAD ends
                                ▼
                          transcribing
                                │
                      transcript ready
                                ▼
                           confirmed  ── 1 s hold ──▶ hidden
                                │
              (anything above can short-circuit to)
                                ▼
                             error  ── 2 s hold ──▶ hidden
```

### 2.2 Hidden
- Nothing on screen. Window not rendered (or fully transparent + non-interactive).

### 2.3 Recording
- **Background:** dark surface (see §3.1).
- **Accent line:** thin, glowing horizontal line at the top edge, in the brand accent color. **136 px wide**, centered, gradient-faded at both ends.
- **Center content:** 20-bar audio visualizer (see §4).
- **Micro-label:** the word `Recording` in uppercase, 9.5 px, letter-spacing 0.08 em, muted color (`subtleText` token), centered 4 px below the visualizer.

### 2.4 Transcribing
- **Background:** same surface as Recording.
- **Accent line:** same color, opacity dropped to ~0.55.
- **Center content:** small spinner (14 px, 1.5 px stroke, ~270° arc, 0.9 s rotation) + the word `Transcribing…` to its right (8 px gap).
- **Vibe:** calm, thinking. Not alarming, not busy.

### 2.5 Paste confirmed
- **Background:** same surface.
- **Accent line:** opacity 0.4.
- **Visualizer:** still present, but at **35 % opacity** with a **radial mask** that fades it to fully transparent through the middle (so it cannot collide with the overlaid text) and back up to full at the bar edges.
- **Center content:** single line of text, fades in (~200 ms): **`Pasted into <App>`** where `<App>` is the bold-weight name of the previously-focused app (e.g. *Slack*, *VS Code*, *Notion*).
- **Hold:** ~1 s, then fade the entire pill out.

### 2.6 Error
- **Background:** same surface.
- **Accent line:** **shifts immediately to red** (`#FF4D4F`). No fade on color.
- **Center content:** 5 px red dot (with a soft red glow) + brief error text. Examples: `Microphone blocked`, `Disk full`, `No internet`.
- **Hold:** ~2 s, then fade out.
- **Constraint:** error copy must fit on one line within the 136 px content track at the error font size (12.5 px). Truncate with `…` if longer.

---

## 3. Visual Tokens

### 3.1 Theme — Dark (default)

| Token | Value |
|---|---|
| Surface base | Vertical gradient: `rgba(38,38,40,0.88)` → `rgba(28,28,30,0.92)` |
| Surface effective fill (Win10 fallback) | `#1E1E1E` at 90 % opacity |
| Border (1 px inset) | `rgba(255,255,255,0.07)` |
| Inner top highlight | `rgba(255,255,255,0.05)` |
| Primary text | `rgba(255,255,255,0.96)` |
| Subtle text | `rgba(255,255,255,0.55)` |
| Visualizer bar (active) | `rgba(255,255,255,0.92)` |
| Visualizer bar (idle) | `rgba(255,255,255,0.28)` |

### 3.2 Theme — Light

| Token | Value |
|---|---|
| Surface base | Vertical gradient: `rgba(248,248,250,0.90)` → `rgba(238,238,242,0.92)` |
| Surface effective fill (Win10 fallback) | `#F3F3F3` at 90 % opacity |
| Border (1 px inset) | `rgba(0,0,0,0.06)` |
| Inner top highlight | `rgba(255,255,255,0.70)` |
| Primary text | `rgba(20,20,20,0.90)` |
| Subtle text | `rgba(20,20,20,0.50)` |
| Visualizer bar (active) | `rgba(20,20,20,0.85)` |
| Visualizer bar (idle) | `rgba(20,20,20,0.25)` |

### 3.3 Surface effects

| Effect | Value |
|---|---|
| Material | Mica/Acrylic on Win11, solid translucent fallback on Win10. Approximate with a blur of `radius ~40 px` + saturate `~140 %` over the desktop. |
| Shadow (dark) | `0 12 32 rgba(0,0,0,0.45)` + `0 2 6 rgba(0,0,0,0.35)` + 1 px inset border |
| Shadow (light) | `0 12 32 rgba(0,0,0,0.14)` + `0 2 6 rgba(0,0,0,0.08)` + 1 px inset border |

### 3.4 Brand accent (recording state)

Default is **Mint**. Three approved alternates are provided for theming.

| Name | Hex | Use |
|---|---|---|
| **Mint** *(default)* | `#4DDBA6` | Primary brand accent |
| Warm amber | `#FF7A59` | Alt — warmer feel |
| Cool azure | `#4EA3FF` | Alt — cooler / pro |
| Soft violet | `#B48CFF` | Alt — playful |
| Error red | `#FF4D4F` | Reserved — error state only |

#### Accent line geometry

| Property | Value |
|---|---|
| Width | **136 px** (matches visualizer) |
| Height | 1.5 px |
| Position | Centered horizontally, flush to top edge of pill (top: 0) |
| Fill | Horizontal gradient: `transparent → accent (50 %) → transparent` |
| Glow | When recording or error: outer blur ring at `accent` × 80 % + offset glow at `accent` × 40 % |
| Opacity by state | `recording` 1.0 · `error` 1.0 · `transcribing` 0.55 · `confirmed` 0.40 · `hidden` 0 |

### 3.5 Typography

| Use | Font | Size | Weight | Letter-spacing |
|---|---|---|---|---|
| Body (transcribing, confirmed) | Segoe UI Variable / Segoe UI | 13 px | 500 (600 on app name) | −0.005 em |
| Body (error) | Segoe UI Variable / Segoe UI | 12.5 px | 500 | −0.005 em |
| Micro-label (`Recording`) | Segoe UI Variable / Segoe UI | 9.5 px | 500 uppercase | 0.08 em |

All text is single-line, `white-space: nowrap`, ellipsis on overflow.

---

## 4. The Visualizer

A row of vertical bars that respond to microphone input amplitude. **It is the message** in the recording state — no text needed.

### 4.1 Geometry

| Property | Value |
|---|---|
| Bar count | **20** |
| Bar width | 3 px |
| Bar corner radius | 2 px |
| Bar gap | 4 px |
| Track total width | **136 px** (20 × 3 + 19 × 4) |
| Track height | 28 px |
| Bar height range | 4 px (min) ↔ 26 px (max) |
| Bar fill | `visualizer bar active` token |
| Bar glow | When a bar's level > 0.7, add `0 0 6 px {accent}@33` shadow |

### 4.2 Motion model

The visualizer must feel **alive but not chaotic**. Use a damped target/value model, not a direct level read.

```
every animation frame:
  dt = clamp(now - lastFrame, 0, 64ms) / 1000

  if (recording and now > nextTargetAt):
    nextTargetAt = now + random(90..150) ms
    # voice envelope — louder in center, softer at edges
    speak = 0.55 + sin(now / 380) * 0.25 + random(-0.125, +0.125)
    for each bar i in 0..19:
      center_weight = 1 - abs(i - 9.5) / 9.5     # 1.0 at center, 0 at edges
      base = 0.18 + center_weight * 0.45
      jitter = random(-0.30, +0.70) * 0.55       # asymmetric: more chance of louder
      target[i] = clamp(base * speak + jitter, 0.05, 1.0)

  if (not recording):
    target[i] = 0.05 for all bars

  # damped approach toward target (per-bar rate so they don't move in lockstep)
  for each bar i:
    rate = recording ? (14 + (i % 3) * 3) : 6
    k = 1 - exp(-rate * dt)
    level[i] += (target[i] - level[i]) * k

  # render: bar_height = 4 + level[i] * 22
```

Key properties of this model:
- **Targets re-roll every ~110 ms**, giving a natural breathing cadence regardless of true input rate.
- **Center bars run hotter** than edges — gives the visualizer a "voice shape."
- **Damped approach (exponential decay)** means bars never snap; they ease toward each new target. Different rates per bar break visual lockstep.
- The `speak` envelope rides a slow sine so even a steady voice doesn't produce a flat row.

If you have real amplitude data, you can replace `target[i]` with `realAmplitude × center_weight × (1 + small_jitter)` — keep the damping pass unchanged.

### 4.3 Confirmation mask

When in `confirmed` state, the visualizer renders behind the text at **35 % opacity** *and* with a radial mask:

```
mask-image: radial-gradient(
  ellipse 60% 140% at 50% 50%,
  transparent       0%,
  transparent      35%,
  rgba(0,0,0,0.55) 60%,
  black            88%
);
```

Effect: bars are fully invisible through the text region, fade smoothly outward, and reach full visibility at the edges of the track. This lets the eye lock onto the text without losing the "I'm still here" signal.

---

## 5. Motion

The motion language is **understated, snappy, no bounce.** Think native OSD, not web widget.

| Transition | Duration | Curve | Notes |
|---|---|---|---|
| Appear (hidden → any) | **120 ms** | ease-out | Opacity 0 → 1. No scale, no slide. |
| Disappear (any → hidden) | **120 ms** | ease-in | Opacity 1 → 0. |
| State content crossfade | **150 ms** | ease | Old content fades out, new fades in. |
| Confirmation choreography | see below | — | Multi-step sequence |
| Error accent color shift | **0 ms** | — | Instant — no fade on the color, only on the surface appearance |

### 5.1 Confirmation choreography (transcribing → confirmed → hidden)

```
t = 0      transcribing showing
t = 0      transcript ready → switch to confirmed state
t = 0      visualizer ghosts to 35 % opacity, mask applies, accent line drops to 0.4
t = 0..200 "Pasted into <App>" text fades in (kp-fadein: opacity 0 → 1, translateY 2 → 0)
t = 1000   begin pill fade-out (120 ms)
t = 1120   pill fully hidden
```

### 5.2 Visualizer entry / exit

The visualizer doesn't get a separate fade — when the pill appears, all bars start at their idle level (0.05) and **damp up** to live targets over ~80–120 ms naturally via the motion model.

### 5.3 Reduced motion

Respect the OS reduced-motion preference: replace the visualizer with a single subtly-pulsing horizontal bar at the same position (or simply hold all bars at the mean level, 0.45). Disable all fade transitions to instant on/off.

---

## 6. Behavior

### 6.1 Visibility rule
The pill is visible **only** when the app is in one of: `recording`, `transcribing`, `confirmed`, `error`. Hidden otherwise. There is no "idle" pill — if you find yourself drawing a pill outside these four states, the design is wrong.

### 6.2 Click-through
Mouse events pass through to the underlying window. Implementation: set the window's hit-test region to empty (or use the OS-level transparent-input flag).

### 6.3 No focus theft
Hotkey activation must not move OS keyboard focus. The previously-focused app must remain the paste target.

### 6.4 Hotkey
- Hotkey is configured elsewhere (settings — out of scope for this spec).
- The hotkey is **push-to-talk by default**: hold to record, release to stop. Tap-toggle is a secondary mode.

### 6.5 Multi-monitor
The pill appears on the monitor containing the focused window. If no app is focused, fall back to the primary monitor.

### 6.6 DPI scaling
All dimensions in this spec are **logical pixels**. Multiply by the current display scale factor when sizing the native window.

---

## 7. Design Principles (Tiebreakers)

1. **Native to the host OS shell, not a third-party web widget.** Match the surrounding system in proportion, rounding, and material — diverge only where the brief explicitly says so.
2. **Invisible until needed.** The user should forget the app is running until they press the hotkey.
3. **No chrome.** No titlebar, close button, app icon, or window-frame shadow inside the pill.
4. **One-glance readability.** Whatever state the pill is in, a user reading it from 2 feet away should understand it in <200 ms.
5. **Single fixed footprint.** Never resize the surface between states — only the contents change.

---

## 8. Out of Scope

- Settings window
- Tray icon variations
- Onboarding flow
- Sound-wave squiggles, frequency-domain graphs, oscilloscope views — the **20-bar vertical visualizer is the only audio representation.**

---

## 9. Quick Reference

```
Surface         200 × 56 px · radius 8 px · mica
Position        bottom-center of active monitor · 40 px above taskbar
Visualizer      20 bars · 3 px wide · 4 px gap · 4–26 px tall · 136 px total
Accent line     136 px × 1.5 px · centered · gradient-faded ends
Body type       Segoe UI Variable · 13 px · weight 500
Micro-label     9.5 px · uppercase · letter-spacing 0.08 em
Mint accent     #4DDBA6
Error red       #FF4D4F
Appear / hide   120 ms opacity fade · no scale · no bounce
Confirm hold    1000 ms
Error hold      2000 ms
```

---

## 10. Hover-Extend Override (user request, 2026-05-16)

This section overrides specific clauses of the spec above. It exists because the
pill — as the only persistent UI surface — needs an escape hatch to quit the app
without the tray.

### 10.1 Hover-extend interaction

- On `MouseEnter` over the pill surface, the pill animates its **width** from
  `200 px → 280 px` over **150 ms**, with `CubicEase / EaseOut`. The pill grows
  **rightward only** (left edge anchored).
- On `MouseLeave`, the pill animates back to `200 px` over **150 ms** with
  `CubicEase / EaseIn`.
- The right `80 px` column reveals two circular icon buttons (28×28 each, 4 px
  gap), centred vertically:
  - **Settings** (`Segoe Fluent Icons U+E713`, gear). Placeholder — non-functional
    until the Settings modal lands.
  - **Close** (`Segoe Fluent Icons U+E8BB`, ✕). Calls `Application.Shutdown` on
    click. This is the user-facing "Quit" path.
- Hover state on each button: rounded background tint (white @ ~13 % for
  Settings; red @ ~20 % for Close).
- The right column's buttons start at `Opacity = 0` and `IsHitTestVisible = false`
  when the pill is not hovered. Both animate to `1` / `true` over 150 ms during
  the extend.

### 10.2 Overrides

- **§1.2 Hit testing — "Click-through":** REMOVED. The pill is fully
  hit-test-visible so the user can hover-extend and click the buttons. `WS_EX_TRANSPARENT`
  is NOT applied. Clicks on the pill body that aren't on a button are silently
  ignored.
- **§1.3 "No chrome — no close button":** REMOVED for the hover-extend column.
  The close button is part of the chrome and is intentional.
- **§1.3 "Single fixed footprint":** SOFTENED. The footprint stays fixed across
  the five content states; only the hover gesture resizes the surface.
- **§6.2 Click-through:** see §10.2 first bullet.

### 10.3 Preserved

- **§6.3 No focus theft** — `WS_EX_NOACTIVATE` still applied. Hovering or
  clicking the pill never moves OS keyboard focus.
- **§1.2 Hidden from taskbar/Alt-Tab** — `WS_EX_TOOLWINDOW` still applied.
- **§6.1 Visibility rule** — pill is still hidden whenever the app is not in one
  of the four active states (`recording`, `transcribing`, `confirmed`, `error`).
  Hover-extend is only reachable while the pill is already shown.

---

## 11. Dogfood-driven evolution (2026-05-17)

This section captures pill-spec divergences that landed during the v1 dogfood
pass. Source-of-truth comments live next to the code in
`src/KusPus.App/FloatingPillWindow.xaml{,.cs}`; this section is the spec-side
companion so future edits don't try to "restore" the original behaviour.

### 11.1 Geometry — current footprint (replaces §10.1 width math)

| State | Width | Height | Rationale |
|---|---|---|---|
| Collapsed (resting, not hovered, not pinned) | `200` | `56` | Same as original spec |
| Expanded (hovered, not pinned) | `320` | `78` | Pill 56 + dock 22 (was: hover-extend `200→280` width, no height growth) |
| Pinned compact | `200` | `56` | Same as Collapsed — pin disables hover-expand entirely |

The hover-extend column from §10.1 is replaced by a dock drawer that slides
**down** from below the pill on hover (22 px peek). The previous 200→280
rightward extension is gone.

### 11.2 Pin = "compact-mode + position-lock" (replaces §10.1 pin semantics)

The Pin button no longer "latches the dock open." New semantics:

- **Click pin while expanded** → contract pill back to 200×56 + slide dock back +
  pin button stays mint-tinted at angle=0, always visible.
- **Hover while pinned** → only swap idle content (SVG-wordmark ↔ visualizer);
  **no resize, no dock**.
- **Click pin again** → unpin; if still hovered, expand back to hover view.
- **Drag** → disabled while pinned. `OnPillMouseLeftButtonDown` short-circuits
  before `DragMove()`. Cursor flips `SizeAll`↔`Arrow` on pin toggle to
  telegraph the lock.
- A `CompactRecordButton` (top-**LEFT** corner, 18×18 + Radius=4 matching
  Pin/Wand) appears only while pinned so the user can still trigger recording
  without unpinning.

### 11.3 Idle content (extension to §2)

Adds a fifth pill visual: **Idle** (in-spec dev override per CLAUDE.md). When the
app is not in Recording / Transcribing / Confirmed / Error, the pill stays
visible showing either:

- **Not hovered**: SVG icon + "KusPus" wordmark (mint `{Mint}` foreground)
- **Hovered (unpinned)**: visualizer bars + "IDLE · HOLD TO DICTATE" label, with
  the hover-extend dock visible
- **Hovered (pinned)**: visualizer bars + label, **no resize, no dock**

This will revert to the spec's "hidden when not in use" once the tray menu's
Quit item makes the close path discoverable — already in place via
`TrayMenuWindow`, so reversion is unblocked but not yet scheduled.

### 11.4 Tap-mode record button (Toggle Recording \[BETA\])

The dock and the compact corner each carry a record toggle wired to
`AppCoordinator.ToggleFromTray`. Both glyphs use the same brush state pattern:

| FSM state | Glyph fill | Shape | Rationale |
|---|---|---|---|
| Idle | `MutedText` (grey, theme-aware) | Circle (RadiusX = half side) | "Available — tap to start" |
| Recording | `#EF5350` (red) | Rounded square (RadiusX = ~1.5) | "Press to stop" |

The labels read **"Toggle Recording \[BETA\]"** (both the dock button's
tooltip and the tray menu item) — the mint `[BETA]` chip signals dogfood
expectation that this is freshly-wired tap-mode behaviour.

Per-user-spec, the toggle does **not** auto-capture a foreground HWND. The
post-transcribe paste lands wherever focus is at the time. A small
`RecordNudgePopup` ("Click into your text field") appears for **2 s** above
the record button on click as a brief hint.

### 11.5 Bottom corner-radius behaviour

`PillSurface.CornerRadius` snaps `8` → `(8, 8, 0, 0)` inside `OpenDock()` and
back to `8` inside `CloseDock()` so the pill + dock read as one continuous
shape while the drawer is visible. Pinned mode never calls those methods
(gated by `!_isPinned`), so compact-mode pill keeps its full rounded corners.
Snap (not animate) because `CornerRadius` isn't a natively animatable
`DependencyProperty`.

### 11.6 Magic wand placeholder

The §10 "Refine text" button is rendered at `Opacity=0.35` with
`Cursor=Arrow` and tooltip `"Refine text — coming soon"`. Disabled state is
visually legible per UX audit Option α. The button is **dormant** — no click
handler.

### 11.7 Shadow softened (replaces drop-shadow in §3.3)

Pill drop shadow lifted from `ShadowDepth=2 BlurRadius=32 Opacity=0.45` →
`ShadowDepth=0 BlurRadius=14 Opacity=0.25`. Omnidirectional soft halo —
no directional bleed onto the dock when the drawer is open.

### 11.8 Inner highlight removed

The 1 px `PillInnerHighlight` Rectangle at the pill's top inner edge was
removed — it only existed on the pill (not the dock), so it created a visible
seam at the pill/dock junction when the drawer opened.

### 11.9 Personality animations (3-phase Organic Pill redesign)

Pill carries two long-lived `Storyboards` started in `Loaded`:

- **Breath** — `BreathScale` `ScaleX/Y` sine pulse `1.0 ↔ 1.006` over 4 s
  (`AutoReverse`, `RepeatBehavior.Forever`). Subtle "alive" cue.
- **Hue drift** — middle gradient stop of `AccentBrush` cycles mint → seafoam
  → cyan → back over 14 s. Constant `R=0x4D` so perceived lightness stays
  constant (manual approximation of OKLCH constant-L/C — WPF has no native
  OKLCH interpolation).

`SetReduceAnimations(true)` (driven by Privacy → "Reduce pill animations" OR
Windows accessibility "Show animations" off) stops both storyboards.
State-transition animations (FadePillIn, dock slide, accent line) keep
running regardless.

### 11.10 Multi-monitor sticky behaviour

Session-only `Dictionary<deviceName, Point>` keyed by `MONITORINFOEX.szDevice`
remembers per-monitor drag positions. On `Armed`/`Recording` transitions, the
pill jumps to the focused window's monitor (at remembered or default
position). Dictionary cleared every fresh process start per user spec.
