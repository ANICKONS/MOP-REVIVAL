// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Update check (requirement 13.4; ported from MOPR 5.x Common/UpdateChecker.cs). Once per game start, in the main
// menu, if the setting allows it: MOPR6 asks GitHub for the repository's releases and picks the one this build should
// know about (Core.UpdateCheck: a preview sees the newer v6.0.0-preview.N and the 6.x releases, a release build only
// 6.x releases; the 5.x releases of the same repository are ignored). A newer one gives a message box in the main menu
// with the link to its page (GitHub for previews, Nexus Mods for releases) and a console line.
//
// GitHub answers without a key; it needs TLS 1.2, which Unity 5.0's WWW and Mono 2.0's WebClient lack, so the request
// goes through Windows' WinHTTP on a background thread (SystemHttp). A failed check is only written to the mod log.

using System;
using System.Collections;
using System.Collections.Generic;
using MOPR6.Core;
using MOPR6.Diagnostics;
using MOPR6.Localization;
using MSCLoader;
using Newtonsoft.Json;
using UnityEngine;

namespace MOPR6.UI
{
    internal static class UpdateChecker
    {
        const string ReleasesApi = "https://api.github.com/repos/ANICKONS/MOP-REVIVAL/releases?per_page=50";
        const string NexusLink = "https://www.nexusmods.com/mysummercar/mods/11595?tab=files";
        const float TimeoutSeconds = 15f;

        static bool checkedThisSession, running;

        /// <summary>GitHub's release object; only what is needed (Newtonsoft ignores the rest).</summary>
        sealed class Release
        {
            public string tag_name = null;
            public bool draft = false;
            public bool prerelease = false;
            public string html_url = null;
        }

        /// <summary>Main menu: the automatic check, once per game start, if the setting allows it.</summary>
        internal static void OnMenuLoad(MonoBehaviour host)
        {
            if (checkedThisSession || running || host == null) return;
            checkedThisSession = true;
            host.StartCoroutine(Run(false));
        }

        /// <summary>Console "mopr update": checks now and prints the result (the setting does not matter).</summary>
        internal static string CheckNow(MonoBehaviour host, Language language)
        {
            if (host == null) return "-";
            if (running) return Loc.FIn(language, "con.update.running");
            host.StartCoroutine(Run(true));
            return "";
        }

        static IEnumerator Run(bool manual)
        {
            running = true;
            try
            {
                if (!manual)
                {
                    // The setting comes with the saved values, after OnMenuLoad.
                    float wait = Time.realtimeSinceStartup + 30f;
                    while (!SettingsPage.Loaded && Time.realtimeSinceStartup < wait) yield return null;
                    if (!SettingsPage.CheckUpdates)
                    {
                        ModLog.Line("update check: off in the settings");
                        yield break;
                    }
                }

                SystemHttp.Request request = SystemHttp.Get(ReleasesApi, "MOPR6/" + BuildInfo.Version,
                                                            "Accept: application/vnd.github+json\r\n", (int)(TimeoutSeconds * 1000));
                float deadline = Time.realtimeSinceStartup + TimeoutSeconds + 5f;
                while (!request.Done && Time.realtimeSinceStartup < deadline) yield return null;

                string error = !request.Done ? "no answer in " + TimeoutSeconds + " s" : request.Error;
                ModVersion local = ModVersion.Parse(BuildInfo.Version);
                ReleaseInfo newest = null;
                ModVersion newestVersion = default(ModVersion);
                if (error == null)
                {
                    try
                    {
                        List<Release> list = JsonConvert.DeserializeObject<List<Release>>(request.Text);
                        List<ReleaseInfo> infos = new List<ReleaseInfo>();
                        if (list != null)
                            foreach (Release r in list)
                                if (r != null) infos.Add(new ReleaseInfo { Tag = r.tag_name, Draft = r.draft, Prerelease = r.prerelease, Url = r.html_url });
                        newest = UpdateCheck.Newest(local, infos, out newestVersion);
                    }
                    catch (Exception ex)
                    {
                        ModLog.Report("update/Parse", ex);
                        error = ex.GetType().Name + ": " + ex.Message;
                    }
                }

                string installed = "v" + BuildInfo.Version;
                if (error != null)
                {
                    ModLog.Line("update check failed: " + error);
                    if (manual) Print(Loc.F("con.update.failed", error));
                    yield break;
                }
                if (newest == null)
                {
                    ModLog.Line("update check: no release for this build");
                    if (manual) Print(Loc.T("con.update.none"));
                    yield break;
                }
                string shown = "v" + newestVersion;
                int cmp = newestVersion.CompareTo(local);
                if (cmp <= 0)
                {
                    ModLog.Line("update check: newest " + shown + ", this build " + installed);
                    if (manual) Print(cmp == 0 ? Loc.F("con.update.latest", installed) : Loc.F("con.update.ahead", installed, shown));
                    yield break;
                }

                // Previews are published on GitHub only; releases on Nexus Mods, like 5.x.
                string link = newestVersion.IsPreview ? (newest.Url ?? SettingsPage.GitHubLink) : NexusLink;
                ModLog.Line("update check: " + shown + " is available (" + link + ")");
                Print(Loc.F("con.update.available", shown, installed, link));
                // The message only in the main menu: in game it would cover the screen while playing.
                if (ModLoader.CurrentScene == CurrentScene.MainMenu) ShowMessage(shown, installed, link);
            }
            finally
            {
                running = false;
            }
        }

        static void Print(string text) => ModConsole.Print("MOPR6: " + text);

        static void ShowMessage(string latest, string installed, string link)
        {
            ModUI.ShowCustomMessage(Loc.F("msg.update_available", latest, installed), Loc.T("msg.title.update"),
                new MsgBoxBtn[]
                {
                    ModUI.CreateMessageBoxBtn(Loc.T("msg.btn.open_page"), () => SettingsPage.OpenUrl(link)),
                    ModUI.CreateMessageBoxBtn(Loc.T("msg.btn.later")),
                },
                new MsgBoxBtn[] { });
        }
    }
}
