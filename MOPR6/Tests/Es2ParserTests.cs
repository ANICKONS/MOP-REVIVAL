using System;
using System.IO;
using System.Linq;
using System.Text;
using MOPR6.Core.Es2;
using Xunit;

namespace MOPR6.Core.Tests
{
    public class Es2ParserTests
    {
        // Writes tags the way ES2Writer does: 126, name, int32 length of the rest, header, payload.
        sealed class Es2Builder
        {
            readonly MemoryStream ms = new MemoryStream();
            readonly BinaryWriter w;

            public Es2Builder() { w = new BinaryWriter(ms, new UTF8Encoding(false)); }

            public Es2Builder Tag(string name, Action<BinaryWriter> body)
            {
                MemoryStream inner = new MemoryStream();
                using (BinaryWriter iw = new BinaryWriter(inner, new UTF8Encoding(false), true)) body(iw);
                w.Write((byte)126);
                w.Write(name);
                w.Write((int)inner.Length);
                w.Write(inner.ToArray());
                return this;
            }

            public byte[] Bytes() { w.Flush(); return ms.ToArray(); }
        }

        static Es2Entry Get(Es2ParseResult r, string key) => r.Entries.Single(e => e.Key == key);

        static int H(string typeName) => Es2Types.Hash(typeName);

        [Fact]
        public void Type_hash_matches_the_game()
        {
            // From the header of "GifuRadioTuner" in a real defaultES2File.txt: FF 6B D7 3E 6E, then a float.
            Assert.Equal(0x6E3ED76B, H("System.Single"));
            Assert.Equal(Es2Type.Float, Es2Types.FromHash(0x6E3ED76B));
            Assert.Equal("System.Single", Es2Types.NameOf(0x6E3ED76B));
            Assert.Equal(Es2Type.Unknown, Es2Types.FromHash(12345));
            Assert.Equal(0, H(""));
        }

        [Fact]
        public void Parses_the_format_the_game_writes()
        {
            // ES2Writer: [collection] 255 hash [key hash], payload, terminator 123.
            byte[] data = new Es2Builder()
                .Tag("GifuRadioTuner", w => { w.Write((byte)255); w.Write(H("System.Single")); w.Write(0.05f); w.Write((byte)123); })
                .Tag("Doors", w => { w.Write((byte)255); w.Write(H("System.Boolean")); w.Write(false); w.Write((byte)123); })
                .Tag("Pos", w =>
                {
                    w.Write((byte)255); w.Write(H("UnityEngine.Transform"));
                    w.Write((byte)2); w.Write(1f); w.Write(2f); w.Write(3f); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f);
                    w.Write((byte)123);
                })
                .Tag("Names", w =>
                {
                    w.Write((byte)83); w.Write((byte)255); w.Write(H("System.String"));
                    w.Write((byte)0); w.Write(2); w.Write("a"); w.Write("b");
                    w.Write((byte)123);
                })
                .Tag("Map", w =>
                {
                    w.Write((byte)82); w.Write((byte)255); w.Write(H("System.Single")); w.Write(H("System.Int32"));
                    w.Write((byte)0); w.Write((byte)0); w.Write(1); w.Write(7); w.Write(2.5f);
                    w.Write((byte)123);
                })
                .Tag("Mesh", w => { w.Write((byte)255); w.Write(H("UnityEngine.Mesh")); w.Write(new byte[] { 1, 2, 3 }); w.Write((byte)123); })
                .Tag("User", w => { w.Write((byte)255); w.Write(777); w.Write(new byte[] { 4, 5 }); w.Write((byte)123); })
                .Bytes();

            Es2ParseResult r = Es2Parser.Parse(data);

            Assert.Null(r.Error);
            Assert.Empty(r.Warnings);
            Assert.Equal("0.05", Get(r, "GifuRadioTuner").Text);
            Assert.Equal("false", Get(r, "Doors").Text);
            Assert.Equal("1,2,3", Get(r, "Pos.pos").Text);
            Assert.Equal("0,0,0,1", Get(r, "Pos.rot").Text);
            Assert.Equal("2", Get(r, "Names.count").Text);
            Assert.Equal("b", Get(r, "Names[1]").Text);
            Assert.Equal("2.5", Get(r, "Map{7}").Text);
            Assert.Equal(Es2ValueKind.Raw, Get(r, "Mesh").Kind);
            Assert.Equal(Es2ValueKind.Raw, Get(r, "User").Kind);
            Assert.Equal(1, r.UnknownHashes[777]);
            Assert.Single(r.UnknownHashes);
        }

        [Fact]
        public void Parses_single_values()
        {
            byte[] data = new Es2Builder()
                .Tag("b", w => { w.Write((byte)9); w.Write(true); })
                .Tag("i", w => { w.Write((byte)2); w.Write(-42); })
                .Tag("f", w => { w.Write((byte)6); w.Write(0.1f); })
                .Tag("s", w => { w.Write((byte)1); w.Write("Satsuma ääkköset"); })
                .Tag("v", w => { w.Write((byte)11); w.Write(1f); w.Write(-2.5f); w.Write(3f); })
                .Tag("q", w => { w.Write((byte)14); w.Write(0f); w.Write(0.7071068f); w.Write(0f); w.Write(0.7071068f); })
                .Tag("c32", w => { w.Write((byte)26); w.Write((byte)255); w.Write((byte)0); w.Write((byte)10); w.Write((byte)128); })
                .Bytes();

            Es2ParseResult r = Es2Parser.Parse(data);

            Assert.Null(r.Error);
            Assert.Empty(r.Warnings);
            Assert.Equal(7, r.Tags);
            Assert.Equal("true", Get(r, "b").Text);
            Assert.Equal(Es2ValueKind.Integer, Get(r, "i").Kind);
            Assert.Equal("-42", Get(r, "i").Text);
            Assert.Equal("0.1", Get(r, "f").Text);
            Assert.Equal(0.1f, (float)Get(r, "f").Numbers[0]);
            Assert.Equal("Satsuma ääkköset", Get(r, "s").Text);
            Assert.Equal("1,-2.5,3", Get(r, "v").Text);
            Assert.Equal(Es2ValueKind.Quaternion, Get(r, "q").Kind);
            Assert.Equal(4, Get(r, "q").Numbers.Length);
            Assert.Equal("255,0,10,128", Get(r, "c32").Text);
        }

        [Fact]
        public void Parses_transform_fields_in_order()
        {
            byte[] data = new Es2Builder()
                .Tag("satsuma", w =>
                {
                    w.Write((byte)16);
                    w.Write((byte)4);
                    w.Write(1f); w.Write(2f); w.Write(3f);
                    w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f);
                    w.Write(1f); w.Write(1f); w.Write(1f);
                    w.Write("Untagged");
                })
                .Tag("short", w => { w.Write((byte)16); w.Write((byte)1); w.Write(5f); w.Write(6f); w.Write(7f); })
                .Bytes();

            Es2ParseResult r = Es2Parser.Parse(data);

            Assert.Empty(r.Warnings);
            Assert.Equal("1,2,3", Get(r, "satsuma.pos").Text);
            Assert.Equal("0,0,0,1", Get(r, "satsuma.rot").Text);
            Assert.Equal("1,1,1", Get(r, "satsuma.scale").Text);
            Assert.Equal("Untagged", Get(r, "satsuma.tag").Text);
            Assert.Equal("5,6,7", Get(r, "short.pos").Text);
            Assert.DoesNotContain(r.Entries, e => e.Key == "short.rot");
        }

        [Fact]
        public void Parses_collections()
        {
            byte[] data = new Es2Builder()
                .Tag("arr", w => { w.Write((byte)81); w.Write((byte)2); w.Write(2); w.Write(10); w.Write(20); })
                .Tag("list", w => { w.Write((byte)83); w.Write((byte)6); w.Write((byte)6); w.Write(1); w.Write(1.5f); })
                .Tag("dict", w =>
                {
                    // 82, value key (int), key key (string); payload: 2 bytes, count, (key, value)*
                    w.Write((byte)82); w.Write((byte)2); w.Write((byte)1);
                    w.Write((byte)1); w.Write((byte)2); w.Write(2);
                    w.Write("a"); w.Write(1);
                    w.Write("b"); w.Write(2);
                })
                .Bytes();

            Es2ParseResult r = Es2Parser.Parse(data);

            Assert.Empty(r.Warnings);
            Assert.Equal("2", Get(r, "arr.count").Text);
            Assert.Equal("10", Get(r, "arr[0]").Text);
            Assert.Equal("20", Get(r, "arr[1]").Text);
            Assert.Equal("1", Get(r, "list.count").Text);
            Assert.Equal("1.5", Get(r, "list[0]").Text);
            Assert.Equal("2", Get(r, "dict.count").Text);
            Assert.Equal("1", Get(r, "dict{a}").Text);
            Assert.Equal("2", Get(r, "dict{b}").Text);
        }

        [Fact]
        public void Keeps_undecodable_tags_raw_and_goes_on()
        {
            byte[] data = new Es2Builder()
                .Tag("mesh", w => { w.Write((byte)15); w.Write(new byte[] { 1, 2, 3, 4, 5 }); })
                .Tag("custom", w => { w.Write((byte)255); w.Write(12345); w.Write(new byte[] { 9, 9 }); })
                .Tag("enc", w => { w.Write((byte)127); w.Write((byte)2); w.Write(new byte[] { 7, 7, 7, 7, 7, 7 }); })
                .Tag("short", w => { w.Write((byte)2); w.Write((byte)1); })              // int with 1 byte
                .Tag("long", w => { w.Write((byte)9); w.Write(true); w.Write((byte)0); })   // bool + junk
                .Tag("after", w => { w.Write((byte)2); w.Write(7); })
                .Bytes();

            Es2ParseResult r = Es2Parser.Parse(data);

            Assert.Null(r.Error);
            Assert.Equal(6, r.Tags);
            foreach (string k in new[] { "mesh", "custom", "enc", "short", "long" })
                Assert.Equal(Es2ValueKind.Raw, Get(r, k).Kind);
            Assert.StartsWith("len:6 md5:", Get(r, "mesh").Text);
            Assert.Equal("7", Get(r, "after").Text);
            // Meshes and custom types are raw on purpose; the others are reported.
            Assert.Equal(3, r.Warnings.Count);
            Assert.Contains(r.Warnings, w => w.StartsWith("enc:", StringComparison.Ordinal));
            Assert.Contains(r.Warnings, w => w.StartsWith("short:", StringComparison.Ordinal));
            Assert.Contains(r.Warnings, w => w.StartsWith("long:", StringComparison.Ordinal));
        }

        [Fact]
        public void Raw_entries_differ_when_bytes_differ()
        {
            byte[] a = new Es2Builder().Tag("m", w => { w.Write((byte)15); w.Write(new byte[] { 1, 2, 3 }); }).Bytes();
            byte[] b = new Es2Builder().Tag("m", w => { w.Write((byte)15); w.Write(new byte[] { 1, 2, 4 }); }).Bytes();
            Assert.NotEqual(Es2Parser.Parse(a).Entries[0].Text, Es2Parser.Parse(b).Entries[0].Text);
        }

        [Fact]
        public void Stops_at_terminator_and_reports_broken_structure()
        {
            byte[] good = new Es2Builder().Tag("x", w => { w.Write((byte)2); w.Write(1); }).Bytes();
            byte[] withEnd = good.Concat(new byte[] { 0, 1, 2, 3 }).ToArray();
            Es2ParseResult end = Es2Parser.Parse(withEnd);
            Assert.Null(end.Error);
            Assert.Single(end.Entries);

            byte[] broken = good.Concat(new byte[] { 99, 1, 2 }).ToArray();
            Es2ParseResult r = Es2Parser.Parse(broken);
            Assert.NotNull(r.Error);
            Assert.Equal("1", Get(r, "x").Text);
            Assert.Equal(Es2ValueKind.Raw, r.Entries.Last().Kind);

            byte[] truncated = good.Take(good.Length - 2).ToArray();
            Es2ParseResult t = Es2Parser.Parse(truncated);
            Assert.NotNull(t.Error);
        }

        [Fact]
        public void Recognises_non_es2_files()
        {
            Assert.True(Es2Parser.Parse(Encoding.ASCII.GetBytes("Win+R-Appdata")).NotEs2);
            Es2ParseResult empty = Es2Parser.Parse(new byte[0]);
            Assert.False(empty.NotEs2);
            Assert.Empty(empty.Entries);
        }
    }
}
