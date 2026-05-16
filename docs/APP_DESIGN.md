\# KusPus — Full Design Specification



> This document is the single source of truth for the KusPus visual + interaction system. Hand it to a designer, an image generator, or another AI to reproduce the design across all surfaces. It is implementation-agnostic — translate the tokens and components into whatever framework you build with.



\---



\## 0. Product Summary



\*\*KusPus\*\* is a lightweight, local-first \*\*Windows dictation utility\*\*.



> Press a hotkey. Speak. The transcript pastes into whatever app has focus.



\- \*\*Audience.\*\* Power users, developers, writers — people who type all day and would rather not.

\- \*\*Stack constraint.\*\* Native Windows 11 (Mica/Acrylic). Windows 10 supported as a fallback.

\- \*\*No cloud.\*\* No accounts, no browser UI, no telemetry by default.

\- \*\*Surface count.\*\* Three user-facing surfaces total: the \*\*floating pill\*\*, the \*\*main window\*\*, and the \*\*first-launch onboarding\*\*. Plus a \*\*tray icon + menu\*\*.



The brand temperament is \*\*quiet, instant, native, calm, voice.\*\* If a decision would make the app feel louder than the OS shell around it, the decision is wrong.



\---



\## 1. Brand Identity



\### 1.1 The mark



The KusPus mark is a \*\*voice stack\*\*: five vertical pearly-mint pills arranged in a \*\*center-heavy envelope silhouette\*\* — tallest in the middle, shorter at the edges. Each bar has a faint mint glow ellipse underneath, suggesting reflected light from the visualizer in the product.



\*\*Geometry (in a 1024 × 1024 artboard):\*\*



| Bar | x | y | width | height | corner radius |

|---|---|---|---|---|---|

| 1 (outer) | 288 | 402 | 64 | 146 | 32 |

| 2 | 384 | 332 | 64 | 286 | 32 |

| 3 (center) | 480 | 260 | 64 | 424 | 32 |

| 4 | 576 | 332 | 64 | 286 | 32 |

| 5 (outer) | 672 | 402 | 64 | 146 | 32 |



\- \*\*Fill:\*\* vertical gradient `#F5F5F2` (top) → `#EEF4EE` (72 %) → `#CDEFD9` (bottom). Pearly, not pure white.

\- \*\*Glow:\*\* under each bar, a horizontal ellipse filled `#4DDBA6` at 18–20 % opacity, blurred 10 px stdDev, color-matrixed to mint. Glow ellipses sit slightly \*below\* the bar baseline.

\- \*\*Container:\*\* rounded square with corner radius \*\*22 % of the side\*\*. Background `#1C1C1E` on dark, `#F3F3F3` on light.



\### 1.2 The wordmark



\- Spelled \*\*`KusPus`\*\* — capital K, capital P. \*\*Mid-string cap is intentional\*\*; never flatten to `Kuspus` or `KUSPUS`.

\- \*\*Typeface:\*\* Segoe UI Variable, weight 600. Fallback: Segoe UI 600, then Inter 600.

\- \*\*Tracking:\*\* −0.5 % to −1 %.

\- \*\*Color:\*\* primary text token (white on dark, near-black on light) — \*\*never mint\*\*.

\- \*\*Lockup:\*\* when paired with the icon horizontally, the gap between them = 0.5 × the icon side.

\- \*\*Optional motif:\*\* a 1.5 px mint underline beneath the wordmark, \*\*136 px wide\*\*, gradient-faded at both ends — same accent stroke used over the pill's visualizer.



\### 1.3 Color palette



| Token | Value | Where used |

|---|---|---|

| \*\*Mint\*\* \*(primary brand)\* | `#4DDBA6` | Accent line, selected states, brand highlights, success affirmations |

| Mint glow | `rgba(77,219,166,0.22)` | Soft outer glows behind mint elements |

| Charcoal | `#1E1E1E` | Pill surface fill (dark, effective at 90 % opacity) |

| App background (dark) | `#202020` | Main window body |

| Surface (dark) | `#2A2A2C` | Cards, inputs in dark mode |

| Surface elevated (dark) | `#323234` | Hover state, dropdowns |

| App background (light) | `#F3F3F3` | Main window body (light) |

| Surface (light) | `#FFFFFF` | Cards in light mode |

| Error red | `#EF5350` | Error states, danger buttons |

| Success | `#4CAF50` | OK indicators in history, mic detect |

| Warning amber | `#FFB74D` | Shortcut conflict warnings |



\*\*Rules:\*\*

\- Mint is the \*\*only saturated color in normal flow\*\*. If you find yourself reaching for a second brand color, stop.

\- Error red is reserved for \*\*the error state only\*\* — not for emphasis, not for delete buttons-at-rest. A "Delete" button is ghost-styled until destructive intent is being shown (e.g., a confirmation modal).

\- Never invent gradients beyond the icon's pearly-mint gradient and the pill's surface gradient. No marketing-style hero gradients anywhere.



\### 1.4 Type system



Single family — \*\*Segoe UI Variable\*\* (fallback chain: `"Segoe UI Variable", "Segoe UI", -apple-system, system-ui, sans-serif`). One monospace family for code/keys: `ui-monospace, "Cascadia Mono", "Cascadia Code", "SF Mono", Menlo, monospace`.



| Role | Size | Weight | Letter-spacing | Notes |

|---|---|---|---|---|

| Hero / wordmark | 56 px | 600 | −0.03 em | Brand only |

| Window title | — | — | — | Use system title bar |

| Step title (onboarding) | 22 px | 600 | −0.02 em | |

| Section title | 13 px | 600 | −0.005 em | Inside main window |

| Row title | 13 px | 500 | −0.005 em | Inside list cards |

| Body | 13 px | 500 | −0.005 em | Default copy |

| Body small | 12.5 px | 500 | −0.005 em | Error pill text |

| Subtitle / secondary | 11.5–12 px | 400 | 0 | Helper copy |

| Micro-label (`RECORDING`) | 9.5 px | 500 | 0.08 em uppercase | Under visualizer |

| Eyebrow | 10.5 px | 600 | 0.1 em uppercase | Onboarding step counter |

| Mono (keys, timestamps) | 10–14 px | 500 | 0 | Cascadia Mono / monospace |



\---



\## 2. Surface 1 · The Floating Pill



99 % of daily interaction. Visible only when the app is in a dictation cycle; otherwise the OS shouldn't show it exists.



\### 2.1 Anatomy



| Property | Value |

|---|---|

| Width | \*\*200 px\*\* logical (fixed across all states) |

| Height | \*\*56 px\*\* |

| Corner radius | \*\*8 px\*\* — Windows 11 surface rounding, \*\*not iOS super-ellipse\*\* |

| Material | Mica/Acrylic on Win11; solid translucent fill on Win10 |

| Position | Bottom-center of the active monitor, \*\*40 px above the taskbar\*\* |

| Z-order | Always-on-top |

| Interaction | \*\*Click-through.\*\* Mouse events pass through; keyboard focus never moves |

| Taskbar / Alt-Tab | Hidden from both |

| Inner horizontal padding | 32 px each side (content track = 136 px) |

| Shadow (dark) | `0 12 32 rgba(0,0,0,0.45) + 0 2 6 rgba(0,0,0,0.35) + 1 px inset border` |

| Shadow (light) | `0 12 32 rgba(0,0,0,0.14) + 0 2 6 rgba(0,0,0,0.08) + 1 px inset border` |

| Top inner highlight | 1 px line, `rgba(255,255,255,0.05)` dark / `rgba(255,255,255,0.7)` light |



\### 2.2 The accent line



A thin glowing horizontal stroke at the top edge — the only chrome on the pill.



| Property | Value |

|---|---|

| Width | \*\*136 px\*\* (hugs the visualizer) |

| Height | 1.5 px |

| Position | Centered, flush to top edge (`top: 0`) |

| Fill | Horizontal gradient `transparent → accent (50%) → transparent` |

| Glow (recording/error) | Outer blur `0 0 12 px {accent}@80%` + offset `0 2 14 px {accent}@40%` |

| Opacity by state | `recording` 1.0 · `error` 1.0 · `transcribing` 0.55 · `confirmed` 0.40 · `hidden` 0 |

| Color | Mint by default (`#4DDBA6`); \*\*shifts to `#FF4D4F` (error red) instantly — no fade\*\* |



\### 2.3 The visualizer



\*\*This is the message\*\* when recording. No text is needed; the visualizer carries the meaning.



| Property | Value |

|---|---|

| Bar count | 20 |

| Bar width | 3 px |

| Bar corner radius | 2 px |

| Bar gap | 4 px |

| Total track width | \*\*136 px\*\* (20 × 3 + 19 × 4) |

| Track height | 28 px |

| Bar height range | 4 px (idle) ↔ 26 px (max) |

| Active bar fill | `rgba(255,255,255,0.92)` (dark) / `rgba(20,20,20,0.85)` (light) |

| Idle bar fill | `rgba(255,255,255,0.28)` (dark) / `rgba(20,20,20,0.25)` (light) |

| Per-bar glow | When level > 0.7, add `0 0 6 px {accent}@33%` shadow |



\#### Motion model (language-agnostic pseudocode)



```

every animation frame:

&#x20; dt = clamp(now - lastFrame, 0, 64ms) / 1000



&#x20; if (recording and now > nextTargetAt):

&#x20;   nextTargetAt = now + random(90..150) ms

&#x20;   # voice envelope — louder in center, softer at edges

&#x20;   speak = 0.55 + sin(now / 380) \* 0.25 + random(-0.125, +0.125)

&#x20;   for each bar i in 0..19:

&#x20;     center\_weight = 1 - abs(i - 9.5) / 9.5     # 1.0 at center, 0 at edges

&#x20;     base    = 0.18 + center\_weight \* 0.45

&#x20;     jitter  = random(-0.30, +0.70) \* 0.55      # asymmetric — favors loud

&#x20;     target\[i] = clamp(base \* speak + jitter, 0.05, 1.0)



&#x20; if (not recording):

&#x20;   target\[i] = 0.05 for all bars



&#x20; # damped approach (per-bar rate so they don't move in lockstep)

&#x20; for each bar i:

&#x20;   rate = recording ? (14 + (i % 3) \* 3) : 6

&#x20;   k = 1 - exp(-rate \* dt)

&#x20;   level\[i] += (target\[i] - level\[i]) \* k



&#x20; # render

&#x20; bar\_height\[i] = 4 + level\[i] \* 22

```



Replace `target\[i]` with real-amplitude × center\_weight × jitter if you have live audio data; keep the damping pass.



\### 2.4 The five states



\#### 1 · Hidden

Nothing on screen. Window not rendered (or fully transparent + non-interactive). There is \*\*no "idle" pill\*\*.



\#### 2 · Recording

\- Dark/light surface as theme dictates.

\- Mint accent line at full opacity + glow.

\- 20-bar visualizer running (motion model above).

\- 4 px below the visualizer: micro-label \*\*`RECORDING`\*\* in 9.5 px uppercase, letter-spacing 0.08 em, muted-text color.



\#### 3 · Transcribing

\- Surface unchanged.

\- Accent line opacity drops to 0.55.

\- Center content: \*\*14 px spinner\*\* (1.5 px stroke, \~270° dash arc, 0.9 s linear rotation) + \*\*`Transcribing…`\*\* in 13 px / 500 / primary-text-color. 8 px gap.



\#### 4 · Paste confirmed

\- Surface unchanged.

\- Accent line opacity drops to 0.40.

\- Visualizer \*\*continues to run\*\* but at \*\*35 % opacity\*\* with a \*\*radial mask\*\* punching a transparent hole through the middle:



```

mask-image: radial-gradient(

&#x20; ellipse 60% 140% at 50% 50%,

&#x20; transparent       0%,

&#x20; transparent      35%,

&#x20; rgba(0,0,0,0.55) 60%,

&#x20; black            88%

);

```



\- Text \*\*`Pasted into <App>`\*\* fades in (200 ms, opacity 0→1 + translateY 2→0). `<App>` is \*\*bold 600\*\*, the rest is \*\*500\*\*. 13 px.

\- Holds 1 s, then the whole pill fades out (120 ms).



\#### 5 · Error

\- Surface unchanged.

\- Accent line \*\*shifts to red `#FF4D4F` instantly\*\* — no fade on color.

\- Center content: a 5 × 5 px red dot with a soft red glow + brief error text (12.5 px / 500). 8 px gap.

\- Holds \~2 s, then the pill fades out.

\- Copy must fit the 136 px content track at 12.5 px. Truncate with `…`. Approved short forms: `Microphone blocked`, `Disk full`, `No internet`, `Window closed`, `Paste failed`, `Model not loaded`.



\### 2.5 Pill motion language



| Transition | Duration | Curve | Notes |

|---|---|---|---|

| Appear (hidden → any) | 120 ms | ease-out | Opacity 0 → 1. No scale, no slide, no bounce. |

| Disappear (any → hidden) | 120 ms | ease-in | Opacity 1 → 0. |

| State content crossfade | 150 ms | ease | Old content fades out, new fades in. |

| Confirmation choreography | 200 + 1000 + 120 ms | — | See §2.6 |

| Error accent color shift | \*\*0 ms\*\* | — | Instant — no fade on the color, only on appearance. |



\### 2.6 Confirmation choreography



```

t = 0      transcribing showing

t = 0      transcript ready → switch to confirmed state

t = 0      visualizer ghosts to 35 % opacity, mask applies, accent line drops to 0.4

t = 0..200 "Pasted into <App>" text fades in (opacity 0→1, translateY 2→0)

t = 1000   begin pill fade-out (120 ms)

t = 1120   pill fully hidden

```



\### 2.7 Reduced motion

Respect the OS reduced-motion preference: hold all bars at the mean level (\~0.45), no animation. All fades become instant.



\---



\## 3. Surface 2 · The Main Window



Opened intentionally from the tray. Closing hides (does not quit).



\### 3.1 Window chrome



| Property | Value |

|---|---|

| Default size | 880 × 620 |

| Minimum size | 820 × 600 |

| Resizable | Yes |

| Title bar | \*\*System chrome\*\* — no custom title bar. Show app icon (16 px, monochrome) + "KusPus" wordmark on the left of the bar. |

| Title bar height | 32 px |

| Title bar background | `#1A1A1C` (dark) / `#EAEAEC` (light) |

| Min / Max / Close | Native chrome behavior. Close button shows `#E81123` hover background. |

| Closing | \*\*Hides the window\*\* — quit is only via the tray menu. |

| Corner radius | 8 px on the outer window |



\### 3.2 Layout



Two-pane: \*\*left sidebar (200 px, fixed) + right content area (fills)\*\*.



\#### Sidebar

\- Background: `#1B1B1D` (dark) / `#ECECEE` (light).

\- Vertical 1 px right divider (`rgba(255,255,255,0.06)` dark / `rgba(0,0,0,0.06)` light).

\- Padding: 14 px vertical, 10 px horizontal.

\- 6 tab buttons stacked vertically. Each:

&#x20; - 9 px × 12 px padding, 6 px corner radius, 12 px icon + label gap.

&#x20; - Icon (16 px, line-style, 1.4 px stroke).

&#x20; - Label in 12.5 px / 500.

&#x20; - \*\*Selected:\*\* elevated background (`#2A2A2C` dark / `#FFFFFF` light), icon turns mint (`#4DDBA6`), label gets full primary text color, soft `0 1 2 rgba(0,0,0,0.08)` shadow.

&#x20; - \*\*Selected accent stripe:\*\* 3 × 18 px mint pill, 2 px corner radius, sitting just left of the sidebar's inner padding.

&#x20; - \*\*Hover (non-selected):\*\* `rgba(255,255,255,0.05)` (dark) / `rgba(0,0,0,0.04)` (light).

\- \*\*Sidebar footer pill:\*\* small status row at the very bottom of the sidebar — mint dot + `Idle · tiny.en` + monospaced hotkey glyph `⌃⊞`.



\#### Content area

\- Padding: 28 px top / 36 px sides / 32 px bottom.

\- Vertical scroll only.

\- Background: `#202020` (dark) / `#F3F3F3` (light).



\### 3.3 The six tabs



The order is fixed: \*\*General → Audio → Models → History → Privacy → About.\*\*



\#### Tab 1 · General



The most-changed settings. Three sections:



1\. \*\*Hotkey\*\* \*(hero control)\*

&#x20;  - Big tappable card (\~440 px wide). 20 × 22 px padding, 10 px corner radius, 1.5 px border (solid when idle, \*\*dashed mint when listening\*\*).

&#x20;  - Eyebrow: `HOTKEY` uppercase 10.5 px, mint when listening.

&#x20;  - Each chord key rendered as a \*\*monospaced keycap\*\*: 7 × 14 px padding, 6 px corner radius, surface background, 1 px strong border, faint `0 1 0 rgba(0,0,0,0.2)` bottom shadow.

&#x20;  - `+` separator between keys in muted-text color.

&#x20;  - Default chord: \*\*`LCtrl + LWin`\*\*.

&#x20;  - \*\*Conflict warning row:\*\* amber dot + bold "Shortcut conflict." + explanation. Appears inline below the picker when the chord clashes with a Windows shortcut.



2\. \*\*Startup\*\*

&#x20;  - One row card: title `Launch KusPus when I sign in` + subtitle, with a toggle on the right. Default \*\*OFF\*\*.



3\. \*\*Appearance\*\*

&#x20;  - One row card: title `Theme` + 3-way segmented control `Auto / Light / Dark`. Default `Auto`.



\#### Tab 2 · Audio



1\. \*\*Input device\*\* — one row: device label + native-styled `<select>` showing `Microphone (USBAudio1.0)`, `Headset Microphone (Bluetooth)`, etc.

2\. \*\*Live level\*\* — one row showing a \*\*horizontal 5-bar level meter\*\* (200 px wide). Same visual style as the pill's visualizer but only 5 bars wide. Bars animate live when the tab is open and mic is active.

3\. \*\*Test transcription\*\* — two stacked rows: button row ("Test" → records 5 s, transcribes) and result row (readonly text panel inside a dashed surface; empty state `Press Test to record a sample.`).



\#### Tab 3 · Models



1\. \*\*Active model\*\* — one row: mint dot + model name + size/speed subtitle + ghost "Change" button.

2\. \*\*Available models\*\* — stacked card list. Each row:

&#x20;  - Radio input (mint accent color), disabled if not installed.

&#x20;  - Title + optional \*\*`Bundled` mint pill badge\*\* (for the tiny model).

&#x20;  - Subtitle: size · speed (e.g. `75 MB · Fastest`).

&#x20;  - Status indicator on the right depending on state:

&#x20;    - \*\*Installed:\*\* mint-success dot + `Installed` text.

&#x20;    - \*\*Downloading:\*\* inline 4 px-tall mint progress bar (180 px) + percent in mono + ghost `Cancel`.

&#x20;    - \*\*Not installed:\*\* secondary `Download` button.

3\. \*\*Custom model link\*\* — small text link below the list pointing to `…\\models\\` + `settings.json`. No first-class UI in v1.



Model list (English-only in v1):

\- `Tiny (English)` · 75 MB · Fastest · \*\*bundled\*\*

\- `Base (English)` · 142 MB · Fast

\- `Small (English)` · 466 MB · Balanced

\- `Medium (English)` · 1.5 GB · Accurate

\- `Large v3` · 3.1 GB · Most accurate · multilingual



\#### Tab 4 · History



1\. \*\*Search bar at top.\*\* 10 px corner radius, search glyph at left, placeholder `Search transcripts…`.

2\. \*\*Card list.\*\* Each card (8 px corner radius, 1 px border, 12 × 14 px padding):

&#x20;  - Header row: status dot (mint = OK, red = failed) + monospaced relative timestamp + app name on the right (`Slack`, `VS Code`, `Notion`, `Outlook`).

&#x20;  - Transcript line — single line, 13 px, primary text, ellipsis on overflow. If failed: italic + red.

&#x20;  - Footer row: 2 badge chips (model · duration) + spacer + ghost action buttons (`Copy`, optional `Retry` if failed, `Delete`).

3\. \*\*Bulk footer.\*\* 1 px divider above. Subtle stats (`47 transcripts · 18.3 MB of audio retained`) + danger-style ghost button `Purge all history`. Clicking opens a confirmation modal.



\#### Tab 5 · Privacy



1\. \*\*Offline mode\*\* — one row: `Block all outbound traffic` toggle. Subtitle changes based on state: when on → `Killswitch enabled — no network requests will be made.` when off → `KusPus may reach model and crash-report servers when needed.`

2\. \*\*Telemetry\*\* — one row: `Send opt-in crash reports` toggle. Default \*\*OFF\*\*. Subtitle: `No transcript text, audio, or clipboard contents are ever sent.`

3\. \*\*Logs\*\* — two rows: total log size + `Clear logs` ghost button; log folder path + `Open in Explorer` ghost button.

4\. \*\*Local-first promise card\*\* — full-width mint-tinted card at the bottom. Mint headline `Local-first.` + reassuring copy.



\#### Tab 6 · About



\- \*\*Header row:\*\* 80 px brand icon + wordmark + version line + monospaced build date / Git SHA.

\- \*\*Row card group:\*\* source code link + GitHub button, log folder row, \*\*Re-run onboarding\*\* row (triggers the 7-step flow again).

\- \*\*License blurb\*\* at the bottom: `MIT licensed · Local-first · No telemetry.` plus one warm tagline.



\### 3.4 Reusable component patterns (used across tabs)



\#### Row (list card)

\- `display: flex`, vertical align center, gap 14 px, padding `14 × 16 px`.

\- Background: surface (`#2A2A2C` dark / `#FFFFFF` light).

\- Hoverable: shifts to elevated surface (`#323234` / `#FAFAFA`) on hover when clickable.

\- Stacking radius: first row `8 8 0 0`, middle 0, last `0 0 8 8`, alone `8`. \*\*Gap between rows: 1 px\*\* (set via container `gap`) so they read as a single grouped card.

\- Inner `RowLabel`: title (13 / 500) + optional subtitle (11.5 / 400, 3 px gap, line-height 1.45).



\#### Toggle (switch)

\- 36 × 20 px pill, 10 px corner radius.

\- Off: `borderStrong` background, 14 px white knob inset 3 px from left, soft shadow.

\- On: mint background, knob shifts to inset 3 px from right, knob fill darkens to `#0F1F18`.

\- Transition: `background 150ms ease`, `left 150ms ease`.



\#### Button

4 kinds, 3 sizes.



| Kind | Background | Text | Border |

|---|---|---|---|

| primary | mint `#4DDBA6` | `#0F1F18` | transparent |

| secondary | elevated surface | primary text | 1 px border token |

| ghost | transparent | primary text | 1 px border token |

| danger | transparent | error red | 1 px error red @ 33 % |



| Size | Padding | Font size |

|---|---|---|

| sm | 5 × 10 px | 11.5 px |

| md (default) | 7 × 14 px | 12.5 px |

| lg | 11 × 20 px | 14 px |



All buttons: 6 px corner radius, weight 500, letter-spacing −0.005 em, hover applies `filter: brightness(1.1)`.



\#### Segmented control

\- Outer pill: 7 px corner radius, 2 px padding, 1 px border, surface-input background.

\- Each segment: 6 × 14 px padding, 5 px corner radius, 12 px / 500 label.

\- Selected: elevated background + primary text + `0 1 3 rgba(0,0,0,0.15)` shadow.

\- Unselected: transparent + secondary text.



\#### Badge (small pill chip)

\- Padding 2 × 7 px, 4 px corner radius, 10.5 px / 500, letter-spacing 0.01 em.

\- Default: surface-input fill, border token, secondary text.

\- Colored: pass a hex; fill becomes `{hex}20`, border `{hex}33`, text `{hex}`.



\#### Dot (status indicator)

\- 7 × 7 px circle.

\- Box-shadow `0 0 6 px {color}88` for a soft glow.



\#### Hotkey picker

\- See \*\*§3.3 Tab 1 · General\*\*. Use this same component on \*\*onboarding step 2\*\*.



\---



\## 4. Surface 3 · Onboarding (7-step modal)



Shown on first launch. Modal \*\*over a dimmed desktop\*\* (not a tab inside the main window).



\### 4.1 Window



| Property | Value |

|---|---|

| Size | 720 × 520, \*\*centered on screen\*\* |

| Background | App background (`#202020` dark / `#F3F3F3` light) |

| Corner radius | 12 px |

| Shadow | `0 40 80 rgba(0,0,0,0.55)` (dark) / `0 40 80 rgba(0,0,0,0.20)` (light) + 1 px hairline border |

| Backdrop | The desktop behind, dimmed roughly 50 % toward black |



\### 4.2 Layout



Top → bottom:



1\. \*\*Progress dots header.\*\* 20 px top padding. Centered row of dots:

&#x20;  - Active dot: 22 × 6 px mint pill (3 px corner radius).

&#x20;  - Completed dots: 6 × 6 px mint @ 33 %.

&#x20;  - Future dots: 6 × 6 px in `borderStrong`.

&#x20;  - Each dot is clickable (jumps to step).

2\. \*\*Step content area.\*\* 20 px top / 40 px sides padding, flexes to fill.

3\. \*\*Footer.\*\* 16 × 28 px padding, 1 px top divider.

&#x20;  - `Skip onboarding` text link on the far left (muted on step 1, secondary thereafter).

&#x20;  - `Back` ghost button (hidden on step 1).

&#x20;  - `Next` primary button (or `Finish` on the last step).



\### 4.3 Step content scaffold



Every step uses the same scaffold:



\- \*\*Eyebrow:\*\* `STEP N OF 7` in 10.5 px uppercase mint @ 600 weight, 0.1 em tracking.

\- \*\*Title:\*\* 22 px / 600 / −0.02 em.

\- \*\*Subtitle:\*\* 13 px / 400 / secondary text, line-height 1.55, max width 540 px.

\- \*\*Body:\*\* the unique content for that step.



\### 4.4 The seven steps



\#### Step 1 · Welcome

\- \*\*Title:\*\* `Welcome to KusPus`

\- \*\*Subtitle:\*\* `A quiet, local-first way to turn your voice into text — anywhere on Windows.`

\- \*\*Body:\*\*

&#x20; - Big visual: a stylized desktop "screenshot" (radial gradient backdrop + 22 px taskbar hint) with the \*\*actual pill in `recording` state\*\* centered.

&#x20; - Below: a 3-column grid of value-prop cards: `Hotkey-driven`, `Local-first`, `Invisible`. Each card: 8 px corner radius, surface fill, 1 px border, label 12 / 600 + detail 11 / 400.



\#### Step 2 · Hotkey picker

\- \*\*Title:\*\* `Pick your hotkey`

\- \*\*Subtitle:\*\* `Hold this chord anywhere in Windows to start dictation. Release to stop and paste.`

\- \*\*Body:\*\*

&#x20; - The full hotkey picker component (same as in General tab).

&#x20; - Helper line below in 11.5 px secondary text: `Now press the keys you want to use…` when listening, otherwise `Tap the picker, then press a new chord.`

&#x20; - If a known Windows conflict is detected after capture, the amber warning row appears inline.



\#### Step 3 · Microphone check

\- \*\*Title:\*\* `Check your mic`

\- \*\*Subtitle:\*\* `We need permission to listen while you hold the hotkey. Nothing is recorded otherwise.`

\- \*\*Body:\*\*

&#x20; - Centered mic card: device label in micro-uppercase + 200 px-wide live 5-bar meter (mint when active) + success line: success-dot + `Receiving audio` in success-green.

&#x20; - Below the card: `Test again` ghost button.

&#x20; - \*\*Error variant:\*\* if the mic is blocked, the card flips to red — error-red border + `Microphone blocked` error message + primary `Open Settings` button linking to `ms-settings:privacy-microphone`.



\#### Step 4 · Autostart

\- \*\*Title:\*\* `Start with Windows?`

\- \*\*Subtitle:\*\* `KusPus is most useful when it's already running. You can change this later in Preferences.`

\- \*\*Body:\*\* a single \*\*ToggleCard\*\* — full-width clickable card containing title, subtitle, and toggle. Default \*\*OFF\*\*. Card border highlights mint when toggle is on.



\#### Step 5 · Crash reports

\- \*\*Title:\*\* `Help improve KusPus?`

\- \*\*Subtitle:\*\* `Crash reports are anonymous and opt-in. Everything else stays local. Always.`

\- \*\*Body:\*\* another \*\*ToggleCard\*\* for opt-in crash reports (default \*\*OFF\*\*) + below it the \*\*mint-tinted local-first promise card\*\* repeating the same reassurance copy used in Privacy tab.



\#### Step 6 · Try it

\- \*\*Title:\*\* `Try your first dictation`

\- \*\*Subtitle:\*\* `Press the hotkey, say one sentence, then release.`

\- \*\*Body:\*\*

&#x20; - A 120 px-tall \*\*transcript surface\*\* with a dashed border (mint @ 33 % once a transcript has appeared, otherwise standard border). Empty state in italic muted text: `Your transcript will appear here.`

&#x20; - Centered button row below: primary `Simulate dictation` (or `Try another` after success) + ghost `Clear` once there's content.

&#x20; - On simulate: \~1.8 s "Listening…" then a sample sentence appears with a soft mint glow + inset-mint border.



\#### Step 7 · Done

\- \*\*Title:\*\* `You're set up`

\- \*\*Subtitle:\*\* `KusPus lives in your system tray. Right-click the icon to change anything.`

\- \*\*Body:\*\* \*\*TrayDiagram\*\* — a stylized corner-of-screen illustration:

&#x20; - Original-geometry gradient backdrop, 36 px taskbar at the bottom, a row of placeholder tray icons on the right, with the \*\*KusPus tray icon highlighted\*\* (mint-tinted slot with a slow `kp-pulse` 1.6 s breathing animation).

&#x20; - A dashed mint arrow curves from a copy block in the upper-left ("`Your KusPus icon lives here.`" + "Right-click for the menu.") down to the tray slot.

\- Footer button changes from `Next` to `Finish`.



\---



\## 5. Surface 4 · Tray Icon + Menu



\### 5.1 Tray icon states



The tray icon is the monochrome 5-bar mark, \*\*without the glow ellipses\*\* (too noisy at 16 px). It changes appearance by state:



| State | Bar color (dark theme) | Bar color (light theme) | Overlay |

|---|---|---|---|

| \*\*Idle\*\* | `#E2E2E4` | `#202020` | none |

| \*\*Recording\*\* | \*\*`#4DDBA6`\*\* (mint) | \*\*`#4DDBA6`\*\* (mint) | small \*\*red dot\*\* badge at bottom-right (\~40 % of icon size), with a 1.5 px halo ring matching the tray bg |

| \*\*Error\*\* | `#E2E2E4` / `#202020` | same | small \*\*red caution triangle\*\* with white `!` at bottom-right (\~50 % of icon size), with a halo ring matching the tray bg for legibility |



\*\*Important:\*\* the \*recording\* and \*error\* overlays must be visually distinct. Recording is a soft red dot — "we are live." Error is a hard red triangle with an exclamation — "something needs attention."



\#### Caution badge geometry (in a 24 × 24 SVG viewBox)

\- Outer halo path: `M12 2.5 L 22.5 21 H 1.5 Z`, filled and stroked in the tray ring color (3.2 px stroke, miter join).

\- Inner triangle: `M12 4 L 21 20 H 3 Z`, filled error red (`#EF5350`).

\- Exclamation: 1.8 × 6.2 px rounded rect at `(11.1, 9)` + 1.1 px-radius circle at `(12, 17.6)`, both fill `#FFFFFF`.

\- Position over the icon: bottom-right, offset by \~12 % of the badge size into the icon's bounds.



\### 5.2 Tray right-click menu



A floating menu, 240 px wide, opens at the cursor on right-click of the tray icon.



| Property | Value |

|---|---|

| Background | `rgba(38,38,40,0.95)` dark / `rgba(252,252,254,0.96)` light |

| Material | `backdrop-filter: blur(40px) saturate(140%)` |

| Border | 1 px `borderStrong` token |

| Corner radius | 8 px |

| Shadow | `0 16 40 rgba(0,0,0,0.35)` |

| Padding | 4 px vertical, 0 horizontal (items have their own padding) |



\#### Items (top to bottom)



1\. \*\*Header row\*\* — `KusPus` (11.5 / 600) + subline `Version 1.0.0 · {state}` in 10 / muted. Non-clickable. Pairs with a 20 px brand icon on the left. \*\*8 / 14 / 6 px padding.\*\*

2\. \*Divider\* — 1 px line in divider token, 4 / 8 px margin.

3\. \*\*Toggle recorder\*\* — mic-glyph icon (13 × 13 line, 1.4 stroke) + label + `⌃⊞` accelerator on the right in mono 10.5 px.

4\. \*\*Active model: {name} ▸\*\* — model-glyph icon + label, with the active model name highlighted \*\*mint\*\*. Submenu arrow at the end. Hovering reveals a flyout of installed models (radio group).

5\. \*Divider.\*

6\. \*\*Preferences…\*\* — gear icon. Opens main window on the General tab.

7\. \*\*History…\*\* — clock icon. Opens main window on the History tab.

8\. \*Divider.\*

9\. \*\*Quit\*\* — danger-red text. The only way to fully exit the app.



\#### Item interaction

\- Hover background: `{mint}@15%` for normal items, `{errorRed}@15%` for the Quit item.

\- 6 / 14 px padding, 12.5 px / regular weight, 4 px corner radius, 4 px outer margin so the hover state floats inside the menu.

\- Icon column is 14 px wide, secondary-text color, centered.



\---



\## 6. Error Surface Catalog



Every error reuses the same pill geometry. \*\*Only the message and the red accent change.\*\* No modal dialogs, no blinking, no sound.



| Scenario | Where it appears | Message |

|---|---|---|

| Mic blocked in Privacy settings | Pill | `Microphone blocked` |

| Disk full while recording | Pill | `Disk full` |

| Active window closed before paste | Pill | `Window closed` |

| Elevated target window (paste failed) | Pill | `Paste failed` |

| No internet (offline mode active or no network) | Pill | `No internet` |

| Active model not loaded | Pill | `Model not loaded` |

| Model download failed | Main window → Models tab | inline red message + `Retry` button on the model row |

| Corrupt model (SHA mismatch) | Main window → Models tab | inline red message + `Re-download` button |



For pill errors, the user sees the message for \~2 s. The app does not steal focus and does not retry automatically — let the user re-trigger.



\---



\## 7. Motion Language (Global)



A small, opinionated set. \*\*Snappy, no bounce, no spring.\*\*



| Surface event | Duration | Curve |

|---|---|---|

| Pill appear / disappear | 120 ms | ease-out / ease-in |

| Pill state crossfade | 150 ms | ease |

| Pill confirmation (full sequence) | \~1320 ms | see §2.6 |

| Toggle switch | 150 ms | ease |

| Sidebar tab hover | 100 ms | ease |

| Sidebar tab select | 120 ms | ease |

| Button filter brightness | 100 ms | ease |

| Onboarding progress dot | 200 ms | ease |

| Try-it-now success glow | 200 ms | ease |

| Tray icon `kp-pulse` (recording highlight) | 1600 ms loop | ease-in-out |

| Visualizer spinner rotation | 900 ms loop | linear |



No element ever scales on appear/disappear. Nothing bounces. No spring physics anywhere.



\---



\## 8. System Integration \& Behavior



\### 8.1 Hotkey

\- Default: \*\*`LCtrl + LWin`\*\*.

\- Mode: \*\*push-to-talk by default\*\*; tap-toggle is a secondary mode (out of scope for v1 UI).

\- Detection: when the picker is in "listening" mode, the next key chord pressed becomes the new shortcut.

\- Conflict detection: if the chord matches a known Windows shortcut (`Win+L`, `Win+D`, `Ctrl+Alt+Del`, etc.), show the amber warning row inline.



\### 8.2 Multi-monitor

The pill appears on the monitor containing the focused window. Falls back to the primary monitor if no app is focused.



\### 8.3 Click-through / focus

\- The pill never accepts mouse events.

\- The pill never moves OS keyboard focus.

\- The previously-focused app must remain the paste target the entire time.



\### 8.4 DPI scaling

All dimensions in this spec are \*\*logical pixels\*\*. Multiply by the current display scale factor when sizing native windows.



\### 8.5 Close vs. quit

\- \*\*Close\*\* (clicking the X on the main window) → hides the window. The app stays running.

\- \*\*Quit\*\* → only via the tray menu's `Quit` item.



\### 8.6 Reduced motion

Respect the OS reduced-motion preference everywhere — instant transitions, no animated visualizer.



\### 8.7 Theme

\- \*\*Auto\*\* (default) follows the Windows app-mode setting (`AppsUseLightTheme` registry value).

\- Manual `Light` / `Dark` override the OS preference for KusPus only.



\---



\## 9. Design Principles (Tiebreakers)



When a decision is ambiguous, use these in order:



1\. \*\*Native to Windows, not macOS, not the web.\*\* Match the surrounding shell.

2\. \*\*Invisible until needed.\*\* The user should forget the app is running until they press the hotkey.

3\. \*\*No chrome.\*\* No titlebar, close button, app icon, or window-frame shadow inside the pill. Use system chrome on the main window — never custom.

4\. \*\*One-glance readability.\*\* Whatever state the pill is in, a user reading it from 2 feet away should understand it in <200 ms.

5\. \*\*Single fixed pill footprint.\*\* Never resize the pill between states — only the contents change.

6\. \*\*Mint is the only saturated color in normal flow.\*\* Red is reserved for errors and destructive intent.

7\. \*\*Local-first as a brand value.\*\* Lean into it visibly — privacy is not buried in a settings tab, it has a card on Privacy and a callout in onboarding.

8\. \*\*Calm, not clinical.\*\* Dark mode is charcoal, not pure black. Type is medium weight, not bold-everywhere. Errors are informative, not alarming.



\---



\## 10. What NOT to Design



\- ❌ No splash screen.

\- ❌ No update prompt UI (auto-update deferred).

\- ❌ No login / account / subscription UI.

\- ❌ No GPU selection UI (CPU-only in v1).

\- ❌ No multi-language picker (English-only in v1).

\- ❌ No custom model import wizard (`settings.json` only).

\- ❌ No password-field detection UI.

\- ❌ No sound-wave squiggles or frequency-domain graphs anywhere — the 20-bar vertical visualizer is the only audio representation.

\- ❌ No microphone glyph in the brand mark (the voice stack is the only mark).

\- ❌ No iOS-style super-ellipse corners (Win11 rounding only).

\- ❌ No drop shadows that look like window frames.

\- ❌ No AI-sparkle or magic-wand iconography. KusPus is a utility, not an "AI experience."



\---



\## 11. File Inventory (for asset delivery)



Recommended deliverables for engineering handoff:



```

brand/

&#x20; kuspus-icon.svg           # the voice-stack mark, full-color (vector)

&#x20; kuspus-icon-mono.svg      # monochrome variant

&#x20; kuspus-icon.ico           # multi-size: 16, 24, 32, 48, 64, 128, 256

&#x20; kuspus-wordmark.svg

&#x20; kuspus-lockup.svg         # icon + wordmark



tokens/

&#x20; colors.json               # all color tokens (dark + light)

&#x20; type.json                 # font stacks, sizes, weights

&#x20; motion.json               # durations + curves

&#x20; spacing.json              # padding/gap scale

```



\---



\## 12. One-paragraph summary (for handoff prompts)



> KusPus is a quiet, local-first Windows dictation utility. The primary surface is a 200 × 56 px floating pill that appears bottom-center on the active monitor, \~40 px above the taskbar, only while a dictation cycle is active. It uses Mica/Acrylic material with an 8 px Windows 11 corner radius and a thin mint (`#4DDBA6`) accent line over a 20-bar voice visualizer. The pill has five states — hidden, recording, transcribing, paste confirmed, error — and the only motion language is short 120–200 ms opacity fades, no bounces, no scale. The brand mark is a center-heavy stack of five pearly-mint vertical pills, each with a soft mint glow underneath, set inside a Win11-rounded square. Two other surfaces exist: a system-chromed main window (880 × 620, six left-tab settings — General, Audio, Models, History, Privacy, About) and a 720 × 520 seven-step first-launch onboarding modal. A tray icon (mint when recording, monochrome with a red caution triangle on error) and a translucent right-click menu round out the system. Typography is Segoe UI Variable throughout. Mint is the only saturated color in normal flow; red is reserved for errors. The whole thing is designed to feel like it shipped with Windows, not like a third-party widget.



\---



\## 13. Refinements — UI/UX audit findings



Source: a full UI/UX audit of the Preferences window against §3 (Main Window) and §1.4 (Type) — see conversation log dated 2026-05-17. This section records the \*\*target behaviour\*\*; the table at the end (\*\*§13.5\*\*) tracks individual fix progress.



The audit verdict: the window has the right bones — sidebar nav, themed surfaces, mint accent line, system chrome — but it ships as \*\*a collection of one-off styles, not a design system\*\*. §3.4 prescribes four button kinds, badge, dot, row, segmented control, toggle; only Toggle and Segmented exist as styles. Buttons and rows are hand-rolled at every call site. Several controls silently lie about their own state (most notably Crash Reports while Offline Mode is on), and several tabs leak internal-milestone copy ("lands in Phase 11") to the end user.



\### 13.1 State-dependent control enablement (binding rule)



When one preference \*\*nullifies\*\* another at runtime, the dependent control must \*\*reflect that nullification visually\*\*. A toggle that shows ON while its underlying behaviour is suppressed elsewhere is a state lie and never acceptable.



The canonical case:



\- \*\*Crash Reports vs. Offline Mode.\*\* When Offline Mode is ON, Sentry is shut down regardless of the Crash Reports toggle (TECH\_SPEC §19 "Forced disable"). The Crash Reports toggle must therefore \*\*render disabled\*\* — knob dimmed (~38 % opacity), background `SurfaceElevated`, no hover affordance — and its subtitle must read \*\*`Disabled while Offline Mode is on.`\*\* on a single line, replacing the default subtitle. When Offline Mode flips OFF, the toggle re-enables and the original subtitle returns. The Toggle's `IsChecked` value is preserved across the disable cycle so the user's intent isn't silently lost.



The same rule generalises to any future "requires internet" control (per-model download, opt-in analytics, etc.): if Offline Mode would gate the behaviour, the control disables and its subtitle explains why.



\### 13.2 Copy guidelines for the Main Window



\- \*\*No internal phase numbers in user-facing strings.\*\* "Lands in Phase 11", "Wiring lands in a follow-up cluster", "Phase 10 onboarding" — none of these belong in the shipped UI. Either implement the feature, or hide the row/section that would have advertised it. A permanently-disabled-but-explained row is worse UX than no row at all.

\- \*\*No Win32 / registry / API jargon in subtitles.\*\* "Adds a HKCU\Run entry" is a developer's note, not user copy. Rewrite in terms of user-observable outcome: \*\*`Starts KusPus when you sign in to Windows.`\*\* The tray-always-available reassurance becomes a second sentence in the same subtitle: \*\*`The tray icon stays available either way.`\*\*

\- \*\*Telemetry copy stays plain.\*\* The Crash Reports row's subtitle is \*\*`Anonymous, opt-in. Never includes transcripts, audio, or clipboard contents.`\*\* — slightly tighter than the v1 wording, retains the same promise.



\### 13.3 Sidebar footer — live state binding contract



The 200 px sidebar's bottom row is a \*\*live status indicator\*\*, not decoration. It binds to two sources:



| Element | Source | Update trigger |

|---|---|---|

| Mint dot + status label (`Idle · {model}`) | `AppCoordinator.State` snapshot + `IPrefsStore.Current.Models.ActiveModelId` | On any coordinator state transition; on settings change |

| Hotkey glyph (mono, right-aligned) | `IPrefsStore.Current.Hotkey` | On settings change |



\- Status text format: `{State} · {ModelDisplayName}` where `State` ∈ `{Idle, Recording, Transcribing, Confirmed, Error}` and `ModelDisplayName` is the short form (e.g. `tiny.en`, `base.en`, `custom`).

\- Hotkey glyph format: \*\*compact text, not platform symbol soup\*\*. The Unicode `⌃⊞` placeholder is replaced by the joined `FriendlyKey` shorthand of the current chord (e.g. `Ctrl+Win`, `Ctrl+Alt+Space`). Mono font, MutedText colour.

\- A stale or hardcoded footer is a bug: the footer changes whenever the underlying value changes, with no exception.



\### 13.4 Inline keycap inside helper text



When helper text references a keyboard key (`ESC to cancel`, `Press Enter`), the key glyph is rendered \*\*inline\*\* as a small mono Keycap (same visual language as §3.4 Keycap, scaled down):



\- Padding 1 × 5 px, 4 px corner radius, `KeycapBg` background, `KeycapBorder` 1 px border.

\- Font: `Cascadia Mono` 10.5 px / 500.

\- Inline using WPF `Run` + `InlineUIContainer`, baseline-aligned to the surrounding `MutedText`.



Applied first in: hotkey-picker listen-mode hint (`Now press the keys you want to use… [ESC] to cancel`).



\### 13.5 Audit findings — progress ledger



\*\*Priority key.\*\* P0 = ship-blocker (broken logic, contrast failure, state lie). P1 = design-system gap or spec deviation. P2 = polish.



\*\*Status key.\*\* `todo` · `wip` · `done` · `deferred`.



| ID | P | Title | File (audit reference) | Status |

|---|---|---|---|---|

| P0-1 | P0 | Crash Reports gated by Offline Mode (§13.1) | `MainWindow.xaml:569-604` + `CrashReporter.cs:88-100` | done |

| P0-2 | P0 | Replace stale "Phase X" + Win32 copy (§13.2) | `MainWindow.xaml:344, 418, 485, 527, 752` | done |

| P0-3 | P0 | Raise `DisabledText` contrast to ≥ 4.5:1 | `ThemeTokens.cs:40` | todo |

| P0-4 | P0 | Sidebar footer live binding (§13.3) | `MainWindow.xaml:233-246` | done |

| P0-5 | P0 | Audio tab mic-active privacy disclosure | `MainWindow.xaml.cs:692-738` | todo |

| P1-1 | P1 | Button styles (primary / secondary / ghost / danger × sm/md/lg) — replace 5 inline-styled call sites | `Styles/Buttons.xaml` + `MainWindow.xaml` | done |

| P1-2 | P1 | History search bar + bulk footer + purge-all flow | `MainWindow.xaml:534-544` | todo |

| P1-3 | P1 | Privacy Logs size row + Clear logs ghost-danger button | `MainWindow.xaml:607-625` | todo |

| P1-4 | P1 | Models tab download flow for non-installed entries | `MainWindow.xaml.cs:853-930` | todo |

| P1-5 | P1 | Migrate Models + History row rendering to DataTemplates (remove `Theme()` brush resolver) | `MainWindow.xaml.cs:385-386, 873-906` | todo |

| P1-6 | P1 | Typography style set (`SectionHeader`, `RowTitle`, `RowSubtitle`, `Eyebrow`, `KeyMono`, `BodySmall`) replacing ~40 inline triplets | `Styles/Typography.xaml` | done |

| P1-7 | P1 | `StatusDot` style replacing 6 hand-rolled Ellipses | `Styles/Dot.xaml` | done |

| P1-8 | P1 | Section gap rhythm — drop negative margins, use one vertical spacing scale | `MainWindow.xaml:299-329` | done |

| P1-9 | P1 | Keyboard focus visuals on sidebar tabs, segments, toggles, buttons | `Styles/Focus.xaml` + Style blocks | done |

| P1-10 | P1 | Extract per-tab `UserControl`s (`GeneralView`, `AudioView`, …) per TECH\_SPEC §22 | `Views/*.xaml` (new) | deferred (post-W3) |

| P2-1 | P2 | Copy: "HKCU\Run entry" → user-language rewrite (§13.2) | `MainWindow.xaml:344` | done |

| P2-2 | P2 | Copy: Crash Reports subtitle (§13.2) | `MainWindow.xaml:591-596` | done |

| P2-3 | P2 | Local-first headline → existing type role (drop 14/SemiBold) | `MainWindow.xaml:633-637` | todo |

| P2-4 | P2 | Inline `[ESC]` keycap in hotkey-listen hint (§13.4) | `MainWindow.xaml.cs:417` | done |

| P2-5 | P2 | Reflow ConflictRow inside Hotkey StackPanel — drop negative margin tuck | `MainWindow.xaml:307` | done |

| P2-6 | P2 | Bump `HoverSubtle` brush to 8–10 % so sidebar hover is visible | `ThemeTokens.cs:43` | todo |

| P2-7 | P2 | Verify GitHub repo URL exists at the case in About tab | `MainWindow.xaml:691` | todo |

| P2-8 | P2 | Delete unreachable `(none)` empty-state branch on Models tab | `MainWindow.xaml.cs:839-841` | todo |

| P2-9 | P2 | Empty-state for History search-with-zero-results | `MainWindow.xaml.cs:1015-1026` | todo |

| P2-10 | P2 | De-duplicate "Open in Explorer" (Privacy ↔ About) | `MainWindow.xaml:616-623, 729-736` | todo |



Work waves (smaller-first, mergeable):



1\. \*\*W1 — "stop lying":\*\* P0-1, P0-2, P0-4, P2-1, P2-2, P2-4. Highest user-trust impact; pure copy + state-binding work, no new components.

2\. \*\*W2 — "design system":\*\* P1-1, P1-6, P1-7, P1-8, P1-9, P1-10. Extract reusable styles + per-tab UserControls. Every later edit becomes half as long.

3\. \*\*W3 — "ship the missing UX":\*\* P0-3, P0-5, P1-2, P1-3, P1-4. Brings the window into spec-completeness — model downloads, log size + clear, history search + purge, mic disclosure.



