// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Update check (5.1.1). Once per game start, in the main menu: MOPR asks GitHub for its latest release
// (api.github.com/repos/<repo>/releases/latest; drafts and pre-releases are never "latest") and, when that
// version is newer than this build, shows a message whose only link goes to the Nexus Mods page.
//
// GitHub because it answers without a key (the Nexus API needs a personal API key, the Nexus page is behind
// Cloudflare). GitHub requires TLS 1.2, which neither Unity 5.0's WWW nor Mono 2.0's WebClient has (MSCLoader
// talks to its own server over plain HTTP); the request goes through Windows' WinHTTP instead (SystemHttp).
//
// A failed check (offline, timeout, rate limit) is only written to the mod log. Only the version number is
// compared: a release published again under the same number is not seen as new.

using System;
using System.Collections;
using System.Diagnostics;
using MSCLoader;
using Newtonsoft.Json;
using UnityEngine;

using MOP.Localization;

namespace MOP.Common
{
    static class UpdateChecker
    {
        const string LatestReleaseApi = "https://api.github.com/repos/ANICKONS/MOP-REVIVAL/releases/latest";
        internal const string NexusLink = "https://www.nexusmods.com/mysummercar/mods/11595?tab=files";
        const float TimeoutSeconds = 15f;

        static bool checkedThisSession;
        static bool running;

        /// <summary>GitHub's release object; only what is needed (Newtonsoft ignores the rest).</summary>
        sealed class Release
        {
            public string tag_name = null;
            public bool draft = false;
            public bool prerelease = false;
        }

        enum Outcome { Newer, Latest, Ahead, Failed }

        /// <summary>Main menu: the automatic check, once per game start, if the setting allows it.</summary>
        internal static void OnMenuLoad(MonoBehaviour host)
        {
            if (checkedThisSession || running || host == null) return;
            checkedThisSession = true;
            host.StartCoroutine(Run(false));
        }

        /// <summary>Console "mopr update": checks now and prints the result (the setting does not matter).</summary>
        internal static void CheckNow(MonoBehaviour host, bool preview)
        {
            if (host == null) return;
            if (running)
            {
                ModConsole.Info("con.update.running");
                return;
            }
            host.StartCoroutine(Run(true, preview));
        }

        static IEnumerator Run(bool manual, bool preview = false)
        {
            running = true;
            try
            {
                if (!manual)
                {
                    // The setting and the language come with the settings (they may load after the menu does).
                    float wait = Time.realtimeSinceStartup + 30f;
                    while (!MOP.SettingsLoaded && Time.realtimeSinceStartup < wait) yield return null;
                    if (MopMenu.CheckUpdates != null && !MopMenu.CheckUpdates.GetValue())
                    {
                        ModConsole.Debug("Update check: off in the settings.");
                        yield break;
                    }
                }

                string text = null, error = null;
                // GitHub's API needs a User-Agent; the background thread never touches Unity.
                SystemHttp.Request request = SystemHttp.Get(LatestReleaseApi, "MOP-REVIVAL/" + MOP.ModVersion,
                                                            "Accept: application/vnd.github+json\r\n", (int)(TimeoutSeconds * 1000));
                float deadline = Time.realtimeSinceStartup + TimeoutSeconds + 5f;
                while (!request.Done && Time.realtimeSinceStartup < deadline) yield return null;
                if (!request.Done) error = "no answer in " + TimeoutSeconds + " s";
                else if (request.Error != null) error = request.Error;
                else text = request.Text;

                string latest = null;
                Outcome outcome = Outcome.Failed;
                if (error == null)
                {
                    try
                    {
                        Release release = JsonConvert.DeserializeObject<Release>(text);
                        if (release == null || string.IsNullOrEmpty(release.tag_name)) error = "no tag_name in the answer";
                        else if (release.draft || release.prerelease) error = "the latest release is a draft or a pre-release";
                        else
                        {
                            latest = release.tag_name;
                            int cmp;
                            if (!TryCompare(latest, out cmp)) error = "can't read the version " + latest;
                            else outcome = cmp > 0 ? Outcome.Newer : cmp == 0 ? Outcome.Latest : Outcome.Ahead;
                        }
                    }
                    catch (Exception ex)
                    {
                        error = ex.GetType().Name + ": " + ex.Message;
                    }
                }

                string installed = "v" + MOP.ModVersion.Replace("_", " ");
                string shown = latest != null ? Display(latest) : null;
                switch (outcome)
                {
                    case Outcome.Failed:
                        ModConsole.Debug("Update check failed: " + error);
                        if (manual) ModConsole.Info("con.update.failed", error);
                        break;
                    case Outcome.Latest:
                        ModConsole.Debug("Update check: " + shown + " is the latest release.");
                        if (manual) ModConsole.Info("con.update.latest", installed);
                        break;
                    case Outcome.Ahead:
                        ModConsole.Debug($"Update check: this build ({installed}) is newer than the latest release ({shown}).");
                        if (manual) ModConsole.Info("con.update.ahead", installed, shown);
                        break;
                    case Outcome.Newer:
                        ModConsole.Info("con.update.available", shown, installed, NexusLink);
                        break;
                }

                // The message only in the main menu: in game it would cover the screen while playing.
                if ((outcome == Outcome.Newer || (preview && shown != null)) && ModLoader.CurrentScene == CurrentScene.MainMenu)
                    ShowMessage(shown, installed);
            }
            finally
            {
                running = false;
            }
        }

        static void ShowMessage(string latest, string installed)
        {
            ModUI.ShowCustomMessage(Loc.F("msg.update_available", latest, installed), Loc.T("msg.title.update"),
                new MsgBoxBtn[]
                {
                    ModUI.CreateMessageBoxBtn(Loc.T("msg.btn.open_nexus"), () => Process.Start(NexusLink)),
                    ModUI.CreateMessageBoxBtn(Loc.T("msg.btn.later")),
                },
                new MsgBoxBtn[] { });
        }

        /// <summary>"v5.1.2" -> "v5.1.2", "v5.2.0-BETA_1" -> "v5.2.0 BETA 1".</summary>
        static string Display(string tag) => "v" + tag.TrimStart('v', 'V').Replace('-', ' ').Replace('_', ' ');

        /// <summary>
        /// The release tag against this build: &gt; 0 = the release is newer. Tags are "v5.1.1" or
        /// "v5.1.1-BETA_1" (scripts/release.ps1). The same number without a suffix is newer than a build with one.
        /// </summary>
        internal static bool TryCompare(string tag, out int result)
        {
            result = 0;
            Version remote, local;
            string remoteSuffix, localSuffix = MOP.SubVersion ?? "";
            if (!TryParse(tag, out remote, out remoteSuffix) || !TryParse(MOP.ModVersionShort, out local, out string _))
                return false;

            result = remote.CompareTo(local);
            if (result != 0) return true;
            bool remoteFinal = remoteSuffix.Length == 0, localFinal = localSuffix.Length == 0;
            if (remoteFinal != localFinal) result = remoteFinal ? 1 : -1;
            else if (!remoteFinal) result = string.Compare(remoteSuffix, localSuffix, StringComparison.OrdinalIgnoreCase);
            return true;
        }

        static bool TryParse(string tag, out Version version, out string suffix)
        {
            version = null;
            suffix = "";
            if (string.IsNullOrEmpty(tag)) return false;
            string s = tag.Trim().TrimStart('v', 'V');
            int dash = s.IndexOfAny(new[] { '-', '_', ' ' });
            if (dash >= 0)
            {
                suffix = s.Substring(dash + 1);
                s = s.Substring(0, dash);
            }
            string[] parts = s.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return false;
            int[] n = new int[4];
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], out n[i]) || n[i] < 0) return false;
            version = new Version(n[0], n[1], n[2], n[3]);
            return true;
        }
    }
}
