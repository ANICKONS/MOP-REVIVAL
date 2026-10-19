# MOP - REVIVAL

[![](https://img.shields.io/github/downloads/ANICKONS/MOP-REVIVAL/total?style=for-the-badge)](https://github.com/ANICKONS/MOP-REVIVAL/releases)
[![](https://img.shields.io/github/v/release/ANICKONS/MOP-REVIVAL?include_prereleases&style=for-the-badge)](https://github.com/ANICKONS/MOP-REVIVAL/releases)
[![](https://img.shields.io/github/license/ANICKONS/MOP-REVIVAL?style=for-the-badge)](LICENSE.md)

<img align="right" src="docs/images/icon.png" alt="MOP - REVIVAL icon" width="128" />

MOP - REVIVAL is the continuation of **MOP (Modern Optimization Plugin)**, the performance mod for My Summer Car.
More FPS without any setup: install the mod and it's ready to go.

It is built on the original MOP 3.10.4 by Athlon, ported to the current game and MSCLoader 1.4, with the old bugs
fixed and new optimizations on top. Every change is measured in the game before it ships.

Interface in English and Russian.

**Results may vary!**

## Features

- **The original MOP engine.** Places, vehicles, items and world objects far away from you are switched off.
- **Restart guard.** Game logic that would re-read your last save when MOP switches it back on (paint, fluids,
  bolts, doors) is paused instead of reset.
- **Render optimizer.** Small far-away objects and their shadows are not drawn. Visual only, nothing is moved.
- **Satsuma mesh merger.** The parked car is drawn as 33 meshes instead of ~440 parts: +5–8% FPS with the car in view.
- **PlayMaker engine optimizations.** Idle game logic is skipped only when nothing would change: about +10% FPS.
- **Fixed bugs of the original MOP.** Walkers that never appeared, a save check that undid your work, food spoiling
  three times slower, the kilju bucket spilling on load and more. See the [changelog](CHANGELOG.md).
- **Built-in compatibility rules** for 43 mods. No rule servers, no internet access.

## Requirements

- **Latest LEGAL COPY** of My Summer Car
- [MSCLoader](https://github.com/piotrulos/MSCModLoader/releases) 1.4.2 or newer

## Installation

1. Download `MOPR.dll` from [Releases](https://github.com/ANICKONS/MOP-REVIVAL/releases).
2. Put it into your Mods folder (e.g. `C:\Steam\steamapps\common\My Summer Car\Mods`).
3. Remove `MOP.dll` of the original MOP, if you have it. The two can't run together.
4. Profit!

Coming from MOPR 4.x? `Mods\References\MOPRNET.dll` is no longer needed and can be deleted.

## Mod compatibility

Rules for these mods are built in and apply while the mod is installed:

<details>
<summary>43 mods</summary>

ActualMop, AthlonsTweaksPack, BetterMSC, BodyPillow, BottleRecycling, CallYourCousin, CDPlayer, ColorfulGauges3,
dazyWoodDash, DonnerTech_ECU_Mod, DrivableBus, DriveableSvoboda, Expanded_Winter_Features, FishingMod, FURY, g_Box,
GAZ24, GifuPump, GlassesBox, GT_Interior, HayosikoColorfulGauges, JetSky, JONNEZ_Basket, KekmetAddons,
KekmetBeaconV2, LiquorStore, LoppeHouse, Manual_Ferndale, MSCStill, OffroadHayosiko, Partition, Police_Ferndale,
Projector_Headlights, PsychovanzRentApartment, Repairable_Ruscko, Roofless, Satsuma_LX, SatsumaDigidash,
SatsumaTurboCharger, SecondFerndale, SuperchargerForSatsuma, TangerinePickup, VHSPlayer

</details>

Your own rules go into `Mods\Config\Mod Settings\MOPR\Rules` (JSON). The settings page has a button that creates a
commented template. Rule files of the original MOP (`.mopconfig`, `Custom.txt`) still work.

Not compatible: the original MOP, KruFPS, ImproveFPS, OptimizeMSC and DisableAll. Use one optimization mod at a time.

## Questions and bugs

Found a bug? Use the **Report bug** button of MOP - REVIVAL in MSCLoader's mod list (the logs are attached for you),
or open an [issue](https://github.com/ANICKONS/MOP-REVIVAL/issues). Console commands: `mopr help`.

## Building from source

Windows, PowerShell and any .NET SDK 6 or newer. Visual Studio is not needed.

```powershell
.\scripts\build.ps1                 # Release build -> dist\MOPR.dll
.\scripts\build.ps1 -Mode deploy    # build and copy into the game's Mods folder
```

With the game installed, the mod is compiled against the real game assemblies. Without it (CI),
`.\scripts\check-stubs.ps1` compiles against compile-only stubs; such builds are for checking the code only.
Design notes and the test plan are in [docs/DESIGN.ru.md](docs/DESIGN.ru.md) (Russian).

<details>
<summary>Repository layout</summary>

```
src/MOPR/src/        original MOP engine (ported) + Next/ (restart guard, render optimizer, engine lab,
                     diagnostics), Localization/ (EN/RU), MopMenu.cs (settings page)
src/MOPR/Rules/      built-in rules (JSON, embedded into the DLL)
src/MOPR.TestBot/    development-only automation mod (never ship it)
tests/scenarios/     TestBot scenarios, run with scripts\test-run.ps1
tests/rules/         rule fixtures (old .mopconfig files, user files for test runs)
refs/                stub overlay + scripts (generated/ and cache/ are not committed)
tools/RefStubGen/    reference stub generator
tools/RulesCheck/    offline rule checks (scripts\rules-check.ps1)
scripts/             build, deploy, stub check, in-game test runner
docs/                design notes and in-game test plan (Russian)
```

</details>

## License

This program is distributed under the GNU General Public License v3, like the original MOP. Feel free to use its
source code as long as you mention the authors and state the changes. Changes against MOP 3.10.4 are marked with
`MOP - REVIVAL` comments in the source. For more, see the [LICENSE](LICENSE.md) file.

## Credits

- **ANICKON** - MOP - REVIVAL
- [Konrad Figura (Athlon)](https://github.com/Athlon007) - the original [MOP](https://github.com/Athlon007/MOP);
  most of the game logic in this repository is his
- [Krutonium](https://github.com/Krutonium) - original [KruFPS](https://github.com/Krutonium/KruFPS) creator, and the
  KruFPS contributors:
  - RedJohn260
  - EPS
- Est_Nbmstr - for the name of the original MOP
- FESilencer - for helping Athlon figure out how PlayMaker actions work

### Testers of the original MOP

- DoritoMan
- CatAtTheDesert
- Slavic_Dude001
- DJ Ötzi

Contains AI-assisted code (disclosed as requested by MSCLoader).
