// MOP - REVIVAL
// Copyright(C) 2019-2023 Athlon (original Modern Optimization Plugin), 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.
//
// The mod settings page (moved out of MOP.cs in 5.1) and the language switch.
//
// Language: the dropdown (ID "language") is the single source of truth once MSCLoader loaded the saved
// values. At build time those values are not known yet, so the page is built in the language cached in
// MopData.json (first start: the system language). ModSettingsLoaded then adopts the dropdown value, which
// also keeps "Reset all settings" consistent: reset restores the dropdown default (the system language),
// MSCLoader calls ModSettingsLoaded again and the page is relabeled.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using MSCLoader;

using MOP.Common;
using MOP.Common.Enumerations;
using MOP.Localization;
using MOP.Next;
using MOP.Rules;

namespace MOP
{
    internal static class MopMenu
    {
        // Optimization
        static internal SettingsSliderInt ActiveDistance;
        static internal SettingsCheckBoxGroup ModePerformance, ModeBalanced, ModeQuality, ModeSafe;
        static internal SettingsCheckBox RestartGuardEnabled;
        static SettingsText restartNoticeText;
        static bool restartNoticeVisible;

        // Render optimizer (new in 5.0)
        static internal SettingsCheckBox RenderOptimizerEnabled, RenderLayerCulling, RenderShadowLod, RenderLightShadows;
        static internal SettingsCheckBox RenderMergeVehicles;   // 5.1
        static internal SettingsSliderInt RenderCullQuality;

        // Engine Lab (new in 5.1)
        static internal SettingsCheckBox EngineFastPath, EngineVerify;
        static internal SettingsCheckBox EngineSharedMouseRay;  // 5.1: was experimental ("experimentalMouseRay", default off)

        // Experimental (5.1, off by default)
        static internal SettingsCheckBox ExperimentalEngineBay, ExperimentalMirrorRate;

        // Graphics
        static internal SettingsSliderInt FramerateLimiter, ShadowDistance;
        static internal SettingsCheckBox KeepRunningInBackground, LimitFramerate, DynamicDrawDistance;
        static internal SettingsDropDownList GameResolution;

        // Other
        static internal SettingsCheckBox DestroyEmptyBottles, DisableEmptyItems, AlwaysDisableSkidmarks;

        // Language (new in 5.1)
        static internal SettingsDropDownList LanguageSelect;

        const string GitHubLink = "https://github.com/ANICKONS/MOP-REVIVAL";
        const string OriginalMopLink = "https://github.com/Athlon007/MOP";

        static readonly string[] ActiveDistanceKeys = { "menu.dist.0", "menu.dist.1", "menu.dist.2", "menu.dist.3", "menu.dist.4" };
        static readonly string[] CullQualityKeys = { "menu.cull.0", "menu.cull.1", "menu.cull.2" };

        /// <summary>
        /// Builds the page. Must run synchronously inside the ModSettings setup function.
        /// Layout and colors follow MOPR 4.x: colored Information and Language headers, "▍ GROUP" subheaders,
        /// sentence-case labels, a short grey explanation under a setting, paired buttons. The setting IDs are
        /// unchanged since 5.0, so saved values carry over.
        /// </summary>
        internal static void Build()
        {
            LocalizedSettings.Clear();
            SettingsUiPatcher.Clear();
            renderDependents.Clear();
            framerateDependents.Clear();
            collecting = null;

            // The saved dropdown value is not loaded yet: use the cached choice, else the system language.
            Language system = Loc.DetectSystemLanguage();
            Language cached;
            Loc.SetLanguage(Loc.TryParseCode(SafeData()?.Language, out cached) ? cached : system);

            // Information. A real header first, so MSCLoader does not insert its own "Settings" header.
            Header("menu.header.info", MoprColors.HeaderInfoBackground);
            Text(GetInfo);
            ButtonRow(Button("menu.btn.github", () => ShowDialog(GitHubLink)),
                      Button("menu.btn.original_mop", () => ShowDialog(OriginalMopLink)));

            // Language: the dropdown alone, no label (the header says it).
            Header("menu.header.language", MoprColors.HeaderLanguageBackground);
            LanguageSelect = Settings.AddDropDownList("language", "", Loc.LanguageNames, (int)system, OnLanguageSelected);
            SettingsUiPatcher.HideLabel(LanguageSelect);

            // Performance.
            Header("menu.header.performance");
            Subheader("menu.sub.mode");
            ModePerformance = Group("modePerformance", "menu.mode.performance", false, "performanceMode", MopSettings.UpdatePerformanceMode);
            ModeBalanced = Group("modeBalanced", "menu.mode.balanced", true, "performanceMode", MopSettings.UpdatePerformanceMode);
            ModeQuality = Group("modeQuality", "menu.mode.quality", false, "performanceMode", MopSettings.UpdatePerformanceMode);
            ModeSafe = Group("modeSafe", () => MoprColors.Colorize(MoprColors.Danger, Loc.T("menu.mode.safe")), false, "performanceMode", MopSettings.UpdatePerformanceMode);
            Hint("menu.mode_hint");
            // Shown only while it has something to say (no empty row otherwise).
            restartNoticeText = Text(RestartNotice);
            restartNoticeText.SetVisibility(false);
            Spacer();
            Subheader("menu.sub.distance");
            // Short label: MSCLoader draws the slider's label and its value on one line.
            ActiveDistance = Slider("activateDistanceNew", "menu.active_distance", 0, 4, 2, null, ActiveDistanceKeys);
            Hint("menu.distance_hint");
            Spacer();
            Subheader("menu.sub.protection");
            RestartGuardEnabled = CheckBox("restartGuard", "menu.restart_guard", true);
            Hint("menu.restart_guard_hint");

            // Render optimization: the main switch first; everything below it depends on it and is hidden while
            // it is off.
            Header("menu.header.render");
            RenderOptimizerEnabled = CheckBox("renderOptimizer", "menu.render_enable", true, OnRenderMainSwitch);
            Hint("menu.render_hint");
            BeginDependents(renderDependents);
            Spacer();
            RenderCullQuality = Slider("renderCullQuality", "menu.render_quality", 0, 2, 1, RenderOptimizer.OnSettingsChanged, CullQualityKeys);
            Hint("menu.render_quality_hint");
            Spacer();
            Subheader("menu.sub.objects");
            RenderLayerCulling = CheckBox("renderLayerCulling", "menu.render_culling", true, RenderOptimizer.OnSettingsChanged);
            Hint("menu.render_culling_hint");
            Spacer();
            Subheader("menu.sub.shadows");
            RenderShadowLod = CheckBox("renderShadowLod", "menu.render_shadow_lod", true, RenderOptimizer.OnSettingsChanged);
            Hint("menu.render_shadow_lod_hint");
            RenderLightShadows = CheckBox("renderLightShadows", "menu.render_lamps", true, RenderOptimizer.OnSettingsChanged);
            Hint("menu.render_lamps_hint");
            Spacer();
            Subheader("menu.sub.satsuma");
            RenderMergeVehicles = CheckBox("renderMergeVehicles", "menu.render_merge", true, VehicleMeshMerge.OnSettingsChanged);
            Hint("menu.render_merge_hint");
            EndDependents();

            // Game engine (Engine Lab).
            Header("menu.header.engine");
            EngineFastPath = CheckBox("enginePlayMakerFastPath", "menu.engine_fastpath", true, Next.Engine.EngineLab.ApplySettings);
            Hint("menu.engine_hint");
            // New ID on purpose: the experimental "experimentalMouseRay" was saved as off for everyone.
            EngineSharedMouseRay = CheckBox("engineSharedMouseRay", "menu.engine_mouse_ray", true, Next.Engine.EngineLab.ApplySettings);
            Hint("menu.engine_mouse_ray_hint");
            Spacer();
            Subheader("menu.sub.diagnostics", MoprColors.SubheaderOther);
            EngineVerify = CheckBox("enginePlayMakerVerify", "menu.engine_verify", false, Next.Engine.EngineLab.ApplySettings);
            Hint("menu.engine_verify_hint");

            // Experimental tier (5.1): measured, default off, not provably exact.
            Header("menu.header.experimental", MoprColors.HeaderExperimentalBackground);
            Text(() => MoprColors.Colorize(MoprColors.Note, Loc.T("menu.experimental_hint")));
            ExperimentalEngineBay = CheckBox("experimentalEngineBay", "menu.exp_engine_bay", false, Next.Engine.EngineLab.ApplySettings);
            Hint("menu.exp_engine_bay_hint");
            ExperimentalMirrorRate = CheckBox("experimentalMirrorRate", "menu.exp_mirrors", false, Next.Engine.EngineLab.ApplySettings);
            Hint("menu.exp_mirrors_hint");

            // Graphics.
            Header("menu.header.graphics");
            Subheader("menu.sub.framerate");
            LimitFramerate = CheckBox("limitFramerate", "menu.limit_fps", false, OnLimitFramerate);
            BeginDependents(framerateDependents);
            FramerateLimiter = Slider("framerateLimiterUpdated", "menu.fps_limit", 20, 200, 60, MopSettings.UpdateFramerateLimiter);
            EndDependents();
            Spacer();
            Subheader("menu.sub.shadows");
            ShadowDistance = Slider("shadowDistance", "menu.shadow_distance", 0, 2000, 200, MopSettings.UpdateShadows);
            Hint("menu.shadow_distance_hint");
            Spacer();
            Subheader("menu.sub.other", MoprColors.SubheaderOther);
            GameResolution = DropDown("resolution", "menu.resolution", GetResolutions(out int selected), selected, OnResolutionChanged);
            DynamicDrawDistance = CheckBox("dynamicDrawDistance", "menu.dynamic_draw", true);
            Hint("menu.dynamic_draw_hint");
            KeepRunningInBackground = CheckBox("keepRunningInBackground", "menu.run_background", true, MopSettings.ToggleBackgroundRunning);
            Hint("menu.run_background_hint");

            // Other.
            Header("menu.header.other");
            DestroyEmptyBottles = CheckBox("destroyEmptyBottles", "menu.destroy_bottles", false);
            Hint("menu.destroy_bottles_hint");
            DisableEmptyItems = CheckBox("disableEmptyItems", "menu.disable_empty", false);
            Hint("menu.disable_empty_hint");
            AlwaysDisableSkidmarks = CheckBox("alwaysDisableSkidmarks", "menu.skidmarks", false);
            Hint("menu.skidmarks_hint");

            // Mod compatibility (rules): what is active, then the actions.
            Header("menu.header.rules");
            Hint("menu.rules_hint");
            rulesStatusText = Text(RulesManager.StatusText);
            ButtonRow(Button("menu.btn.reload_rules", ReloadRules),
                      Button("menu.btn.open_rules", RulesManager.OpenFolder));
            Button("menu.btn.new_rules", NewRulesFile);

            // Logs and bug reports.
            Header("menu.header.logging");
            Hint("menu.logging_hint");
            ButtonRow(Button("menu.btn.open_logs", ExceptionManager.OpenCurrentSessionLogFolder),
                      Button("menu.btn.report", ExceptionManager.GenerateReport));
            Button("menu.btn.delete_logs", ExceptionManager.DeleteAllLogs);

            // Changelog (the changelog itself stays English).
            Header("menu.header.changelog", true);
            Settings.AddText(MOP.GetChangelog(false));

            // About.
            Header("menu.header.about", true);
            Text(GetFooter);
        }

        /// <summary>"MOP - REVIVAL v5.1.0 / by ANICKON / based on the original MOP" in the 4.x colors.</summary>
        static string GetInfo()
        {
            return MoprColors.Bold(MoprColors.Colorize(MoprColors.Brand, "MOP - REVIVAL")) + " " +
                   MoprColors.Colorize(MoprColors.VersionHex, "v" + MOP.ModVersion.Replace("_", " ")) + "\n" +
                   Loc.F("menu.info.by", MoprColors.Colorize(MoprColors.Author, "ANICKON")) + "\n" +
                   MoprColors.Colorize(MoprColors.Hint, Loc.T("menu.info.based"));
        }

        static string RestartNotice()
        {
            if (!restartNoticeVisible) return "";
            bool safe = MopSettings.SelectedMode() == PerformanceMode.Safe || MopSettings.LoadedMode == PerformanceMode.Safe;
            return MoprColors.Colorize(MoprColors.Note, Loc.T(safe ? "menu.restart_note_safe" : "menu.restart_note"));
        }

        /// <summary>ModSettingsLoaded: the saved (or reset) dropdown value becomes the active language.</summary>
        internal static void OnSettingsLoaded()
        {
            ApplyLanguage(SelectedLanguage());
            SyncResolutionDropdown();
            UpdateDependents();
            RefreshRestartNotice();
        }

        #region Dependent settings
        // Settings that only work while a switch above them is on are hidden while it is off, so the page shows
        // which switch they belong to.
        static readonly List<ModSetting> renderDependents = new List<ModSetting>();
        static readonly List<ModSetting> framerateDependents = new List<ModSetting>();
        static List<ModSetting> collecting;

        static void BeginDependents(List<ModSetting> list) => collecting = list;
        static void EndDependents() => collecting = null;

        static T Track<T>(T setting) where T : ModSetting
        {
            if (collecting != null && setting != null) collecting.Add(setting);
            return setting;
        }

        static void UpdateDependents()
        {
            SetVisible(renderDependents, RenderOptimizerEnabled == null || RenderOptimizerEnabled.GetValue());
            SetVisible(framerateDependents, LimitFramerate != null && LimitFramerate.GetValue());
        }

        static void SetVisible(List<ModSetting> list, bool visible)
        {
            for (int i = 0; i < list.Count; i++)
            {
                try { list[i].SetVisibility(visible); }
                catch (Exception ex) { ModConsole.Warn("con.settings_page", ex.Message); }
            }
        }

        /// <summary>The render section's main switch: the optimizer and the Satsuma merger both follow it.</summary>
        static void OnRenderMainSwitch()
        {
            RenderOptimizer.OnSettingsChanged();
            VehicleMeshMerge.OnSettingsChanged();
            UpdateDependents();
        }

        static void OnLimitFramerate()
        {
            MopSettings.UpdateFramerateLimiter();
            UpdateDependents();
        }
        #endregion

        static bool inGame;

        /// <summary>Game loaded (true) or back in the main menu (false).</summary>
        internal static void SetRestartNotice(bool inGameNow)
        {
            inGame = inGameNow;
            RefreshRestartNotice();
        }

        /// <summary>
        /// "Part of the mode applies at the next load" under the performance modes: only in game, and only while
        /// the selected mode differs from the one the game was loaded with. Every other setting applies at once.
        /// </summary>
        internal static void RefreshRestartNotice()
        {
            restartNoticeVisible = inGame && MopSettings.SelectedMode() != MopSettings.LoadedMode;
            if (restartNoticeText == null) return;
            restartNoticeText.SetValue(RestartNotice());
            try { restartNoticeText.SetVisibility(restartNoticeVisible); }
            catch (Exception ex) { ModConsole.Warn("con.settings_page", ex.Message); }
        }

        #region Language
        static Language SelectedLanguage()
        {
            int index = LanguageSelect != null ? LanguageSelect.GetSelectedItemIndex() : 0;
            return Enum.IsDefined(typeof(Language), index) ? (Language)index : Language.English;
        }

        static void OnLanguageSelected()
        {
            ApplyLanguage(SelectedLanguage());
        }

        static void ApplyLanguage(Language language)
        {
            bool changed = Loc.SetLanguage(language);

            // Cache the choice for the next build of the page (see the file header).
            MopData data = SafeData();
            string code = Loc.ToCode(language);
            if (data != null && data.Language != code)
            {
                data.Language = code;
                try { MopSettings.WriteData(data); }
                catch (Exception ex) { ModConsole.Warn("con.language_store_failed", ex.Message); }
            }

            if (changed)
            {
                LocalizedSettings.Refresh();
                ModConsole.Info("con.language", Loc.LanguageNames[(int)language]);
            }
        }

        /// <summary>MopData, or null if the settings folder is not known yet / the file is unreadable.</summary>
        static MopData SafeData()
        {
            if (string.IsNullOrEmpty(MOP.ModConfigPath)) return null;
            try { return MopSettings.Data; }
            catch (Exception ex)
            {
                ModConsole.Warn("con.mopdata_read_failed", ex.Message);
                return null;
            }
        }
        #endregion

        #region Builders (each registers its text for live relabeling)
        static SettingsHeader Header(string key, bool collapsed = false)
        {
            SettingsHeader header = Settings.AddHeader(Loc.T(key), collapsed);
            LocalizedSettings.Header(header, () => Loc.T(key));
            return header;
        }

        /// <summary>A header with its own background (yellow title), like Information and Language in 4.x.</summary>
        static SettingsHeader Header(string key, Color background)
        {
            SettingsHeader header = Settings.AddHeader(Loc.T(key), background, MoprColors.HeaderTitle);
            LocalizedSettings.Header(header, () => Loc.T(key));
            return header;
        }

        /// <summary>"▍ GROUP" in bold color: groups settings inside a section.</summary>
        static SettingsText Subheader(string key, string hex = MoprColors.Subheader)
        {
            return Text(() => MoprColors.SubheaderText(hex, Loc.T(key)));
        }

        /// <summary>The short grey explanation under a setting.</summary>
        static SettingsText Hint(string key) => Text(() => MoprColors.Colorize(MoprColors.Hint, Loc.T(key)));

        /// <summary>An empty row between two groups.</summary>
        static SettingsText Spacer() => Track(Settings.AddText(" "));

        static SettingsText Text(Func<string> producer)
        {
            SettingsText text = Settings.AddText(producer());
            LocalizedSettings.Text(text, producer);
            return Track(text);
        }

        static SettingsButton Button(string key, Action onClick)
        {
            SettingsButton button = Settings.AddButton(Loc.T(key), onClick);
            LocalizedSettings.Button(button, () => Loc.T(key));
            return Track(button);
        }

        /// <summary>Two buttons on one row.</summary>
        static void ButtonRow(SettingsButton left, SettingsButton right) => SettingsUiPatcher.PairButtons(left, right);

        static SettingsCheckBox CheckBox(string id, string key, bool value, Action onChange = null)
        {
            SettingsCheckBox box = Settings.AddCheckBox(id, Loc.T(key), value, onChange);
            LocalizedSettings.Label(box, () => Loc.T(key));
            return Track(box);
        }

        static SettingsCheckBoxGroup Group(string id, string key, bool value, string group, Action onChange)
        {
            return Group(id, () => Loc.T(key), value, group, onChange);
        }

        static SettingsCheckBoxGroup Group(string id, Func<string> label, bool value, string group, Action onChange)
        {
            SettingsCheckBoxGroup box = Settings.AddCheckBoxGroup(id, label(), value, group, onChange);
            LocalizedSettings.Label(box, label);
            return Track(box);
        }

        static SettingsSliderInt Slider(string id, string key, int min, int max, int value, Action onChange = null, string[] valueKeys = null)
        {
            string[] values = null;
            if (valueKeys != null)
            {
                values = new string[valueKeys.Length];
                for (int i = 0; i < valueKeys.Length; i++)
                    values[i] = Loc.T(valueKeys[i]);
            }

            SettingsSliderInt slider = Settings.AddSlider(id, Loc.T(key), min, max, value, onChange, values);
            LocalizedSettings.Label(slider, () => Loc.T(key));
            LocalizedSettings.SliderValues(slider, values, valueKeys);
            return Track(slider);
        }

        static SettingsDropDownList DropDown(string id, string key, string[] items, int selected, Action onChange)
        {
            SettingsDropDownList list = Settings.AddDropDownList(id, Loc.T(key), items, selected, onChange);
            LocalizedSettings.Label(list, () => Loc.T(key));
            return Track(list);
        }
        #endregion

        #region Actions
        static string[] GetResolutions(out int selected)
        {
            List<string> resolutions = new List<string>();
            selected = 0;
            int i = 0;
            foreach (Resolution res in Screen.resolutions)
            {
                resolutions.Add(res.width + "x" + res.height);
                if (res.width == Screen.width && res.height == Screen.height)
                    selected = i;
                i++;
            }
            return resolutions.ToArray();
        }

        /// <summary>The dropdown shows the real current resolution, not a stale saved index.</summary>
        static void SyncResolutionDropdown()
        {
            if (GameResolution == null) return;
            Resolution[] all = Screen.resolutions;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].width == Screen.width && all[i].height == Screen.height)
                {
                    GameResolution.SetSelectedItemIndex(i);
                    return;
                }
            }
        }

        static void OnResolutionChanged()
        {
            try
            {
                Resolution res = Screen.resolutions[GameResolution.GetSelectedItemIndex()];
                Screen.SetResolution(res.width, res.height, Screen.fullScreen);
                ModConsole.Info("con.resolution_set", res.width, res.height);
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "RESOLUTION_CHANGE_ERROR");
            }
        }

        static SettingsText rulesStatusText;

        /// <summary>Rules were read again: show what is active now.</summary>
        internal static void RefreshRulesStatus()
        {
            if (rulesStatusText != null) rulesStatusText.SetValue(RulesManager.StatusText());
        }

        /// <summary>In the menu the rules apply at once; during a game they are only read (they apply at the next load).</summary>
        static void ReloadRules()
        {
            bool inGameNow = ModLoader.CurrentScene == CurrentScene.Game;
            RulesManager.Load(!inGameNow);
            if (inGameNow)
                ModUI.ShowMessage(Loc.T("msg.rules_next_load"), Loc.T("msg.title"));
        }

        static void NewRulesFile()
        {
            RulesManager.NewFile(null);
        }

        /// <summary>Asks before opening a link (hold Left Alt to skip the question).</summary>
        internal static void ShowDialog(string url)
        {
            if (Input.GetKey(KeyCode.LeftAlt))
            {
                System.Diagnostics.Process.Start(url);
                return;
            }

            ModUI.ShowYesNoMessage(Loc.F("msg.open_link", url), Loc.T("msg.title"), () => System.Diagnostics.Process.Start(url));
        }

        static string GetFooter()
        {
            return MoprColors.Bold(MoprColors.Colorize(MoprColors.Brand, "MOP - REVIVAL")) + " " +
                   MoprColors.Colorize(MoprColors.VersionHex, "v" + MOP.ModVersion.Replace("_", " ")) +
                   (BuildInfo.BuiltWithStubs ? Loc.T("menu.footer.test_build") : "") + "\n" +
                   MoprColors.Colorize(MoprColors.Author, "MSCLoader") + " " +
                   MoprColors.Colorize(MoprColors.VersionHex, "v" + ModLoader.MSCLoader_Ver) + "\n" +
                   MoprColors.Colorize(MoprColors.Hint, ExceptionManager.GetSystemInfo()) + "\n\n" +
                   MoprColors.Colorize(MoprColors.Hint, Loc.T("menu.footer.original")) + "\n" +
                   MoprColors.Colorize(MoprColors.Hint, Loc.T("menu.footer.revival"));
        }
        #endregion
    }
}
