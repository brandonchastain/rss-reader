using WasmApp.Services;

namespace SerializerTests;

/// <summary>
/// Each filter view caches into its own slot, keyed by this signature. It ends
/// up inside a localStorage key, so it has to be character-safe, and it has to
/// be stable -- a signature that varied between visits would look like a cache
/// that silently never hits.
/// </summary>
[TestClass]
public class CacheSignatureTests
{
    [TestMethod]
    public void Unfiltered_IsTheDefaultSlot()
    {
        Assert.AreEqual("all", PostCache.SignatureFor(false, false, null));
        Assert.AreEqual("all", PostCache.SignatureFor(false, false, ""));
        Assert.AreEqual("all", PostCache.SignatureFor(false, false, "   "));
    }

    [TestMethod]
    public void EachFilterGetsItsOwnSlot()
    {
        var all = PostCache.SignatureFor(false, false, null);
        var unread = PostCache.SignatureFor(true, false, null);
        var saved = PostCache.SignatureFor(false, true, null);
        var tag = PostCache.SignatureFor(false, false, "tech");

        CollectionAssert.AllItemsAreUnique(new[] { all, unread, saved, tag });
    }

    [TestMethod]
    public void CombinedFiltersDifferFromEitherAlone()
    {
        var unread = PostCache.SignatureFor(true, false, null);
        var tag = PostCache.SignatureFor(false, false, "tech");
        var both = PostCache.SignatureFor(true, false, "tech");

        Assert.AreNotEqual(unread, both);
        Assert.AreNotEqual(tag, both);
    }

    [TestMethod]
    public void IsStableAcrossCalls()
    {
        // A signature that varied per call would mean every visit missed.
        Assert.AreEqual(
            PostCache.SignatureFor(true, false, "tech"),
            PostCache.SignatureFor(true, false, "tech"));
    }

    [TestMethod]
    public void TagCasingDoesNotSplitTheSlot()
    {
        // Feeds in the live data carry both "HQ" and "hq"; treating them as two
        // views would halve the hit rate for no benefit.
        Assert.AreEqual(
            PostCache.SignatureFor(false, false, "HQ"),
            PostCache.SignatureFor(false, false, "hq"));
    }

    [TestMethod]
    public void TagIsReducedToStorageSafeCharacters()
    {
        var sig = PostCache.SignatureFor(false, false, "c++ / rust!");

        Assert.IsFalse(sig.Contains('/'), "a slash would read as a key separator");
        Assert.IsFalse(sig.Contains(' '));
        Assert.IsTrue(sig.StartsWith("tag-"));
    }

    [TestMethod]
    public void PunctuationOnlyTagDoesNotCollideWithUnfiltered()
    {
        // Stripping unsafe characters from "!!!" leaves nothing, which would
        // otherwise land the tag view on top of the default slot.
        var sig = PostCache.SignatureFor(false, false, "!!!");

        Assert.AreNotEqual("all", sig);
        Assert.AreNotEqual(PostCache.SignatureFor(false, false, null), sig);
    }
}
