using System.Text;

namespace RssReaderTests;

/// <summary>
/// Source files must be valid UTF-8. A script that writes with the platform's default
/// encoding turns a typed "…" into a single 0x85 byte: it still compiles (the compiler
/// substitutes a replacement character) but shows up as a broken glyph in the product.
/// </summary>
[TestClass]
public sealed class SourceEncodingTests
{
    private static readonly string[] Extensions = { ".cs", ".razor", ".css", ".js", ".json", ".md", ".yml", ".yaml", ".ps1", ".sh", ".bicep" };

    private static string? FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "rss-reader.sln")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    [TestMethod]
    public void SourceFiles_AreValidUtf8()
    {
        var root = FindRepositoryRoot();
        if (root == null)
        {
            Assert.Inconclusive("The repository root (rss-reader.sln) was not found above the test output.");
            return;
        }

        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var skip = new[] { "node_modules", $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                           $"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}.fixture{Path.DirectorySeparatorChar}",
                           $"{Path.DirectorySeparatorChar}dbcopy{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}feeds{Path.DirectorySeparatorChar}",
                           "package-lock.json", "playwright-report", "test-results", "results" + Path.DirectorySeparatorChar };

        var bad = new List<string>();
        foreach (var dir in new[] { "src", "test", ".github", "infrastructure" })
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                if (skip.Any(s => file.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    strict.GetString(File.ReadAllBytes(file));
                }
                catch (DecoderFallbackException)
                {
                    bad.Add(Path.GetRelativePath(root, file));
                }
            }
        }

        Assert.AreEqual(0, bad.Count, "Not valid UTF-8 (re-save as UTF-8): " + string.Join(", ", bad));
    }
}
