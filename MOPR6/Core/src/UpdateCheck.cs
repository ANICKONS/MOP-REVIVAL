// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Which GitHub release the update checker talks about (requirement 13.4, design §13). The repository also holds the
// 5.x releases, so GitHub's "latest release" would point preview users back to 5.x. Rules:
//   * release builds: stable releases (not drafts, not pre-releases) of the same major version;
//   * preview builds: previews of the same version (v6.0.0-preview.N) and the stable releases of the same major
//     version (6.0.0 itself is newer than every 6.0.0 preview, and a preview user must hear about it);
//   * tags that are not MOPR versions (5.x "v5.2.0-BETA_1", typos) are ignored.
// Shared code: compiled for net8.0 (tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System.Collections.Generic;

namespace MOPR6.Core
{
    public sealed class ReleaseInfo
    {
        public string Tag;
        public bool Draft;
        public bool Prerelease;
        public string Url;
    }

    public static class UpdateCheck
    {
        /// <summary>
        /// The newest release this build should compare itself with (it may be older than the build), or null when
        /// there is none.
        /// </summary>
        public static ReleaseInfo Newest(ModVersion local, IEnumerable<ReleaseInfo> releases, out ModVersion newest)
        {
            newest = default(ModVersion);
            ReleaseInfo best = null;
            if (releases == null) return null;
            foreach (ReleaseInfo r in releases)
            {
                if (r == null || r.Draft) continue;
                if (!ModVersion.TryParse(r.Tag, out ModVersion v)) continue;
                if (!Applies(local, v, r.Prerelease)) continue;
                if (best == null || v > newest)
                {
                    best = r;
                    newest = v;
                }
            }
            return best;
        }

        /// <summary>True if a release with version <paramref name="remote"/> is one this build should know about.</summary>
        public static bool Applies(ModVersion local, ModVersion remote, bool prerelease)
        {
            if (remote.Major != local.Major) return false;
            if (!remote.IsPreview) return !prerelease;
            return local.IsPreview && remote.Minor == local.Minor && remote.Patch == local.Patch;
        }
    }
}
