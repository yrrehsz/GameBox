# GameBox

GameBox is a lightweight, Windows 11-styled start menu launcher designed to unify your PC and console gaming libraries in one clean, gamepad-friendly overlay. It brings together local executables, Steam, PlayStation Network, and Xbox Network titles with achievement badges, cover artwork, and full controller navigation.

---

## Features

- **Unified Game Library**: Aggregates games from Steam, PlayStation Network, Xbox Network, and standalone PC executables into a single grid.
- **Gamepad-First Navigation**:
  - Global `Start + Select` shortcut to open and close the launcher from anywhere.
  - 2D grid navigation using the D-Pad with automatic scrolling.
  - `A` to launch the primary game executable.
  - `X` to launch a secondary executable or mod engine (e.g. ModEngine, RPCS3, ShadPS4).
  - `Y` for game details and achievements.
  - `B` to dismiss the menu.
  - Contextual `A`, `X`, and `Y` badge indicators dynamically displayed above game cards only when a controller is connected.
- **Windows 11 Fluent Interface**:
  - Native hardware-accelerated DWM acrylic blur and rounded corners.
  - Smooth slide and fade open/close animations.
  - Dynamic cover sheen reflection that tracks cursor movement.
  - Dark mode aesthetic matching modern Windows design language.
- **Achievement & Completion Overlays**:
  - Showcases 100% completion badges: PlayStation Platinum, Xbox Mythic, and Steam 100% Ribbon.
  - Configurable overlay priority when a title is completed across multiple platforms.
  - Platform rank icons and progress percentages below game covers.
- **Filter & Search**:
  - Multi-select platform filters (All, Executables, Steam, PlayStation, Xbox, 100% Completed).
  - Instant title search and sorting by executable availability, alphabetical order, platinum count, or completion percentage.
- **Taskbar & Startup Integration**:
  - Companion shim executable (`LauncherGiochi.exe`) stored in AppData to toggle the menu with instant IPC signaling.
  - Optional silent startup with Windows to keep the launcher ready in the background.

---

## Gamepad Controls

| Button / Combination | Action |
| --- | --- |
| `Start` + `Select` (Back) | Open or close GameBox overlay |
| `D-Pad Up` / `Down` / `Left` / `Right` | Navigate game cards in the library grid |
| `A` | Launch primary game executable |
| `X` | Launch secondary / modded executable |
| `Y` | View game info and achievements |
| `B` | Close GameBox overlay |

---

## Requirements & Building

- **Operating System**: Windows 10 (version 1903 or later) or Windows 11 (recommended)
- **Runtime / SDK**: .NET 8.0 SDK (Windows Desktop workload)

### Build from source:

```powershell
# Clone the repository
git clone https://github.com/your-username/GameBox.git
cd GameBox

# Build the project
dotnet build GameStartMenu.csproj -c Release
```

The output binaries will be generated in `bin\Release\net8.0-windows\`.

---

## Configuration & Storage

User configuration, cloud cache, and companion binaries are stored in:
`%AppData%\GameStartMenu\`

- `settings.json`: API credentials, view filters, overlay priority, and startup preferences.
- `library_cache.json`: Cached game catalog, achievement statistics, and metadata.
- `covers/`: Downloaded local box art images.
- `LauncherGiochi.exe`: Lightweight taskbar shortcut compiler output.

---

## To-Do

- [ ] Complete the dedicated Game Info and Achievement breakdown detail view triggered by `Y` / Info button.
- [ ] Expand controller settings tab to support custom controller icon themes, per-device profiles, and custom button bindings.
- [ ] Add integration for additional digital storefronts (Epic Games Store, GOG Galaxy, EA App, Ubisoft Connect).
- [ ] Implement playtime tracking and recently played session history for non-store executables.
- [ ] Add localization support (multi-language UI selection).
- [ ] Provide customizable grid sizing (cover card scale and column density options).

---

## Known Issues

- **Windows 11 Taskbar Auto-Pin Limitation**: Due to security policies introduced in recent Windows 11 updates (23H2/24H2), third-party unpackaged desktop applications cannot programmatically pin shortcuts to the taskbar without user interaction. To pin the launcher, use the "Show in Explorer" option in Settings or Footer, right-click `LauncherGiochi.exe`, and select *Pin to taskbar*.
- **PSN NPSSO Token Expiration**: PlayStation Network NPSSO tokens expire periodically on Sony's authentication servers and must be refreshed manually in Settings > Sync if trophy synchronization stops updating.
- **DirectInput / Non-XInput Controllers**: Gamepad shortcuts and navigation rely on the XInput API. Generic DirectInput controllers require an XInput wrapper or emulator (such as DS4Windows or Steam Input) to be detected.
- **Transparency Fallback**: If Windows Transparency Effects are disabled in Windows Settings (`Accessibility > Visual effects`), the acrylic background will render as a solid dark backdrop.
