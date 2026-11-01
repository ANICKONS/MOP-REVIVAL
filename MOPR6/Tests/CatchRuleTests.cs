using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace MOPR6.Core.Tests
{
    /// <summary>
    /// Requirement 12.1: no exception of the mod is swallowed. Every catch clause in MOPR6/Mod and
    /// MOPR6/Core/src must report (a call to Fail / Report / ReportError) or rethrow (throw), or carry a
    /// "// catch-ok: reason" comment on the catch line or in its block. The allowed ones are listed in the test output.
    /// </summary>
    public class CatchRuleTests
    {
        static readonly string[] ReportingCalls = { "Fail", "Report", "ReportError" };
        readonly ITestOutputHelper output;

        public CatchRuleTests(ITestOutputHelper output) { this.output = output; }

        static string Mopr6Root()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "MOPR6.csproj"))) d = d.Parent;
            if (d == null) throw new InvalidOperationException("MOPR6 not found above " + AppContext.BaseDirectory);
            return d.FullName;
        }

        [Fact]
        public void Every_catch_reports_or_rethrows()
        {
            string root = Mopr6Root();
            List<string> files = new List<string>();
            foreach (string dir in new[] { Path.Combine(root, "Mod"), Path.Combine(root, "Core", "src") })
                if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories));
            Assert.NotEmpty(files);

            List<string> violations = new List<string>(), allowed = new List<string>();
            foreach (string file in files)
            {
                SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.CSharp9));
                foreach (CatchClauseSyntax c in tree.GetRoot().DescendantNodes().OfType<CatchClauseSyntax>())
                {
                    string where = Path.GetRelativePath(root, file) + ":" + (c.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                    string ok = CatchOkReason(c);
                    if (ok != null) { allowed.Add(where + " - " + ok); continue; }
                    if (Rethrows(c) || Reports(c)) continue;
                    violations.Add(where);
                }
            }

            foreach (string a in allowed) output.WriteLine("catch-ok: " + a);
            Assert.True(violations.Count == 0, "catch clauses that neither report nor rethrow:\n" + string.Join("\n", violations));
        }

        static bool Rethrows(CatchClauseSyntax c) =>
            c.Block.DescendantNodes().OfType<ThrowStatementSyntax>().Any() || c.Block.DescendantNodes().OfType<ThrowExpressionSyntax>().Any();

        static bool Reports(CatchClauseSyntax c) =>
            c.Block.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i =>
            {
                string name = i.Expression switch
                {
                    MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
                    IdentifierNameSyntax n => n.Identifier.Text,
                    _ => null
                };
                return name != null && ReportingCalls.Contains(name);
            });

        static string CatchOkReason(CatchClauseSyntax c)
        {
            // FullSpan: also a comment after the closing brace on the same line (trailing trivia).
            IEnumerable<SyntaxTrivia> trivia = c.DescendantTrivia(c.FullSpan);
            foreach (SyntaxTrivia t in trivia)
            {
                if (!t.IsKind(SyntaxKind.SingleLineCommentTrivia)) continue;
                string s = t.ToString();
                int i = s.IndexOf("catch-ok:", StringComparison.Ordinal);
                if (i >= 0) return s.Substring(i + "catch-ok:".Length).Trim();
            }
            return null;
        }

        [Fact]
        public void The_rule_sees_violations()
        {
            CatchClauseSyntax Parse(string body) =>
                CSharpSyntaxTree.ParseText("class C { void M() { try { } " + body + " } }").GetRoot().DescendantNodes().OfType<CatchClauseSyntax>().Single();

            Assert.False(Reports(Parse("catch { }")) || Rethrows(Parse("catch { }")));
            Assert.True(Reports(Parse("catch (System.Exception e) { Sys.Fail(\"x\", e); }")));
            Assert.True(Rethrows(Parse("catch { throw; }")));
            Assert.Equal("reason", CatchOkReason(Parse("catch (System.IO.IOException) // catch-ok: reason\n { }")));
            Assert.Equal("inside", CatchOkReason(Parse("catch { // catch-ok: inside\n }")));
        }
    }
}
