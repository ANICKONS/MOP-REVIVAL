// MOP - REVIVAL
// Copyright(C) 2026 ANICKON. Licensed under GPLv3 (see Loc.cs).
//
// English strings. This table is the reference: every key must exist here.
// Settings texts: labels in sentence case, one short plain sentence under a setting (no internals).
// Colors are added by MopMenu (MoprColors), not stored here.

using System.Collections.Generic;

namespace MOP.Localization
{
    internal static class StringsEn
    {
        internal static readonly Dictionary<string, string> Table = new Dictionary<string, string>
        {
            // === Settings: information ===
            // === Mod list (MSCLoader) ===
            { "mod.description", "The ultimate My Summer Car optimization project, rebuilt for 2026." },

            { "menu.header.info", "Information" },
            { "menu.info.by", "by {0}" },
            { "menu.info.based", "Based on the original MOP by Athlon - GPLv3" },
            { "menu.btn.github", "GitHub" },
            { "menu.btn.original_mop", "Original MOP" },

            // === Settings: language ===
            { "menu.header.language", "Language / Язык" },

            // === Settings: performance ===
            { "menu.header.performance", "Performance" },
            { "menu.sub.mode", "Performance mode" },
            { "menu.mode.performance", "Performance" },
            { "menu.mode.balanced", "Balanced" },
            { "menu.mode.quality", "Quality" },
            { "menu.mode.safe", "Safe" },
            { "menu.mode_hint", "Performance - the most FPS, far-away buildings appear later.\n" +
                                "Balanced - recommended.\n" +
                                "Quality - you see farther, FPS is lower.\n" +
                                "Safe - if something broke: MOP only switches off buildings and scenery." },
            { "menu.restart_note", "Items and vehicles already follow the new mode. Buildings, rooms of the house and simplified " +
                                   "item models follow after you load your save again." },
            { "menu.restart_note_safe", "Safe mode is switched on or off when you quit to the main menu." },
            { "menu.sub.distance", "Load distance" },
            { "menu.active_distance", "Radius" },
            { "menu.dist.0", "Small ×0.5" },
            { "menu.dist.1", "Smaller ×0.75" },
            { "menu.dist.2", "Normal ×1" },
            { "menu.dist.3", "Larger ×2" },
            { "menu.dist.4", "Large ×4" },
            { "menu.distance_hint", "MOP switches off everything beyond this radius and back on as you come closer. " +
                                    "Smaller radius - more FPS." },
            { "menu.sub.protection", "Save safety" },
            { "menu.restart_guard", "Don't let parts and items reset" },
            { "menu.restart_guard_hint", "When MOP switches an object back on, the game sometimes brings its paint, fluids or doors " +
                                         "back to your last save. This prevents it." },

            // === Settings: render optimization ===
            { "menu.header.render", "Render optimization" },
            { "menu.render_enable", "Turn on render optimization" },
            { "menu.render_hint", "The main switch of this section: when off, nothing below works (and it is hidden). " +
                                  "Only the picture changes - never the game or your save." },
            { "menu.render_quality", "Strictness" },
            { "menu.render_quality_hint", "How early small and distant things stop being drawn and casting shadows." },
            { "menu.cull.0", "More FPS" },
            { "menu.cull.1", "Balanced" },
            { "menu.cull.2", "Best picture" },
            { "menu.sub.objects", "Distant objects" },
            { "menu.render_culling", "Don't draw tiny distant objects" },
            { "menu.render_culling_hint", "Anything far away that would be a couple of pixels on screen is not drawn." },
            { "menu.sub.shadows", "Shadows" },
            { "menu.render_shadow_lod", "No shadows from small distant things" },
            { "menu.render_shadow_lod_hint", "Small things far away cast no shadow - you could not see it there anyway." },
            { "menu.render_lamps", "No shadows from distant lamps" },
            { "menu.render_lamps_hint", "Distant lamps still light up, just without shadows." },
            { "menu.sub.satsuma", "Satsuma" },
            { "menu.render_merge", "Faster drawing of the parked Satsuma" },
            { "menu.render_merge_hint", "While nobody drives the car, its parts are drawn as one piece. Looks the same, " +
                                        "more FPS near the car." },

            // === Settings: game engine ===
            { "menu.header.engine", "Game engine" },
            { "menu.engine_fastpath", "Speed up the game's scripts" },
            { "menu.engine_hint", "Skips script work that surely changes nothing. About +10% FPS, the game behaves the same." },
            { "menu.sub.diagnostics", "For bug hunting" },
            { "menu.engine_verify", "Check mode" },
            { "menu.engine_verify_hint", "No speed-up: MOP only compares its result with the game's and records any difference " +
                                         "(console: mopr engine). Turn on if you were asked to." },

            // === Settings: experimental ===
            { "menu.header.experimental", "Experimental" },
            { "menu.experimental_hint", "All off by default. More FPS, but in rare cases they can behave slightly differently from the game " +
                                        "without mods. Turn them on one at a time." },
            { "menu.exp_mouse_ray", "Fewer mouse checks at the Satsuma" },
            { "menu.exp_mouse_ray_hint", "The car's parts share one check per frame for whether the mouse is on them. " +
                                         "A part may respond one frame later." },
            { "menu.exp_engine_bay", "Don't draw the engine from the driver's seat" },
            { "menu.exp_engine_bay_hint", "While you sit in the car with the hood closed, the engine under it is not drawn. Mirrors still show it." },
            { "menu.exp_mirrors", "Mirrors at most 60 times a second" },
            { "menu.exp_mirrors_hint", "Above 60 FPS the car's mirrors are not refreshed every frame." },

            // === Settings: graphics ===
            { "menu.header.graphics", "Graphics" },
            { "menu.sub.framerate", "Frame rate" },
            { "menu.limit_fps", "Limit FPS" },
            { "menu.fps_limit", "Limit" },
            { "menu.shadow_distance", "Shadow distance, m" },
            { "menu.shadow_distance_hint", "How far shadows are visible. The game uses 200. Less - more FPS." },
            { "menu.sub.other", "Other" },
            { "menu.dynamic_draw", "Dynamic draw distance" },
            { "menu.dynamic_draw_hint", "Indoors the distant world is not drawn; outside your own draw distance is back." },
            { "menu.run_background", "Keep running in the background" },
            { "menu.run_background_hint", "If off, the game pauses when you switch to another window." },
            { "menu.resolution", "Screen resolution" },

            // === Settings: other ===
            { "menu.header.other", "Other" },
            { "menu.destroy_bottles", "Remove empty containers" },
            { "menu.destroy_bottles_hint", "Empty bottles, glasses, cups and packs vanish right after drinking instead of piling up." },
            { "menu.disable_empty", "Hide emptied items" },
            { "menu.disable_empty_hint", "Emptied packs and cans are hidden while you are not holding them." },
            { "menu.skidmarks", "No tire marks" },
            { "menu.skidmarks_hint", "Tire marks are not drawn. On long drives they slowly eat memory." },

            // === Settings: mod compatibility (rules) ===
            { "menu.header.rules", "Mod compatibility" },
            { "menu.rules_hint", "Rules tell MOP which objects of other mods to leave alone. For known mods they are built " +
                                 "into MOP and switch on by themselves. Your own rules are JSON files in the rules folder." },
            { "menu.rules_status_none", "The rules have not been read yet." },
            { "menu.rules_builtin", "Built-in rules for: {0}" },
            { "menu.rules_builtin_none", "Your mods need no built-in rules." },
            { "menu.rules_sep", " - " },
            { "menu.rules_kind_user", "your file" },
            { "menu.rules_kind_legacy", "old format" },
            { "menu.rules_count_one", "{0} rule" },
            { "menu.rules_count_many", "{0} rules" },
            { "menu.rules_problems_one", "{0} problem" },
            { "menu.rules_problems_many", "{0} problems" },
            { "menu.rules_unreadable", "can't be read" },
            { "menu.rules_off", "rules are off" },
            { "menu.rules_mod_missing", "mod {0} is not installed or is disabled" },
            { "menu.rules_more", "...and {0} more" },
            { "menu.rules_see_console", "What is wrong: console command mopr rules" },
            { "menu.rules_next_load", "The changes apply at the next game load." },
            { "menu.btn.reload_rules", "Read rules again" },
            { "menu.btn.open_rules", "Open rules folder" },
            { "menu.btn.new_rules", "Create your own rule file" },

            // === Settings: logs and bug reports ===
            { "menu.header.logging", "Logs and bug reports" },
            { "menu.logging_hint", "Found a bug? Press Report bug in MSCLoader - the logs are attached for you." },
            { "menu.btn.open_logs", "Open logs folder" },
            { "menu.btn.report", "Create mod report" },
            { "menu.btn.delete_logs", "Delete all logs" },

            // === Settings: changelog / about ===
            { "menu.header.changelog", "Changelog" },
            { "menu.header.about", "About" },
            { "menu.footer.test_build", " (test build)" },
            { "menu.footer.original", "Original MOP: Copyright © Konrad Figura 2019-2023" },
            { "menu.footer.revival", "Revival: ANICKON 2026. Licensed under GPLv3." },

            // === Common buttons ===
            { "common.ok", "OK" },
            { "common.yes", "YES" },
            { "common.no", "NO" },

            // === Message boxes ===
            { "msg.title", "MOP - REVIVAL" },
            { "msg.title.warning", "MOP - REVIVAL - Warning" },
            { "msg.welcome", "Welcome to MOP - REVIVAL {0}!\n\n" +
                             "This version is rebuilt on top of the original, battle-tested MOP code " +
                             "and adds a render optimizer and PlayMaker engine optimizations.\n\n" +
                             "<b>BACKUP YOUR SAVE FILES!</b>" },
            { "msg.btn.changelog", "CHANGELOG" },
            { "msg.changelog_title", "MOP - REVIVAL {0} - Changelog" },
            { "msg.restarts", "The game has been reloaded over {0} times, which may cause issues with the game's physics." },
            { "msg.conflict", "MOP - REVIVAL does not work with <color=yellow>{0}</color>. Please disable that mod first." },
            { "msg.rules_next_load", "The rules have been read again. They apply at the next game load." },
            { "msg.open_link", "This will open the following link:\n<color=yellow>{0}</color>\n\nAre you sure you want to continue?" },
            { "msg.safe_mode_on_later", "Safe Mode will be enabled after you quit to the Main Menu." },
            { "msg.safe_mode_off_later", "Safe Mode will be disabled after you quit to the Main Menu." },
            { "msg.msceditor", "The engine block is saved as bolted, but some of its bolts are not tightened at all. " +
                               "The game does not do that by itself; it usually comes from editing the save (MSCEditor or similar).\n\n" +
                               "MOP skips its check whether the Satsuma loaded completely for this load. You can keep playing." },
            { "msg.satsuma_not_loaded", "Satsuma has not been fully loaded by the game!\n\n" +
                                        "Consider restarting the game in order to avoid any issues." },
            { "msg.no_logs_folder", "Logs folder doesn't exist." },
            { "msg.no_output_log", "File \"output_log.txt\" doesn't exist." },
            { "msg.no_logs", "No logs exist." },
            { "msg.delete_logs_one", "Are you sure you want to delete <color=yellow>{0}</color> log?" },
            { "msg.delete_logs_many", "Are you sure you want to delete <color=yellow>{0}</color> logs?" },

            // === Save integrity verification ===
            { "save.title", "MOP - REVIVAL - Save Integrity Verification" },
            { "save.check_title", "MOP - REVIVAL - Save Integrity Check" },
            { "save.found_one", "MOP has found <color=yellow>{0}</color> problem with your save:" },
            { "save.found_many", "MOP has found <color=yellow>{0}</color> problems with your save:" },
            { "save.more", "...and <color=yellow>{0}</color> more." },
            { "save.ask_one", "Do you want MOP to try to fix this problem?" },
            { "save.ask_many", "Do you want MOP to try to fix these problems?" },
            { "save.btn.report", "SHOW REPORT" },
            { "save.fixed_one", "<color=yellow>{0}</color> issue has been fixed." },
            { "save.fixed_many", "<color=yellow>{0}</color> issues have been fixed." },
            { "save.failed_one", "<color=red>{0}</color> issue couldn't be fixed." },
            { "save.failed_many", "<color=red>{0}</color> issues couldn't be fixed." },
            { "save.backup_note", "Your save file has been backed up into <color=yellow>defaultES2File.txt.mopbackup</color> and <color=yellow>items.txt.mopbackup</color>." },
            { "save.bug.bucket_seats", "Bucket seats: only one of the pair is purchased" },
            { "save.bug.flatbed", "Flatbed trailer is saved as attached, but it is far from the tractor" },
            { "save.bug.fuel_line", "Fuel Line tightness is not a correct value.\n\nValue is <b>{0}</b>.\n<b>{1}</b> is expected" },

            // === Loading screen ===
            { "load.waiting_mods", "Waiting for other mods" },
            { "load.checking_satsuma", "Checking the Satsuma" },
            { "load.restarting", "Reloading the scene to finish loading the Satsuma" },
            { "load.optimizing", "Optimizing the world" },
            { "load.final_touches", "Final touches" },
            { "load.easter", "HAVE A NICE DAY :)" },
            { "load.tip_prefix", "TIP: " },
            { "load.tip.0", "MOP turns far-away objects off and turns them back on before you get close." },
            { "load.tip.1", "Saving is safe: MOP turns every object back on before the game writes your save." },
            { "load.tip.2", "Found a bug? Use the Report bug button in MSCLoader: it attaches the logs for you." },
            { "load.tip.3", "Type \"mopr help\" in the console to see what MOP can do." },
            { "load.tip.4", "Having issues? The Safe mode only toggles world objects and leaves items and vehicles alone." },
            { "load.tip.5", "PlayMaker engine optimizations skip only work that provably changes nothing." },
            { "load.tip.6", "Your own rules for mods: \"Create your own rule file\" in the Mod compatibility section of the settings." },
            { "load.tip.7", "The language can be switched in the MOP - REVIVAL settings." },
            { "load.tip.8", "Tire marks slowly eat memory. You can turn them off in the Other section of the settings." },
        };

        /// <summary>Number of "load.tip.N" entries.</summary>
        internal const int TipCount = 9;
    }
}
