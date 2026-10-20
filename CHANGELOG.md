# Changelog

## 5.1.0 "Engine" (2026-10-20)

The first public release of the 5.x line; it includes everything of 5.0 below.

### Added
- **PlayMaker engine optimizations** (`Next/Engine/`, Harmony). Work is skipped only when MOP can prove from
  the live state that it would change nothing; otherwise the original code runs:
  - idle `PlayMakerFSM.Update`/`LateUpdate` (no active per-frame work, no pending events);
  - *evaluated actions*: a state whose only per-frame actions are `MousePickEvent`, `BoolTest` or
    `FloatCompare` (for example the Satsuma part removal FSMs; about a quarter of all FSM updates at home) is
    evaluated exactly as PlayMaker 1.6 would; if none of them would send an event this frame, the FSM update is
    skipped like an idle one. The same `ActionHelpers.IsMouseOver` calls are made in the same order (the pure
    tests are checked first; all picks of one state must share ray distance and layer mask, so when the original
    has to run after all, its repeated calls are cache hits), and `Fsm.RaycastHitInfo` is written as the action
    writes it. Only a null event counts as "sends nothing". Verify mode also watches `Fsm.Event` and the hit
    info: 23.5 million checks at home and while driving, 0 differences. Measured (A/B against the blocking path,
    1 s blocks): yard +4.5% FPS (204.9 vs 195.9; second run 206.3 vs 197.4), living room +6.1% (280 vs 264),
    driving 6.50 vs 6.87 ms median frame;
  - *shared mouse ray* (`Next/Engine/SharedMouseRay.cs`; experimental in the first 5.1 builds, now on by default,
    setting *Fewer mouse raycasts* under the game engine switch). Every Satsuma part asks every frame whether
    the mouse is over it; PlayMaker caches the ray for one layer mask only and the game interleaves about a
    dozen, so next to the car about 90 picks per frame cast a new ray each (with two `Camera.main` lookups).
    Only `ActionHelpers.DoMousePick` is replaced: it stores what `Physics.Raycast` would store, taken from one
    ray per mask per frame, and sets PlayMaker's three cache fields itself, so `MouseOver`, `IsMouseOver`,
    `MousePick` and `Fsm.RaycastHitInfo` keep PlayMaker's own semantics. The first version answered the queries
    instead and differed in two ways its verify mode did not see: `MousePick` returned a trimmed hit where
    PlayMaker returns the cached hit of a longer ray, and on a miss it stored an empty hit while Unity 5.0 only
    clears the collider and keeps point, normal, distance and face of the previous hit (measured: every miss
    differed). A ray is recast when `Camera.main` is another camera, the camera moved, turned or zoomed, or the
    hit collider was disabled, deactivated, moved or turned. About 11 rays per 100 picks.
    Verify mode now runs the original `DoMousePick` too and compares every field of the stored hit:
    1.84 million picks at home (yard, living room, next to the car with the camera turning) and in the car
    (seated, driving), 0 different. A/B (shared vs original, 1 s blocks): living room 236.7 vs 222.3 FPS
    (+6.5%), yard 202.7 vs 190.3 (+6.5%), next to the car 186.2 vs 166.6 (+11.8%), driving 6.49 vs 7.00 ms median
    frame; a second run gave +5…8%. Not provable: another collider moving INTO the ray between two FSM updates
    of the same frame would be seen one frame later (never seen in the verify runs);
  - *IK fast path* (`Next/Engine/IkFastPath.cs`): the game's CCD IK (`SimpleIKSolver`: Satsuma suspension
    arms and shocks, car and floor jacks, tractor and flatbed cylinders) iterates in LateUpdate until the end
    joint is within ~3.5 cm of its target, at most 21 times; most chains can never get that close, so all 19
    solvers ran 21 iterations every frame (0.24 ms at home). A solve is a deterministic function of the local
    poses (and parents) of every transform from its joints and target up to the scene root and of its own
    fields; when a solve on such inputs left every joint bitwise as it was, the next solve on bitwise the same
    inputs is skipped. Anything else runs the original. The skip drops only same-value transform writes
    (`Transform.hasChanged`, which no game script reads; colliders keep their pose). Verify mode: 39,513
    skipped solves checked (yard, next to the car, car pushed, seated, driving), 0 differences. At rest 87% of
    the solves are skipped. A/B: yard +5.4% FPS (196.3 vs 186.3), living room +5.9% (271.6 vs 256.5), next to
    the car −0.26 ms median frame, driving −0.11 ms;
  - `GetProperty`/`SetProperty` through compiled delegates instead of reflection;
  - redundant per-frame writes (`cInput` axis inversion save, `SetGameVolume`).
  Measured with the in-game TestBot (interleaved A/B): about +10% FPS at the spawn and at home, about 60% less
  managed garbage. Setting: *PlayMaker engine optimizations* (on by default).
- **Verify mode** for the engine optimizations: the original code runs anyway and every difference is
  reported (`mopr engine`). About 25 million checks, 0 differences.
- **English, Russian and Polish** (`src/Localization/`: `Strings.*.cs` for the interface, `Console.*.cs` for
  the console). The language dropdown in the mod settings switches the whole page live; the first start follows
  the system language. The console follows the language too (new lines; what is already printed stays). The copy
  MOPR keeps for mod reports and bug reports is always English. Technical details inside a message (paths,
  object names, exception texts, rule parser messages) and the dumps of `mopr engine` / `mopr render` stay
  English. The mod calls itself MOPR everywhere ("MOP" only means the original mod).
- **Console format**: one line per switch ("[MOPR] Engine: Script optimization ON", "Render: Mirror optimization
  OFF"), and when the game is ready three short blocks: *Managing* (world objects, items, vehicles, places),
  *Mode* (with render optimization, script optimization, raycast optimization, Satsuma merging) and
  *Experimental*. Internal progress lines (toggled lists, sectors, bolts, renderer analysis...) are no longer
  printed; they are kept for mod reports and bug reports and shown by the new `mopr log [n]`.
- **New loading screen** without an asset bundle: animated logo, the real loading step (waiting for mods →
  checking the Satsuma → optimizing the world → final touches), an eased progress bar, tips, a short fade-out.
  Next to "MOP - REVIVAL" a large slanted orange "5" (the major version, with a lighter upper half, an extruded
  shadow and a faint glow).
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
- Settings texts: *View distance* (was *Load distance*), new mode descriptions, *Hide the engine while driving*,
  *Mirrors at 60 FPS*; the second sentence under the render main switch is gone. *Screen resolution* is the
  first setting under *Graphics → Other*.
- "Found N broken SatsumaBoltsAntiReload" is no longer printed on every load. It counted entries of the original
  MOP's bolt guard (it keeps the parts on the car "glued" while it loads and stops the bolt scripts from
  restarting) whose object the game had destroyed by the time the parts are released: its `DUMMY_` placeholders
  and copies of parts made after the load (on the test save 22 of 187: 5 placeholders, 17 copies). Harmless;
  the log line for reports now names them.
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
- Satsuma mesh merger (pre-release builds): the cabin looked lighter or darker than without the merger,
  changing with the surroundings (user report). Cause, measured with the TestBot (`merge-light`, new ops
  `lightinfo` and diff images): the merged renderers kept Unity's default *blend probes*, while the game draws
  the interior (`interior_panels`, `dashboard_console`, gauges...) with reflection probes off. Merged, those
  parts reflected the realtime probes (`RefProbeMap`, in the garage `RefProbeGarage`). Parts are now merged only
  with parts of the same reflection probe setting, and for shaders that sample probes (everything except Unity's
  legacy shaders) only when the merged mesh gets exactly the same probes and weights as each part
  (`Renderer.GetClosestReflectionProbes`, checked at build). While merged, the probes are compared every
  frame (on, active, position, box, importance) and all probe groups are checked again when the car moved by
  more than the pose tolerance; any difference hands the originals back. Parts with a probe anchor (or light
  probes in the scene) are not merged. Screenshots into the cabin, merged vs originals: before 1.61–1.77% of
  the pixels differed (noise 0.40–0.45%, the merged cabin brighter); after 0.16–0.23% (noise 0.37–0.42%).
  With the garage probe moved over the car: 71 parts stay unmerged, 0.20% (noise 0.44%); over the front only:
  5 parts, 0.11–0.17% (noise 0.31–0.49%). The gain stays: +6.8% and +6.4% FPS at 6.5 m in front (36 meshes
  instead of 33).

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
- The restart guard now also covers the Satsuma's own switching (Body, MiscParts, Dashboard, Interior, Chassis,
  CarSimulation...), the Hayosiko and the world objects: they used plain `SetActive`, so FSMs in them that read
  the save when they start read it again every time MOP switched them back on. Measured with the TestBot
  (`guard-world`: every running FSM at home that reads the save on start gets a value the save does not have,
  2 km away and back): before, the dashboard light mode, the radio tuner and the windshield went back to the save
  (a windshield broken in the session came back whole, its tax sticker back); after, 0 resets caused by MOP of 128
  marked values. The one reset left (the repair shop order) happens in the vanilla game too.
- While MOP had the Satsuma's physics off, `ForceRotation` put the car back every frame to where it was frozen,
  also when something else had moved it on purpose (the game, a teleport or garage mod, a tow): TestBot `b3-flip`,
  the car lifted 0.5 m or set down at the rally parc ferme 1.8 km away was back at its old place in the same
  frame. Only small drift (under 0.25 m and 5°) is undone now; a bigger move becomes the car's new resting place.
  The frozen pose was also saved as a world pose in one place and applied as a local one (wrong for a parented
  car). At the parc ferme (`b3-parc`, car on the gravel, away and back three times) it stays upright with no
  velocity spike when its physics come back.
- `SatsumaHingeManager` froze the game for good if a part ever had two joints of one kind: its clean-up loop
  (`while (joints.Length > 1) Destroy(joints[0])`) never ends, `Destroy` does not shrink the array.
- **GifuPump rule**: only the truck's `MESH` stays on while MOP switches the parked GIFU off (`keepActiveIn`).
  The old rule line was misread by MOP 3.10, which excluded the whole truck.

- TestBot: `reloadgame` (back to the main menu without saving, then Continue; the run survives MSCLoader's reset
  of all mods in the menu), `savegame` (a real save through the game's own save point, then the run goes on
  after the load; the runner restores the save folder afterwards), `fsmcache` (MOP's cached FSM variables: live or
  left over from an earlier scene), `partsnap` / `partcompare` (every Satsuma part, bolt stage and parts-database
  value, inactive objects included), `fsmmark` and `guardmark` / `guardcheck` (values the save does not have, to
  see resets), `repeat`, `place` in an object's own frame, probes `@self`, `@parent`, `@in:OTHER`.
- Test runs are about three times faster (a short scenario: 75 s instead of ~220 s): shorter waits in the bot and
  the scenarios, a faster snapshot comparison, and `test-run.ps1 -Scenario a.json,b.json` runs several scenarios
  in one game session.

### Checked
- **Satsuma parts falling off, bolts resetting** (the most common report against MOP 3.x): not reproduced with
  5.1. TestBot `b1-parts`, `b1-marked`, `b1-save`, against the vanilla game: 21 cycles away (40 m and 2 km) and
  back, and a real save and load - no part moved or came off (249 parts, 1709 FSMs, 123 bolt-stage lists). Every
  BoltCheck tightness got -0.5 first (a value the save does not have): all 88 kept it, also across the save.
- **Items falling out of vehicles** (`b2-cargo`): three items in the back of the Hayosiko, three times 3 km away
  and back: 0.00 m difference in the van's frame.
- **Sudden death in the Satsuma** (`b5-death`): death is the game's "Death" FSM on `DriverHeadPivot` seeing its
  ConfigurableJoint gone. MOP switches that object (player farther than 10 m), which restarts the FSM into its
  10 s wait; the joint itself stays (break force unchanged). Not reproduced without driving.
- **FPS dropping after a few minutes** (`b7-fps`): FPS at one spot at home, then 12 rounds home -> Teimo ->
  repair shop -> home (about 5 minutes, MOP switching everything each time), then the same spot again. MOPR
  194 -> 249 fps (vanilla 90 -> 106). After `GC.Collect`: GameObjects, meshes, textures, FSMs, rigidbodies and
  MonoBehaviours unchanged, MOP's lists unchanged (303 items, 59 world objects...). Materials +96 and managed
  heap +15 MB, the vanilla game the same way (+72, +12.5 MB): the game's own.
- **Gifu septic pump** (reported against MOPR 4.0.x): reproduced the job in game with the TestBot
  (`tests/scenarios/gifu-pump.json`). With MOPR 5.1 the well drains at the rate the game's FSM predicts
  (rpm / 90 / 500 per second) with the player at 3 m, at 9 m (MOP has frozen the truck physics), at 40 m
  and with the engine optimizations off. Not affected.

## 5.0.0 "Reborn" (not released on its own, part of 5.1.0)

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
