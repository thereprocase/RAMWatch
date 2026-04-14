# RAMWatch — UI/UX Design Guide

**Companion to:** RAMWatch Architecture Document v1.0
**Target platform:** Windows 10/11, WPF, 2026
**Design philosophy:** A system utility that earns its place in your taskbar by being quiet, dense, honest, and fast.

---

## 1. The Problem with Modern Windows UI

Windows Settings is a cautionary tale. It replaced dense, functional Control Panel pages with enormous whitespace, hidden options, half-finished migrations, and interactions that require three clicks where one sufficed. Users don't hate "modern" — they hate losing information density and control in exchange for visual padding.

The enthusiast tool space (HWiNFO, ZenTimings, Process Hacker, Sysinternals) gets this right by accident: developers who prioritize function over fashion produce interfaces that power users love. But these tools are often visually rough — misaligned elements, inconsistent spacing, Win32 controls from 2008.

RAMWatch occupies the middle ground: **the information density of HWiNFO with the visual polish of a well-designed dark-mode application.** Not a Settings panel. Not a 2008 dialog box. A tool that looks like it was built by someone who cares about both data and typography.

---

## 2. Core Principles

### 2.1 Density Over Padding

Every pixel of whitespace must justify its existence. If removing padding doesn't hurt readability, remove it. The user has a 1440p or 4K monitor — don't waste it on margins that could hold data.

**Rules:**
- Default window size should show all critical information without scrolling
- No content card with less than 3 lines of actual data in it
- No full-width single-value rows (e.g., a 500px-wide row showing just "Status: OK")
- Group related values horizontally when they fit. Timings are a natural grid, not a vertical list
- Padding exists to separate groups, not to fill space

**Anti-patterns:**
- ❌ A 300px-tall hero panel showing one status indicator
- ❌ Accordion panels that hide data behind clicks
- ❌ Scrollable content where a denser layout would eliminate the need to scroll
- ✅ A compact header bar with status dot + text + uptime, 32px tall
- ✅ A timing grid where 20 values are visible simultaneously

### 2.2 Numbers Are Sacred

This is a monitoring tool. Numbers are the primary content. Treat them with the same care a financial application treats currency.

**Rules:**
- **Monospace font for all numeric values.** Always. No exceptions. Proportional fonts cause columns to misalign when values change (e.g., latency jumping from 9.8ns to 10.2ns shifts everything). Use `Consolas`, `Cascadia Mono`, or `IBM Plex Mono`.
- **Right-align numbers** in table columns. Left-aligned numbers are unreadable when comparing across rows.
- **Fixed-width columns** for numeric data. Never auto-size a column based on the widest value — it causes the entire table to reflow when data updates.
- **Consistent decimal places.** If VSOC shows 1.0875V, VDIMM shows 1.4000V — not 1.4V. Trailing zeros communicate precision.
- **Units adjacent, not distant.** "1800 MHz" not a column header that says "MHz" 400px away from the number.
- **No thousand separators in small numbers.** "8000%" not "8,000%". "53200 MB/s" not "53,200 MB/s". These are technical values, not financial figures.
- **Proportional font for labels and prose.** Use `Segoe UI` or `IBM Plex Sans` for everything that isn't a number. The contrast between proportional labels and monospace values creates natural visual hierarchy.

### 2.3 Color Means Something

Color is information, not decoration. Every color choice must answer: "What does this tell the user that they didn't already know?"

**Semantic palette (dark theme):**

| Color | Hex | Meaning | Usage |
|---|---|---|---|
| Green | `#00C853` | Healthy / passing / clean | Status dots, passing test results, LKG indicator |
| Red | `#FF1744` | Error / failure / attention needed | Error counts > 0, failed tests, WHEA events |
| Amber | `#FFB300` | Warning / in-progress / drift | SFC running, drift detected, notice-tier events |
| White/Light gray | `#E0E0E0` | Primary text / normal values | Labels, timing values, body text |
| Medium gray | `#9E9E9E` | Secondary text / metadata | Timestamps, units, column headers |
| Dim gray | `#616161` | Disabled / unavailable | Grayed-out features when driver not loaded |
| Blue | `#448AFF` | Interactive / link / manual designation | Clickable elements, "manual" timing indicators |
| Background dark | `#1A1A2E` | Primary background | Main window |
| Background medium | `#16213E` | Panel background | Data panels, table backgrounds |
| Background accent | `#0F3460` | Header/active elements | Tab bar, column headers, active selections |

**Rules:**
- Never use color as the sole differentiator. Always pair with shape, text, or position. (Accessibility: ~8% of men are color-deficient.)
- Status dots use the semantic colors above. They're always accompanied by text ("CLEAN — 0 errors" not just a green dot).
- Timing values are white/light gray by default. Only colorize them in comparison views (green = improved, red = regressed vs. baseline).
- Background colors are muted navy/dark blue, not pure black. Pure black (#000) against bright text creates harsh contrast that causes eye strain in dark rooms.
- No gradients. No glows. No shadows on individual elements. Flat, clean, professional.

### 2.4 Layout Is a Grid, Not a Canvas

System utilities show structured data. Structured data belongs in a grid. Resist the urge to make every panel a unique artisanal layout.

**Rules:**
- Use a consistent 8px base grid. All padding, margins, and spacing are multiples of 8 (8, 16, 24, 32).
- Panels align to columns. If the window is divided into two columns of timing data, every panel respects those columns.
- Vertical rhythm matters. Row heights within data tables are consistent (28–32px per row). Don't vary row height based on content.
- Labels are left-aligned. Values are right-aligned (numbers) or left-aligned (text). Never center-align data.
- Group separators are subtle — a 1px line with 16px vertical padding, or a section header in a slightly different background shade. Not a thick border or a card with drop shadow.

### 2.5 Motion Is a Last Resort

Animation in a monitoring tool is almost always wrong. The user opens RAMWatch to check a number, not to watch a transition.

**Rules:**
- No fade-in on window open. Appear instantly.
- No slide animations on tab switches. Content swaps immediately.
- No animated progress bars for background tasks. Use a static spinner icon or a text status ("SFC: Running... 2–10 min").
- No bounce or wiggle effects on notifications.
- The only acceptable animation: a brief (150ms) color flash on a value that just changed (e.g., error count incrementing). This draws the eye without being distracting. Flash, don't animate.
- System tray icon state changes (green → red) are instantaneous. No transition.

---

## 3. Window Behavior

### 3.1 Default Size and Position

- Default size: 520×640px (fits comfortably on 1080p without feeling oversized on 1440p+)
- Position: remember last position and size across sessions
- Minimum size: 440×400px (below this, data truncates unacceptably)
- Maximum size: unconstrained (panels reflow or grow to fill)
- Multi-monitor: remember which monitor the window was on. If that monitor is no longer connected, fall back to primary monitor.

### 3.2 Resize Behavior

- Tables grow vertically to show more rows (preferred)
- Timing grid can reflow from 3-column to 2-column if window is narrowed
- Panels never overlap or stack when resized
- Scrollbar appears only when content genuinely exceeds the viewport, never as a permanent fixture
- Horizontal scrollbar: never. If content doesn't fit horizontally, the layout is wrong.

### 3.3 Always-on-Top (Optional)

- Off by default
- Toggle via Settings and via right-click on title bar
- When enabled, use `Topmost = true` but respect fullscreen applications — don't render over exclusive fullscreen games
- Subtle visual indicator when topmost is active (thin accent-colored line at top of window, or a small pin icon in the title bar)

### 3.4 Taskbar and Alt-Tab Presence

- Always visible in taskbar and Alt-Tab when the window is open
- When minimized to tray: remove from taskbar and Alt-Tab entirely. The tray icon is sufficient.
- Never show both a taskbar button and a tray icon for the same window state. It's confusing.

### 3.5 Close Button Behavior

- Close button (`X`) minimizes to tray if tray mode is enabled. This matches the convention for persistent monitoring tools (Discord, Steam, etc.).
- If tray mode is disabled, close button actually closes the GUI (service keeps running).
- First time the user clicks X with tray mode enabled, show a one-time tooltip: "RAMWatch is still running in the system tray. Right-click the tray icon to quit." With a "Don't show again" checkbox.

---

## 4. System Tray

### 4.1 Icon Design

- 16×16 and 32×32 ICO with transparency
- Simple, recognizable silhouette — a memory stick or a waveform, not a detailed illustration
- Three icon states:
  - **Green variant:** normal operation, no errors
  - **Red variant:** errors detected since boot
  - **Gray variant:** service not connected / degraded mode
- The color difference must be distinguishable in both Windows 10 and Windows 11 system trays, which have different background colors and icon sizes
- Test at 100%, 125%, 150%, and 200% DPI scaling

### 4.2 Tray Tooltip

Hover tooltip shows one-line status:
- "RAMWatch — Clean, 0 errors (Up 12h 14m)"
- "RAMWatch — 3 WHEA errors since boot"
- "RAMWatch — Service not connected"

No multi-line tooltips. No timing data in the tooltip. Keep it glanceable.

### 4.3 Tray Context Menu

Right-click menu:

```
Show RAMWatch
─────────────
● Clean — 0 errors          (status line, not clickable)
─────────────
Save Snapshot...
Copy Digest
─────────────
Quit
```

- Menu items use system font and follow Windows context menu conventions
- "Quit" closes the GUI only. Service continues running.
- No "Exit" and "Quit" as separate items. One word, one action, unambiguous.
- Status line is informational only (grayed-out or styled differently)

### 4.4 Toast Notifications

- Use native Windows `ToastNotificationManager` — not a custom popup window
- Notifications appear in Action Center and follow the user's notification preferences (Do Not Disturb, Focus Assist)
- Notification content: one line title ("RAMWatch — WHEA Error"), one line body ("Corrected hardware error on Memory at 14:23")
- No notification sounds. System monitoring tools should not beep.
- Click notification → opens RAMWatch window to the relevant tab
- Cooldown per source (default 5 minutes) to prevent notification storms

---

## 5. Tab Design

### 5.1 Tab Bar

- Horizontal tab bar below the status header
- Tab labels: short, one word where possible. "Monitor", "Timings", "Timeline", "Snapshots", "Settings"
- Active tab: accent background color (`#0F3460`) with light text
- Inactive tabs: transparent background with medium gray text
- No icons in tabs. Text only. Icons add visual noise without improving navigation.
- No close buttons on tabs. All tabs are permanent.
- Tab order reflects usage frequency: Monitor first (most checked), Settings last (rarely changed)

### 5.2 Tab Content Padding

- 16px padding inside each tab's content area
- Consistent across all tabs — don't let one tab feel "roomier" than another
- Content starts immediately below the tab bar. No sub-header or breadcrumb.

### 5.3 Tab Keyboard Navigation

- `Ctrl+Tab` / `Ctrl+Shift+Tab` cycles tabs (standard Windows convention)
- `Ctrl+1` through `Ctrl+5` jumps to specific tabs
- Tab content is focusable and keyboard-navigable (arrow keys in tables, Tab between controls)

---

## 6. Component Patterns

### 6.1 Data Tables

The primary UI element. Most of what the user sees is tabular data.

**Style:**
- No visible cell borders. Use alternating row backgrounds for scanability (e.g., `#16213E` / `#1A2744`)
- Column headers: accent background (`#0F3460`), bold text, 32px height
- Data rows: 28px height (tight but readable at 12px font size)
- Selected row: subtle highlight (`#1E3A5F`), not a garish blue bar
- Hover: very subtle brightness shift on the row, or none at all. Hover effects on static data are distracting.

**Interaction:**
- Click a row to select it (for clipboard copy or context menu)
- Double-click a row in the error table to expand the event detail
- Right-click for context menu: "Copy", "Copy Row", "Show in Event Viewer"
- No inline editing. This is a read-only monitoring tool.

**Scrolling:**
- Sticky column headers (don't scroll with content)
- Smooth scrolling, no row snapping
- If the table has fewer rows than the viewport, don't show a scrollbar

### 6.2 Status Header

Always visible at the top of the window, regardless of which tab is active.

```
● CLEAN — 0 errors since boot
Boot: 04/14 09:01  |  Up: 12h 14m  |  Updated: 14:23:05
```

- Height: 56px (two lines of text)
- Status dot: 14px, semantic color
- Status text: 14px, bold, same color as dot
- Metadata line: 11px, medium gray
- Background: same as window background (no separate panel). Separated from tab content by a 1px line.

### 6.3 Timing Grid

The Timings tab is the most information-dense panel. It must display 40+ values without scrolling on a 1080p monitor.

**Layout strategy: labeled value pairs in a multi-column grid**

```
┌─ Primaries ────────────────────────────────────┐
│ CL     16●   tRCDRD  22●   tRCDWR  22●        │
│ tRP    22●   tRAS    42    tRC     62          │
│ CWL    16●   GDM     On    Cmd     1T          │
└────────────────────────────────────────────────┘
```

- Label in medium gray, value in white, monospace
- `●` after value = manual designation (blue dot, 6px). Absence = auto.
- 3-column layout for primaries (label+value = one cell, 3 cells per row)
- 2-column or 3-column for secondaries depending on label width
- Section headers ("Primaries", "Secondaries", "tRFC", "Voltages") are left-aligned, medium gray, 11px uppercase
- Sections separated by 8px vertical gap, no borders

**Nanosecond display:** For tRFC, show both clocks and nanoseconds: "577 (320ns)". The ns value is calculated at display time from the clock value and MCLK. Display in a slightly smaller font size or dimmer color to avoid visual competition with the clock value.

**Drift indicator:** If DriftDetector has flagged a timing, show a small amber triangle (▲) next to the value. Tooltip on hover: "tRRDL trained to 12 on boot 04/15 08:30 (expected 11)".

### 6.4 Buttons

- Height: 28px
- Corner radius: 2px (barely rounded — not the pill-shaped buttons of modern Windows)
- Background: `#0F3460`
- Text: `#E0E0E0`, 12px
- Hover: lighten background 10%
- Active/pressed: darken background 10%
- Disabled: background `#2A2A4A`, text `#616161`
- No button icons unless the button has no text label (rare)
- Button groups are right-aligned at the bottom of their panel
- Destructive actions (clear log, remove snapshot) use a distinct color (amber, not red — red is for errors in this palette)

### 6.5 Settings Controls

Settings tab uses standard form controls, not the Windows 11 "toggle switch on a full-width row" pattern.

**Toggle switches:** For boolean settings. Compact, inline with label. Not full-width rows.

**Numeric inputs:** Spinner controls (up/down arrows) for values like refresh interval. Show the unit inline ("60 seconds").

**Text inputs:** Standard text box for paths (mirror directory, git repo name). Include a "Browse..." button for file paths.

**Dropdowns:** Standard combobox for enumerated choices (theme, provider). No custom dropdown styling.

**Grouping:** Settings are grouped by category with section headers matching the settings.json structure: General, Monitoring, Logging, Notifications, Git, Sharing, Advanced. Each group is a collapsible section (collapsed = just the header, expanded = all controls). Collapse state persists.

### 6.6 Dialogs

Dialogs are rare. Most interactions happen inline (tab content, tray menu). When a dialog is necessary:

- Modal dialogs only (block the parent window). No modeless floating windows.
- Maximum two buttons: primary action ("Save", "Run") and cancel ("Cancel"). Not "Yes/No/Maybe/Cancel".
- No "Are you sure?" confirmations for non-destructive actions. Only confirm for actions that delete data.
- Dialog size: as small as possible. Auto-size to content.
- Center on parent window, not on screen.

### 6.7 Comparison View (Snapshots Tab)

Side-by-side timing comparison between two snapshots or between current and LKG.

**Layout:** Two columns, each showing a full timing snapshot. Changed values are highlighted:
- Green text: value improved (lower is better for timings, lower is better for latency)
- Red text: value regressed
- White text: unchanged
- A delta column between the two snapshots showing the numeric difference (e.g., "-2" for CL going from 18 to 16)

**Dropdown selectors** at the top of each column: "Current", "LKG", or any named snapshot. Default comparison: Current vs. LKG.

---

## 7. Typography

### 7.1 Font Stack

| Usage | Font | Weight | Size | Fallback |
|---|---|---|---|---|
| Numbers, timing values, code | Cascadia Mono | Regular (400) | 12px | Consolas, monospace |
| Body text, labels | Segoe UI | Regular (400) | 12px | system-ui, sans-serif |
| Section headers | Segoe UI | SemiBold (600) | 11px uppercase | system-ui |
| Status text | Segoe UI | Bold (700) | 14px | system-ui |
| Metadata, timestamps | Segoe UI | Regular (400) | 11px | system-ui |
| Tab labels | Segoe UI | SemiBold (600) | 12px | system-ui |

**Why Cascadia Mono over Consolas:** Cascadia Mono ships with Windows Terminal and modern Windows installs. It has better glyph coverage, more consistent metrics across sizes, and ligature support (disabled for our use). Consolas is the fallback for older systems.

**Why not IBM Plex Mono:** It's excellent but not pre-installed on Windows. Bundling a font adds to the distribution size and requires font embedding configuration. Use it only if the EXE is already shipping a custom theme resource bundle.

### 7.2 Sizing and DPI

- All font sizes specified in device-independent pixels (WPF default)
- Test at 100%, 125%, 150%, 175%, and 200% display scaling
- At 200% scaling on a 4K monitor, the default window should still show all Monitor tab content without scrolling
- Minimum readable body text: 11px at 100% scaling (anything smaller is unreadable on 1080p)

---

## 8. Accessibility

### 8.1 Keyboard Navigation

Full keyboard navigation is non-negotiable. Every interactive element must be reachable via Tab key, and every action must be triggerable via Enter or Space.

- Tab order follows visual layout (left-to-right, top-to-bottom)
- Focus indicator: 2px accent-colored (`#448AFF`) border around the focused element. Must be clearly visible against the dark background.
- Data tables: arrow keys navigate rows, Enter expands detail, Escape deselects
- Tabs: Ctrl+Tab cycles, Ctrl+number jumps
- Global shortcuts: Ctrl+C copies current state to clipboard, Ctrl+S saves snapshot

### 8.2 Screen Reader Support

- All interactive elements have `AutomationProperties.Name` set
- Data tables use proper DataGrid automation peers (WPF provides these by default if the control is a standard DataGrid)
- Status changes announced via `AutomationProperties.LiveSetting = "Polite"` — screen reader announces "3 WHEA errors since boot" when the count changes, without interrupting current focus
- Icons have alt text. Status dots have text equivalents.

### 8.3 High Contrast Mode

- Detect `SystemParameters.HighContrast` and switch to a high-contrast theme
- In high contrast: use system colors (`SystemColors.WindowBrush`, `SystemColors.WindowTextBrush`) instead of the custom dark palette
- Data tables must remain readable with system-selected high contrast colors
- This is easy to get wrong with custom themes. Test it explicitly.

### 8.4 Color Deficiency

- Never rely on color alone to convey status. Every colored element has a text or shape companion:
  - Green dot + "CLEAN" text
  - Red dot + "ERRORS — 3" text
  - Amber triangle ▲ for drift warnings
  - Blue dot ● for manual designation
- In comparison views, changed values use color AND a delta number (+2, -4). Don't rely on "green means better."

---

## 9. Performance and Responsiveness

### 9.1 Render Performance

- UI updates on a 60-second timer (matching the service refresh interval). No continuous rendering.
- DataGrid binding updates via `ObservableCollection` or `INotifyPropertyChanged` — WPF re-renders only changed cells, not the entire table.
- Never block the UI thread for data fetching. Pipe reads are async. File operations are async. If something takes >16ms, it's off the UI thread.
- Window opens in <200ms from click to fully rendered content. No splash screen. No loading spinner. The data is already in the pipe — just display it.

### 9.2 Perceived Performance

- Show stale data immediately, update when fresh data arrives. Don't show a blank window while waiting for the first pipe message.
- If the service is slow to respond (>2 seconds), show the last known state with a subtle "Connecting..." indicator — not a blank screen.
- Tab switches are instant. Content for all tabs is pre-bound in memory (the state message contains everything). Tabs don't fetch data on activation.

---

## 10. Dos and Don'ts Summary

### Do

- Show all critical data without scrolling at default window size
- Use monospace for numbers, proportional for text
- Right-align numeric columns
- Use color semantically (green = good, red = bad, amber = warning)
- Pair every color with a text or shape indicator
- Remember window position, size, and tab selection across sessions
- Minimize to tray with a colored status icon
- Support full keyboard navigation
- Open in <200ms
- Use the 8px grid for all spacing

### Don't

- Don't use cards with rounded corners and drop shadows for data display
- Don't animate tab switches, panel expansions, or data updates
- Don't use hamburger menus, drawers, or side panels
- Don't use full-width toggle rows with 80% whitespace
- Don't use progress circles or percentage donuts for simple status
- Don't use custom-styled scrollbars
- Don't use a splash screen or loading animation
- Don't use tooltips as the primary way to access important information
- Don't put data behind hover states
- Don't make the user scroll to see the most important information
- Don't use notification sounds
- Don't use more than 5 colors in the semantic palette
- Don't center-align data
- Don't auto-hide the tab bar or status header
- Don't display a "What's New" dialog on update

---

## 11. Reference Applications

Tools to study for what they get right (and wrong):

| Tool | Gets Right | Gets Wrong |
|---|---|---|
| **HWiNFO** | Extreme data density, sensor tree, custom layouts | Visually dated, inconsistent spacing, overwhelming for new users |
| **ZenTimings** | Perfect scope — does one thing well. Clean grid. | Slightly cramped, no dark theme option, no save/export |
| **Process Hacker** | Fast, keyboard-driven, dense tables, dark theme | Complex for non-developers, too many context menu items |
| **OCCT** | Clean dark theme, clear test controls, good charts | Some wasted space in monitoring view |
| **Sysinternals (Process Monitor)** | Filter system, toolbar density, log clarity | Win32 aesthetic, no DPI scaling, font rendering |
| **GPU-Z** | Compact window, sensor tab, one-screen-fits-all | Fixed size, no resize, tiny at high DPI |
| **Windows Terminal** | Settings UI in JSON + GUI, theming system, font rendering | Over-designed settings UI (ironic) |
| **Discord** | Tray behavior, minimize-to-tray UX, notification model | Electron bloat, memory usage, update nags |

RAMWatch aims for: the density of HWiNFO, the scope discipline of ZenTimings, the dark theme of OCCT, the keyboard navigation of Process Hacker, the tray behavior of Discord, and the typography of Windows Terminal. None of the bloat.

---

## 12. Theme System

### 12.1 Dark Theme (Default and Only Theme at MVP)

The dark theme is not a preference — it's the default. Monitoring tools are used in dim rooms, at night, while stress testing. Light themes cause eye strain in these conditions.

A light theme is a Phase 5 "nice to have." Do not build the theme system to support it from day one. Build the dark theme, ship it, and if users request light mode, add it later with a proper theme resource dictionary swap.

### 12.2 Color Tokens

Define all colors as resource dictionary entries, not inline hex values. This makes future theming possible without a refactor.

```xml
<ResourceDictionary>
    <!-- Backgrounds -->
    <Color x:Key="BgPrimary">#1A1A2E</Color>
    <Color x:Key="BgPanel">#16213E</Color>
    <Color x:Key="BgAccent">#0F3460</Color>
    <Color x:Key="BgHover">#1E3A5F</Color>
    <Color x:Key="BgAlternateRow">#1A2744</Color>
    
    <!-- Text -->
    <Color x:Key="TextPrimary">#E0E0E0</Color>
    <Color x:Key="TextSecondary">#9E9E9E</Color>
    <Color x:Key="TextDisabled">#616161</Color>
    
    <!-- Semantic -->
    <Color x:Key="StatusGreen">#00C853</Color>
    <Color x:Key="StatusRed">#FF1744</Color>
    <Color x:Key="StatusAmber">#FFB300</Color>
    <Color x:Key="AccentBlue">#448AFF</Color>
    
    <!-- Borders -->
    <Color x:Key="BorderSubtle">#2A2A4A</Color>
    <Color x:Key="BorderFocus">#448AFF</Color>
</ResourceDictionary>
```

### 12.3 Control Templates

Override default WPF control templates for:
- DataGrid (remove default blue selection, use custom row styles)
- Button (flat style, no chrome)
- TabControl (horizontal tab strip, no border around content)
- ScrollViewer (thin scrollbar, dark track)
- ComboBox (dark dropdown, no border flash on focus)
- ToggleButton/Switch (compact, inline)
- TextBox (dark background, subtle border)
- ToolTip (dark background, no shadow, 200ms delay)

Do NOT override templates for:
- ContextMenu (use system context menu styling — it blends with the OS)
- Window chrome (use default window border and title bar. Custom chrome breaks snap layouts, accessibility, and user expectations)

### 12.4 Window Chrome

**Use the default Windows title bar.** Do not create a custom title bar. Reasons:
- Snap layouts (Win+Z) only work with standard chrome
- Accessibility tools expect standard title bar buttons
- Dragging, double-click-to-maximize, and aero shake work automatically
- Custom chrome is the #1 source of DPI scaling bugs

The title bar will be light-colored on Windows 10 and dark on Windows 11 (if the app declares dark mode preference via `DwmSetWindowAttribute`). This is fine. Don't fight it.

---

## 13. Design Review Checklist

Before shipping any UI change, verify:

- [ ] All numeric values use monospace font
- [ ] All numeric columns are right-aligned
- [ ] No horizontal scrollbar at default window size
- [ ] All critical data visible without scrolling at 1080p
- [ ] Colors pass WCAG AA contrast ratio (4.5:1 for text, 3:1 for large text)
- [ ] Every color-coded element has a text/shape companion
- [ ] Tab key reaches every interactive element in logical order
- [ ] Focus indicator is visible on dark background
- [ ] Window remembers position and size across sessions
- [ ] Tray icon is distinguishable at 16×16 in both Win10 and Win11 tray
- [ ] No animation longer than 150ms
- [ ] No blocking operations on UI thread
- [ ] Window opens in <200ms
- [ ] Tested at 100%, 150%, and 200% DPI scaling
- [ ] High contrast mode doesn't break the layout
