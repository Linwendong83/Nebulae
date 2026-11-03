using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class StellarPresentationTest
{
    [TestMethod]
    public void AppearanceBudgetIsAnchoredAndRejectsNearStarsAndChangedProperties()
    {
        var cache = new DistantStarAppearance();
        Check.IsFalse(cache.TryReuse(1e8, .001f, 1, 1, out _));
        cache.Capture(1e8, .001f, 1, 1, default);
        Check.IsTrue(cache.TryReuse(1e8 + 9000, .001f, 1, 1, out _));
        Check.IsFalse(cache.TryReuse(1e8 + 18000, .001f, 1, 1, out _), "Small steps must not accumulate unbounded error.");
        Check.IsFalse(cache.TryReuse(1e8, .001001f, 1, 1, out _));
        Check.IsFalse(cache.TryReuse(1e8, .001f, 2, 1, out _));
        Check.IsFalse(cache.TryReuse(1e8, .001f, 1, 2, out _));
        cache.Capture(1e6, .1f, 1, 1, default);
        Check.IsFalse(cache.TryReuse(1e6, .1f, 1, 1, out _));
        cache.Capture(3e6, .1f, 100, 1, default);
        Check.IsFalse(cache.TryReuse(3e6, .1f, 100, 1, out _));
        Check.IsFalse(StellarPresentation.Close(1, double.NaN));
        Check.IsFalse(StellarPresentation.Close(1, double.PositiveInfinity));
        Check.IsFalse(StellarPresentation.Close(0, 0));
    }
}
