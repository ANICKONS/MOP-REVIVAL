using System;
using MOPR6.Core;
using Xunit;

namespace MOPR6.Core.Tests
{
    public class ModVersionTests
    {
        [Theory]
        [InlineData("6.0.0", 6, 0, 0, 0)]
        [InlineData("6.0.0-preview.1", 6, 0, 0, 1)]
        [InlineData("v6.1.12-preview.30", 6, 1, 12, 30)]
        [InlineData(" 10.0.0 ", 10, 0, 0, 0)]
        public void Parses_valid_versions(string text, int major, int minor, int patch, int preview)
        {
            Assert.True(ModVersion.TryParse(text, out ModVersion v));
            Assert.Equal(major, v.Major);
            Assert.Equal(minor, v.Minor);
            Assert.Equal(patch, v.Patch);
            Assert.Equal(preview, v.Preview);
            Assert.Equal(preview > 0, v.IsPreview);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("6.0")]
        [InlineData("6.0.0.0")]
        [InlineData("6.0.x")]
        [InlineData("6.0.-1")]
        [InlineData("06.0.0")]
        [InlineData("6.0.0-beta.1")]
        [InlineData("6.0.0-preview.")]
        [InlineData("6.0.0-preview.0")]
        [InlineData("6.0.0-preview.1a")]
        [InlineData("+6.0.0")]
        public void Rejects_invalid_versions(string text)
        {
            Assert.False(ModVersion.TryParse(text, out _));
            Assert.Throws<FormatException>(() => ModVersion.Parse(text));
        }

        [Theory]
        [InlineData("6.0.0")]
        [InlineData("6.0.0-preview.7")]
        [InlineData("12.34.56-preview.78")]
        public void Round_trips_through_ToString(string text)
        {
            Assert.Equal(text, ModVersion.Parse(text).ToString());
        }

        [Theory]
        [InlineData("6.0.0-preview.1", "6.0.0-preview.2")]
        [InlineData("6.0.0-preview.9", "6.0.0-preview.10")]
        [InlineData("6.0.0-preview.99", "6.0.0")]
        [InlineData("6.0.0", "6.0.1-preview.1")]
        [InlineData("6.0.9", "6.1.0")]
        [InlineData("6.9.9", "7.0.0-preview.1")]
        public void Orders_versions(string lower, string higher)
        {
            ModVersion a = ModVersion.Parse(lower), b = ModVersion.Parse(higher);
            Assert.True(a < b);
            Assert.True(b > a);
            Assert.True(a.CompareTo(b) < 0);
            Assert.True(b.CompareTo(a) > 0);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Equal_versions_compare_equal()
        {
            Assert.Equal(ModVersion.Parse("v6.0.0-preview.3"), ModVersion.Parse("6.0.0-preview.3"));
            Assert.Equal(ModVersion.Parse("6.0.0").GetHashCode(), new ModVersion(6, 0, 0).GetHashCode());
            Assert.True(ModVersion.Parse("6.0.0") == new ModVersion(6, 0, 0, 0));
        }

        [Fact]
        public void Rejects_negative_parts()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ModVersion(6, -1, 0));
        }
    }
}
