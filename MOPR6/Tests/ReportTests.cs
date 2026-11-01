using System.Collections.Generic;
using MOPR6.Core;
using MOPR6.Core.Diagnostics;
using Xunit;

namespace MOPR6.Core.Tests
{
    /// <summary>Requirement 12.3 (no personal data in the bug report) and 13.4 (which release the update check offers).</summary>
    public class ReportTests
    {
        const string Profile = @"C:\Users\anick";

        [Theory]
        [InlineData(@"C:\Users\anick\AppData\LocalLow\Amistech\My Summer Car\defaultES2File.txt",
                    @"%USERPROFILE%\AppData\LocalLow\Amistech\My Summer Car\defaultES2File.txt")]
        [InlineData(@"c:/users/ANICK/AppData/x.txt", @"%USERPROFILE%/AppData/x.txt")]
        [InlineData(@"log: C:\Users\anick\a and C:\Users\anick\b", @"log: %USERPROFILE%\a and %USERPROFILE%\b")]
        [InlineData(@"D:\Games\anick\My Summer Car\Mods", @"D:\Games\%USERNAME%\My Summer Car\Mods")]
        [InlineData(@"D:\Games\anick", @"D:\Games\%USERNAME%")]
        [InlineData(@"""D:\anick"" ok", @"""D:\%USERNAME%"" ok")]
        public void Replaces_the_profile_and_the_user_name(string text, string expected)
        {
            Assert.Equal(expected, Redact.Paths(text, Profile, "anick"));
        }

        [Theory]
        [InlineData(@"D:\anickon\Mods")]          // a longer segment
        [InlineData(@"D:\x_anick\Mods")]          // inside a segment
        [InlineData("anick wrote this")]          // not in a path
        [InlineData(@"D:\SteamLibrary\steamapps\common\My Summer Car")]
        public void Leaves_other_text_alone(string text)
        {
            Assert.Equal(text, Redact.Paths(text, Profile, "anick"));
        }

        [Fact]
        public void Handles_missing_values()
        {
            Assert.Equal("", Redact.Paths(null, Profile, "anick"));
            Assert.Equal(@"C:\Users\a\x", Redact.Paths(@"C:\Users\a\x", null, "a"));   // a 1-letter name is not replaced
            Assert.Equal(@"%USERPROFILE%\x", Redact.Paths(@"C:\Users\anick\x", Profile + @"\", null));
        }

        static ReleaseInfo R(string tag, bool prerelease = false, bool draft = false) =>
            new ReleaseInfo { Tag = tag, Prerelease = prerelease, Draft = draft, Url = "u/" + tag };

        static string Pick(string local, params ReleaseInfo[] releases) =>
            UpdateCheck.Newest(ModVersion.Parse(local), releases, out _)?.Tag;

        [Fact]
        public void A_preview_sees_newer_previews_of_its_version_only()
        {
            Assert.Equal("v6.0.0-preview.2", Pick("6.0.0-preview.1",
                R("v5.1.1"), R("v5.2.0-BETA_1", true), R("v6.0.0-preview.2", true), R("v6.0.0-preview.3", true, draft: true)));
            Assert.Null(Pick("6.0.0-preview.1", R("v5.1.1"), R("v6.0.1-preview.1", true), R("v6.1.0-preview.1", true)));
        }

        [Fact]
        public void A_preview_sees_the_release()
        {
            Assert.Equal("v6.0.0", Pick("6.0.0-preview.4", R("v6.0.0-preview.5", true), R("v6.0.0")));
        }

        [Fact]
        public void A_release_sees_stable_releases_of_its_major_version_only()
        {
            Assert.Equal("v6.0.1", Pick("6.0.0", R("v6.0.0-preview.9", true), R("v5.2.0"), R("v6.0.1"), R("v7.0.0")));
            Assert.Null(Pick("6.0.0", R("v6.1.0-preview.1", true), R("v6.0.2", prerelease: true), R("v5.9.9")));
        }

        [Fact]
        public void The_newest_may_be_older_than_the_build()
        {
            List<ReleaseInfo> list = new List<ReleaseInfo> { R("v6.0.0-preview.1", true) };
            ReleaseInfo r = UpdateCheck.Newest(ModVersion.Parse("6.0.0-preview.2"), list, out ModVersion v);
            Assert.Equal("v6.0.0-preview.1", r.Tag);
            Assert.True(v < ModVersion.Parse("6.0.0-preview.2"));
        }
    }
}
