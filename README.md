# DisplayTheSpire

A Slay the Spire 2 mod that adds information HUD tooltips to the top bar.

Inspired by infomod2 from the previous game.

## Features

### Potion drop chance widget
- Persistent panel to the left of the top-bar buttons showing the current potion drop chance as a percentage.
- Hovering it opens a tooltip with cumulative drop probability for the next 2-5 fights

### Card reward odds
- Hovering the Deck button shows the actual rare/uncommon/common card drop rates for normal and elite fights

### Unknown node odds
- Hovering the Map button shows the current probabilities for each possible `?` node outcome (Event, Monster, Treasure, Shop, Elite)

### Shop prices
- Hovering the Gold button shows price ranges for cards, potions, and relics, adjusted for active price-modifying relics (Membership Card, The Courier...), plus the current card removal cost

### Cards played
- Hovering the Settings button shows cards played this turn, this combat, and across the entire run
  - A second tooltip below shows the current Act variant (eg. Underdocks, Hive...)

### Ascension details
- When Ascension is active, hovering the portrait replaces the native tip with one that includes the full description of each active ascension level

## Requirements

- None other than the base game

## Installation

1. Download the zip file containing `display_the_spire.dll`, `display_the_spire.pck`, and `mod_manifest.json`
   from the latest release.
2. Create the directory:
   ```
   <STS2 install>\mods\display_the_spire\
   ```
   Default Steam path on Windows:
   ```
   C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\
   ```
3. Copy the three files into that directory.
4. Launch Slay the Spire 2. The mod is loaded automatically if the game's mod loader is enabled.

### Files written

Data persistence file for counters:
`C:\Users\$USER\AppData\Roaming\SlayTheSpire2\display_the_spire\run_data.json`

## Building from source

### Requirements

- .NET 9 SDK
- Slay the Spire 2 installed (provides the following:)
  - `sts2.dll`
  - `GodotSharp.dll`
  - `0Harmony.dll`
- MegaDot 4.5.x console build _(headless Godot editor, needed for PCK packaging only,
  not required if you only need the DLL)_

### Build the DLL only

```sh
dotnet build display_the_spire.csproj -c Release
```

The project looks for game DLLs at the standard Steam path by default:

```
C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64
```

Override `STS2DataDir` if your game is installed elsewhere:

```sh
dotnet build display_the_spire.csproj -c Release -p:STS2DataDir="D:\Games\STS2\data_sts2_windows_x86_64"
```

### Build, package PCK, and deploy

Set the `MEGADOT` environment variable to the path of the MegaDot console executable,
then run `build.sh` (here from WSL):

```sh
export MEGADOT="/c/path/to/megadot-4.5.x/MegaDot_..._console.exe"
./build.sh
```

Pass `--no-deploy` to build and package without copying to the mods directory:

```sh
./build.sh --no-deploy
```

The deploy target defaults to the standard Steam mods path. Override with `MODS_DIR`:

```sh
export MODS_DIR="/c/custom/path/mods/display_the_spire"
./build.sh
```

### Output files

After a full build:

| File | Description |
|------|-------------|
| `bin/Release/net9.0/display_the_spire.dll` | Compiled mod assembly |
| `display_the_spire.pck` | Godot resource package  |
| `mod_manifest.json` | Mod metadata (copy alongside the two files above) |

## Project structure

```
display_the_spire/
├── DtsConst.cs                  # mod-wide string constants
├── ModInit.cs                   # Harmony bootstrap
├── Patches/                     # one file per patched system
│   ├── TopBarPotionChancePatch.cs
│   ├── TopBarDeckPatch.cs
│   ├── TopBarGoldPatch.cs
│   ├── TopBarMapPatch.cs
│   ├── TopBarSettingsPatch.cs
│   └── TopBarAscensionPatch.cs
├── UI/                          # reusable UI components
│   ├── DtsTheme.cs              # colors, font sizes, style constants
│   ├── DtsNativeTip.cs          # tooltip wrapper around the game's NHoverTipSet
│   ├── DtsModal.cs              # full-screen modal with fade
│   ├── DtsRunData.cs            # persisted per-run counters
│   └── TopBarHelper.cs          # scene-tree utilities
├── Logging/ModLog.cs            # structured log wrapper
├── build.sh                     # build + package + deploy script
├── display_the_spire.csproj
└── mod_manifest.json
```

## License

MIT

See `LICENSE`.

