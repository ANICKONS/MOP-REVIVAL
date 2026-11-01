// MOPR6 - interface texts, English (the fallback for every other language). Keys: menu.* settings page,
// msg.* message boxes. Console texts are in Console.En.cs.

using System.Collections.Generic;

namespace MOPR6.Localization
{
    internal static class StringsEn
    {
        internal static readonly Dictionary<string, string> Table = new Dictionary<string, string>
        {
            { "mod.description", "Performance mod for the vanilla game, rewritten from scratch (preview). Do not use together with MOPR 5.x." },

            { "menu.header.info", "Information" },
            { "menu.info", "MOPR6 {0}: MOP - REVIVAL rewritten for the vanilla game. Only optimizations that provably change nothing in the game." },
            { "menu.preview", "PREVIEW: a test build. Found a problem? Press \"Create a bug report\" and attach the file." },
            { "menu.conflict", "MOPR 5.x is installed and enabled. Use only one of them: MOPR6 does nothing in this session." },
            { "menu.btn.github", "GitHub" },
            { "menu.btn.report", "Create a bug report" },

            { "menu.header.language", "Language" },

            { "menu.header.main", "Main switch" },
            { "menu.enabled", "MOPR6 enabled" },
            { "menu.enabled.hint", "Off: everything MOPR6 changed is returned at once and every optimization stops. Works in game too." },

            { "menu.header.exact", "Exact optimizations" },
            { "menu.exact.hint", "These change nothing in the game: the same frames, the same save. Every setting applies at once." },
            { "menu.sys.engine.fsm", "PlayMaker scripts: skip idle frames" },
            { "menu.sys.engine.fsm.hint", "Game scripts that wait for something are not run every frame for nothing." },
            { "menu.sys.engine.property", "PlayMaker properties: direct calls" },
            { "menu.sys.engine.property.hint", "Scripts read and write component properties without slow reflection: less garbage, fewer GC freezes." },
            { "menu.sys.engine.writes", "Skip redundant writes" },
            { "menu.sys.engine.writes.hint", "The input settings and the game volume are not written again when nothing changed." },
            { "menu.sys.engine.ik", "IK: skip unchanged solves" },
            { "menu.sys.engine.ik.hint", "Suspension arms, jacks and cylinders are not recomputed while nothing they depend on moved." },
            { "menu.sys.engine.mouseray", "Shared mouse ray" },
            { "menu.sys.engine.mouseray.hint", "Car parts that ask what is under the cursor share one raycast per frame instead of dozens." },
            { "menu.verify", "Verify mode (slower)" },
            { "menu.verify.hint", "Every optimization also runs the original code and checks that it would change nothing. For checking and bug reports only: costs FPS." },

            { "menu.header.sleep", "Script sleep" },
            { "menu.sleep.hint", "Game scripts that wait for an event are taken out of the per-frame updates and woken before the event reaches them. Each one is checked against the game's own code." },
            { "menu.sys.sleep.idle", "Sleep of idle scripts" },
            { "menu.sys.sleep.idle.hint", "A script whose current state has finished and that waits only for events sleeps until an event, a lookup or its object addresses it." },

            { "menu.header.world", "World" },
            { "menu.world.hint", "Parts of the world away from the player stop being simulated, without switching anything off. These are not exact optimizations: each is checked against vanilla by the test bench, and saving, the main switch and \"mopr restore\" give everything back." },
            { "menu.sys.world.vehicles", "Parked vehicles" },
            { "menu.sys.world.vehicles.hint", "A vehicle standing with its engine off more than 15 m from the player is frozen: its body and wheel physics are not computed. It comes back when the player walks up, gets in, hooks a tow rope or another car drives by." },
            { "menu.sys.world.deactivate", "Switch off distant vehicles (test)" },
            { "menu.sys.world.deactivate.hint", "A parked vehicle more than 125 m away is switched off and a still copy of it is drawn instead, but only when nothing in it has changed for 10 s and it does not react to the player's controls. Anything the game reaches for is switched back on first. Still being tested." },

            { "menu.header.render", "Rendering" },
            { "menu.render.hint", "These change only how the scene is drawn: what is too small to see is not drawn, far shadows are skipped. Game logic, physics and saves are not affected. Only the Satsuma merge is on by default (up to 0.4 ms per frame): the others saved almost nothing in our measurements (under 0.2 ms)." },
            { "menu.sys.render.layers", "Don't draw tiny distant objects" },
            { "menu.sys.render.layers.hint", "Per layer, objects smaller than 2 pixels on screen are not drawn (never closer than 30 m)." },
            { "menu.sys.render.shadows", "No shadows of small distant objects" },
            { "menu.sys.render.shadows.hint", "A small object stops casting its shadow where the shadow would be under 6 pixels (never closer than 10 m)." },
            { "menu.sys.render.lamps", "No shadows of far lamps" },
            { "menu.sys.render.lamps.hint", "A lamp keeps its light but casts no shadows while you are farther than its range plus 35 m." },

            { "menu.sys.render.merge", "Merge the parked Satsuma" },
            { "menu.sys.render.merge.hint", "While the car stands frozen (see \"Parked vehicles\"), it is drawn as a few dozen meshes instead of hundreds of parts. Any change to a part brings the parts back in the same frame." },

            { "menu.header.experimental", "Experimental" },
            { "menu.experimental.hint", "Measured, but MOPR6 cannot prove for every frame that the picture stays the same. Off by default." },
            { "menu.sys.experimental.mirrors", "Car mirrors at most 60 FPS" },
            { "menu.sys.experimental.mirrors.hint", "Above 60 FPS a mirror skips a render and keeps its previous picture (at most 1/60 s old)." },
            { "menu.sys.experimental.enginebay", "Don't draw the engine from the driver's seat" },
            { "menu.sys.experimental.enginebay.hint", "While you sit in the car with the hood closed, your own view does not draw the engine. Mirrors and other cameras still do." },

            { "menu.header.other", "Other" },
            { "menu.check_updates", "Check for updates at start" },
            { "menu.check_updates.hint", "Once per game start, in the main menu (GitHub)." },
            { "menu.btn.import5", "Import MOPR 5.x settings" },
            { "menu.import5.hint", "Takes over what MOPR6 already has: engine optimizations, verify mode, update check, language. The 5.x files are only read." },

            { "msg.title", "MOPR6" },
            { "msg.title.update", "MOPR6 update" },
            { "msg.update_available", "A new version is available: {0}\nYou have {1}." },
            { "msg.btn.open_page", "Open the download page" },
            { "msg.btn.later", "Later" },
            { "msg.report_saved", "The bug report is saved:\n{0}\n\nIt is in English and has no personal data. Attach it to your bug report." },
            { "msg.btn.open_report", "Open the file" },
            { "common.ok", "OK" },
        };
    }
}
