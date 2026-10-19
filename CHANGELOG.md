# Changelog

## 5.1.0 "Engine" (unreleased)

### Added
- **PlayMaker engine optimizations** (`Next/Engine/`, Harmony). Work is skipped only when MOP can prove from
  the live state that it would change nothing; otherwise the original code runs:
  - idle `PlayMakerFSM.Update`/`LateUpdate` (no active per-frame work, no pending events);
  - `GetProperty`/`SetProperty` through compiled delegates instead of reflection;
  - redundant per-frame writes (`cInput` axis inversion save, `SetGameVolume`).
  Measured with the in-game TestBot (interleaved A/B): about +10% FPS at the spawn and at home, about 60% less
  managed garbage. Setting: *PlayMaker engine optimizations* (on by default).
- **Verify mode** for the engine optimizations: the original code runs anyway and every difference is
  reported (`mopr engine`). About 25 million checks, 0 differences.
- **English and Russian interface** (`src/Localization/`). The language dropdown in the mod settings switches
  the whole page live; the first start follows the system language. Console messages and logs stay English.
- **New loading screen** without an asset bundle: animated logo, the real loading step (waiting for mods →
  checking the Satsuma → optimizing the world → final touches), an eased progress bar, tips, a short fade-out.
- **Satsuma mesh merger** (`Next/MeshMerger.cs`, `Next/VehicleMeshMerge.cs`). Why the FPS drops near the
  assembled car: it is CPU-bound, not GPU-bound (resolution changes nothing). Unity 5.0 issues one draw call per
  renderer, the car has ~550 of them, and it is drawn even behind a wall. While nobody drives the car, about 440
  opaque single-material parts are drawn as 33 meshes merged by material (~104k vertices, built in ~8 ms spread
  over 2–3 frames). Every frame in LateUpdate, before any camera renders (mirror and reflection cameras
  included), each merged part is checked: hidden, re-enabled, material swapped, or moved relative to the body (local position,
  rotation and scale of the part and of every object up to the car root, against the pose at merge time,
  within 0.1 mm / 0.01° - below a pixel, and independent of where the car is in the world). Any change brings
  the original renderers back in that same frame. Writes of the same value (some FSMs set positions every
  frame) are recognised and ignored. The merged meshes follow the body, so a car with physics on (player
  within 5 m) stays merged. Parts that move are skipped for 15 s, then 30 s, then for the session. Transparent,
  multi-material, mirrored, deformable panels and items are never merged. Not while the player sits in the car:
  measured from the driver's seat, merging saves nothing there and the check costs 0.15–0.2 ms. Visual only: no
  `SetActive`, colliders, physics or PlayMaker.
  Measured (TestBot, merged vs original in alternating 1 s blocks, medians, net of the per-frame check):
  −0.43…−0.54 ms per frame at 6.5 m in front and at 8 m with the car behind the house wall (+5–8.5% FPS, five
  runs); at 3.5 m with physics on between 0 and −0.63 ms (the check costs 0.3–0.36 ms there). Screenshots merged
  vs original: the car is pixel-identical; hiding the hood while merged is drawn correctly in the very next
  frame. Setting: *Merge the parked Satsuma* (on). Report: `mopr render`.
- **Experimental tier** (5.1, every option off by default, in its own settings section). Measured and, where
  possible, verified, but MOP cannot prove for every frame that the game behaves exactly as without them:
  - *Shared mouse ray* (`Next/Engine/SharedMouseRay.cs`). Every Satsuma part asks every frame whether the mouse
    is over it; PlayMaker caches the ray for one layer mask only and the game interleaves about a dozen, so next
    to the car 300 queries cast 107 rays per frame (each with two `Camera.main` lookups). One ray per mask per
    frame answers every query exactly as a fresh ray of that length would; the cache is dropped when the camera
    moves and an entry is recast when its collider was disabled. About 10 rays per frame instead of 107.
    A/B: −0.76 ms next to the car, −0.33…−0.57 ms in the driver's seat, −0.12…−0.89 ms while driving.
    Verify mode (Engine Lab verify setting): 1.28 million answers compared with the original, also with the
    camera turning every frame, 0 different. Not provable because a collider moving into the ray between two
    FSM updates of the same frame would be seen one frame later.
  - *Car mirrors at most 60 FPS* (`Next/MirrorRate.cs`). The rear-view and side mirror cameras render the world
    again every frame (0.4 ms in Peräjärvi, 1.2 ms in the home garage, measured). Above 60 FPS a mirror render is
    skipped when the mirror was rendered less than 1/60 s ago; the mirror keeps its picture (culling mask
    nothing and "don't clear" only during that camera's own render). A/B: −0.51 ms seated, −0.55 ms driving.
  - *Don't draw the engine from the driver's seat* (`Next/EngineBayCulling.cs`) - the idea of
    SatsumaFpsOptimization, but conditional and per camera: only while the player sits in the car and the hood
    is attached, drawn and closed, and only for the player's own view, during its render: the engine objects
    (no colliders, no shadow casters) are moved to a layer that camera does not draw and get their layers back
    right after it. Mirrors, reflection probes and every other camera still see the engine, and no game code
    sees a changed layer. (Switching ~140 renderers off and on every frame costs more than it saves in Unity
    5.0, +0.5 ms measured; keeping them off breaks the game's reflection probe camera.) A/B: 0 ms seated,
    −1.1 ms driving; switching the engine renderers off for reference gave −0.1…−0.5 ms - a small, noisy gain.

- **New rule system** (`src/Rules/`). Rules tell MOP which objects of other mods to leave alone.
  - Format: JSON (`Mod Settings/MOPR/Rules/*.json`, comments allowed, keys case-insensitive), one meaning per
    key: `exclude` (MOP never touches it), `keepActive` (never switched off; may still be hidden or frozen),
    `keepActiveIn` (children that stay on inside a place or vehicle), `manage` (objects / renderers / items /
    vehicles / vehiclePhysics of a mod MOP should switch), `reparent`, `noLod`, `sectors` (now with
    `drawDistance`), `satsuma` (`keepRenderers` / `exclude`), `keepEmptyBottles`, `skipFixes`, and the whole-game
    `allVehiclesPhysicsOnly` / `noLods` for the user's own files. The old `ignore` meant three of these
    depending on what the object turned out to be.
  - The 43 bundled rule files are built into the DLL as JSON and apply while their mod is installed and enabled.
    They are no longer copied into the settings folder, so an update always brings the current version and a
    user's edit can never block it. Converted automatically; `scripts/rules-check.ps1` proves that every JSON
    file says exactly what its old `.mopconfig` said (43 identical, 118 rules) and runs 44 parser tests.
    In game (TestBot `rules-ab`, new op `mopstate`: every world object, item, vehicle, place, sector and Satsuma
    list MOP manages): the old DLL with all 43 old files and the new DLL with the built-in JSON give the same
    666 lines, except the 3 vehicles named by two mods with different rules (FERNDALE, GIFU, RUSCKO: BodyPillow
    says keepActive, Manual_Ferndale / GifuPump / Repairable_Ruscko say exclude - now exclude wins instead of
    the file that came first) and duplicate names in the YARD list (same 151 names). Without any rules 82 of
    those lines differ, so the comparison does see rules. Reading every rule file takes 1-10 ms.
  - A wrong key or value is reported with its line and skipped, the rest of the file applies (old: one bad
    line dropped the rest of the file, with a wrong line number). Only a JSON syntax error disables a file, and
    the message says where. Unknown keys get a "did you mean" suggestion.
  - Old `.mopconfig` files and `Custom.txt` still work, read line by line with the same meaning; an old
    `<ModID>.mopconfig` replaces the built-in rules of that mod, as before. `mopr rules convert <file>` turns one
    into JSON. Copies MOPR 5.0 installed and nobody edited are deleted once (MD5 from `MopData.json`).
  - Rules are read in the menu and again at every game load: an edited file or a mod switched on or off in the
    menu applies at the next load. Reading during a game only shows the result ("applies at the next load").
  - One dictionary lookup per object instead of a new list of all rules per object. When two files disagree
    about an object, the rule that lets MOP do less wins (exclude > keepActive); before, the first file in the
    folder won.
  - Settings page: what is active (built-in for which mods, your files, problems) and the buttons *Read rules
    again*, *Open rules folder*, *Create your own rule file* (a commented template). *Delete unused rules* and
    its setting are gone: nothing is copied any more, rules of a removed mod simply stop applying.
  - Console: `mopr rules [all]` (active, not used and why, problems, named objects MOP did not meet),
    `mopr rules why <object>`, `mopr rules export <ModID>` (a built-in file to edit), `mopr rules convert`.
    Bug reports list the rules and include the user's own files.
  - Removed: the rule server code (offline since 2023), `min_ver` (ignored), `ignore_mod_vehicles` (never had
    an effect), the main-menu "MOP: loaded N rule files" text.
  - Fixed on the way: `dont_destroy_empty_bottles` (now `keepEmptyBottles`) was read but never used - with
    *Remove empty bottles* on, a bottle-recycling mod lost its bottles; rule sectors were read with the system's
    number format (where the decimal mark is a comma, "1078.3" failed and the sector was made at 0,0,0); two
    files managing the same vehicle created it
    twice; "Delete unused rules" changed a list while iterating it; rule files are read as UTF-8 (a file
    without a BOM was read in the system code page).
- The console command is **`mopr`** (the mod ID). The old `mop` is gone.
- The mod icon of MOPR 4.0.x is back (the figure with the mop). The short description in MSCLoader's mod list
  follows the interface language, also while the list is open.

### Changed
- The settings page lives in `MopMenu.cs` and is styled like MOPR 4.x: colored *Information* and *Language*
  headers (the experimental section has its own), "▍ GROUP" subheaders, labels in normal case, a short grey
  explanation under a setting, paired buttons (`Common/SettingsUiPatcher.cs`), the language dropdown without a
  label. All texts are rewritten in plain language (EN and RU). Setting IDs are unchanged, saved values carry over.
- Settings page reworked for clarity. *Render optimization* starts with its main switch and says so under it;
  switching it off hides every setting of the section and really turns all of it off, the Satsuma merger
  included (before, the merger kept working with the switch off). The FPS limit slider shows only while
  *Limit FPS* is on. The load distance slider is labelled *Radius* (MSCLoader draws the label and the value on
  one line, the long label ran into "Normal ×1"), and the empty row of the restart note is gone until the note
  has something to say. Every RU/EN text is shorter and plainer.
- Console: the tag is `[MOPR]` (orange, bold) and MOPR lines are colored (`Common/MoprColors.cs`, the 4.x
  palette): beige text, cyan versions and names, green for done/on, yellow for attention, red for failures, grey
  for off. Warnings and errors keep MSCLoader's "MOPR Warning:" / "MOPR Error:" prefix (no doubled tag). When the
  game is ready, a short summary shows what MOP manages and which optimizations are on. Message box titles say
  "MOP - REVIVAL".
- While the mesh merger is on, the render optimizer leaves the Satsuma's renderers alone (one owner per renderer).
  Switching the merger in game hands the car over at once (a rescan, ~33 ms once), not after the next load.
- Settings changed in game apply at once. Audited every setting: all of them already did except *Dynamic draw
  distance* switched off (the draw distance stayed at the last indoor value until the next load; now the
  player's own value is back right away: 300 → 3000 m in the sauna, measured) and the performance modes. A mode
  changes the load distances of items and vehicles, skidmarks and traffic at once; what MOP builds for a mode at
  load (detached buildings, indoor sectors, simplified item models) follows at the next load, and the note under
  the modes now appears only while the selected mode differs from the loaded one and says exactly that.
- The PreSave hook count in the log is exact, and a failing save point is reported by name.

### Fixed
- Physics-only items (bucket, fish trap, pike, fuel tank, rule-ignored items) were moved back to an old
  position on every save when the game had turned their gravity off: MOP restored a freeze it had not made.
- One save point without the expected state aborted the hooks of all save points after it.
- The save integrity backup copied `defaultES2File.txt` over `items.txt.mopbackup`.
- *Delete all logs* threw when the logs folder did not exist.
- Satsuma interior lamp visible through the roof (a vanilla model bug, also with MOPR's merger off). Measured
  with the TestBot (`lamp-roof`, `lamp-fix`: vertex-against-roof check and screenshots from a free camera): the
  lamp housing (part of `cowl_parts`) sticks out of the roof by up to 6.8 mm and the switch by 1.1 mm. At load
  (`Next/RoofLampFix.cs`), in the car's own frame, the 31 housing vertices above the roof are moved down to
  1.5 mm under it, in a copy of the mesh (the asset and colliders are untouched), and the lamp is lowered by the
  2.6 mm its switch needs in both positions. After the fix: 0 vertices outside the roof, nothing visible from
  above (also merged); seen from inside the car the lamp looks the same. Skipped with `satsuma_ignore`.
- Safe mode chosen in game did not apply after quitting to the main menu, as its message says, but only after
  restarting the game (the next load saw the mismatch again and kept the old mode). It now applies in the menu.
- MOP's loops allocated garbage every tick (`Enum.HasFlag` boxing, `GameObject.name` reads).
- Satsuma mesh merger (pre-release builds): opening a door could make the body behind the front doors vanish,
  and after it came back the tyres and front seat backs (and the suspension) stayed invisible until the game was
  restarted. Cause, measured with the TestBot (`render-toggle`, `satsuma-doors`): the merge was validated in
  `Camera.onPreCull`, and Unity 5.0 loses a renderer switched on while a camera renders - `enabled` reads true,
  but it is never drawn again. Parts that moved (the car rocks on its springs when a door is opened) were
  handed back that way and stayed lost; the rest only came back with the next merge. The check now runs in
  LateUpdate, before any camera renders. Before the fix: 72 of 783 renderers on but never drawn after four
  door cycles (all four tyres among them); after it: 0, and with the merger switched off every renderer
  matches the baseline (state, mesh, material, visibility).

- **Bugs of the original MOP 3.10.4** (collected from Nexus, Reddit and Steam reports, then confirmed in the code;
  checked in game with the TestBot, `tests/scenarios/batch1.json`, against the build before these fixes):
  - The walkers of the December 2023 game update (Kale, Julli, Kristian, Unto, Rauno, Alpo) never appeared.
    Their spawner `HUMANS/Randomizer` sits at (0, 0, 0), so MOP only enabled it within 150 m of that point -
    at home, where MOP switches all of `HUMANS` off. MOP leaves it alone now; each walker checks its own
    distance to the player.
  - A `keepActive` rule on an object without a renderer of its own (`COTTAGE`, `HUMANS`, ... - BetterMSC,
    LoppeHouse and others have such rules) threw "Couldn't find the renderer", and every world object after it
    in the same list was skipped (`WORLD_OBJECTS_1/2_INITIALIZAITON_FAIL`). There is nothing to hide on such an
    object, so MOP leaves it alone; every world object (and every human) is now added in its own try, and a
    rule-excluded object no longer breaks the list either.
  - `FsmManager.ResetAll()` reset nothing (`GetFields()` without flags does not see private fields). After
    quitting to the menu and loading again, MOP read the FSM variables of the destroyed scene: frozen old
    values for the hood, the trailer, the Hayosiko key, the combine, the Suski phone call and the clock.
  - The bucket lid came off and the kilju spilled on load: MOP switched the lid's `Removal` FSM off and on
    again, and a re-enabled PlayMaker FSM restarts from its first state ("lid removed"). The lid's FSM is the
    game's again (as in MOPR 4.0.3b).
  - Save check: the bolt "check" against `MopSave.json` is removed. MOP wrote the rear bumper, halfshaft and
    battery terminal bolts at every load and, if that file was still there at the next start (no save through
    MOP, another save slot, a crash), "fixed" the save back to those load-time values - the rear bumper
    silently. That undid the player's own work and left cars that would not start or drive.
  - Save check: the item counter "fix" is removed. The game keeps the tags of eaten and thrown-away items, so
    raising the count to the number of tags made the game spawn those items again.
  - "MSCEditor has been used" no longer fires for a car that simply has few bolts tightened (15% or less);
    with it MOP also skipped its "Satsuma loaded completely" check. Only the state the game can't produce
    (engine block saved as bolted with untightened bolts) is reported, worded as what it is.
  - Door handles of the house are set up one by one: a door without an FSM (the fridge of BetterMSC) stopped
    the rest (`FRIDGE_DOORHANDLE_ERROR`). The garage doors and the sauna are checked the same way.
  - The combine check (`JOBS/Farm/Job`) had no null check: while it could not be found, every MOP tick threw
    (`VEHICLE_TOGGLE_ERROR_9`).
  - Removed the "battery popping out" fix: its arguments were passed in swapped order, so it never ran; made
    to run, its ASSEMBLE event resets the battery bolts.
  - Item toggling no longer swallows every error silently (two `catch { }`): errors go to `output_log.txt`,
    once per item and method. The two known causes (a loose battery, an empty package lying in the world) are
    handled on purpose.
  - Food that MOP had switched off spoiled three times slower than in the game. The game's "Use" FSM takes
    `SpoilingRate` (`SpoilingRateFridge` in the fridge) off once per real second; MOP made up for the time off
    with scaled level time "* 0.33". Measured (`tests/scenarios/spoil.json`): milk in the fridge loses 0.0010 per
    second while active; after about 190 s switched off MOP took off 0.063 instead of about 0.19. Now real time,
    factor 1. (Food handled by the FridgeAPI mod, e.g. sausages with its `SpoilableFood`, keeps its own value:
    MOP's correction is overwritten there, and such food does not spoil while switched off - a FridgeAPI matter.)
- Back in the main menu without a save through MOP (death, MOP's own scene restart), MOP stayed "active", and
  during the next load `Hypervisor.Update` ran before MOP was set up: about 200 `NullReferenceException`s in
  the log (seen with the TestBot's new `reloadgame` step, also in the build before these fixes).
- **GifuPump rule**: only the truck's `MESH` stays on while MOP switches the parked GIFU off (`keepActiveIn`).
  The old rule line was misread by MOP 3.10, which excluded the whole truck.

- TestBot: `reloadgame` (back to the main menu without saving, then Continue; the run survives MSCLoader's reset
  of all mods in the menu), `fsmcache` (MOP's cached FSM variables: live or left over from an earlier scene),
  probes `@self` and `@parent`.

### Checked
- **Gifu septic pump** (reported against MOPR 4.0.x): reproduced the job in game with the TestBot
  (`tests/scenarios/gifu-pump.json`). With MOPR 5.1 the well drains at the rate the game's FSM predicts
  (rpm / 90 / 500 per second) with the player at 3 m, at 9 m (MOP has frozen the truck physics), at 40 m
  and with the engine optimizations off. Not affected.

## 5.0.0 "Reborn" (unreleased)

A new foundation. MOPR 4.x was a from-scratch rewrite and introduced many regressions; 5.0 is built
on the original MOP 3.10.4 source instead, so all of its game-specific fixes are back.

### Added
- **Restart guard.** FSMs inside objects MOP disables whose PlayMaker restart path contains Easy Save 2
  `Load*` actions are paused (`RestartOnEnable = false`) for MOP's disable/enable window only, then
  restored. Setting: *Automatic restart guard* (applies from the next load). Report: `mop guard`.
- **Render optimizer** (visual only, fully restorable): smart per-layer small-object culling, shadow LOD
  for small objects, distant lamp shadows. Settings in *Render optimizer*. Report: `mop render`.
- **Bundled rule database** (43 rule files), installed for loaded mods; user-edited files are never
  overwritten. A `Custom.txt` from the original MOP settings folder is copied once.
- **`satsuma_ignore`** rule flag: MOP leaves the Satsuma entirely to another mod.
- `mop audit` — lists FSMs in managed objects that could reset when re-enabled.
- Save barrier check: after the pre-save re-enable, MOP logs anything it manages that is still disabled.

### Changed
- Ported to the MSCLoader 1.4 API; Mod Loader Pro support removed. Mod ID is `MOPR`.
- Heavy initialization waits until every mod finished MSCLoader's phase 3 (MOP 3.10 used `SecondPassOnLoad`).
- Rule files accept `#` comments, leading whitespace and a UTF-8 BOM.
- Bug reports go through MSCLoader's *Report bug* button.

### Fixed
- Crash on MSCLoader 1.3.6+: the bug reporter needed `Ionic.Zip.dll`, which MSCLoader deletes.
- The same error was written to the log on every 0.7 s loop tick (de-duplication never worked).
- The "Satsuma not fully loaded" scene restart never ran on MSCLoader 1.4 (renamed loading canvas).
- `mop reload all` threw a NullReferenceException for rule files of mods that are not installed.
- Special rule flags (e.g. `satsuma_ignore`) applied even when their mod was disabled.
