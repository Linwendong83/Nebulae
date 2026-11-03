using System.Reflection;
using NebulaeModel;
using NebulaeModel.Attributes;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class MultiplayerOptionsTest
{
    [TestMethod]
    public void EveryOptionPropertyDeclaresAUniqueConfigKey()
    {
        // The key is what users' existing nebulae.cfg stores, so it is spelled out per
        // property instead of being derived from a name the release build may rename.
        var properties = typeof(MultiplayerOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        TestAssert.IsGreaterThan(0, properties.Length);

        var keys = properties.Select(property => property.GetCustomAttribute<ConfigKeyAttribute>()).ToArray();
        foreach (var (property, attribute) in properties.Zip(keys, (property, attribute) => (property, attribute)))
            TestAssert.IsNotNull(attribute, $"{property.Name} has no config key");
        TestAssert.HasCount(keys.Length, keys.Select(key => key!.Key).Distinct().ToArray());
        foreach (var key in keys)
            TestAssert.IsFalse(string.IsNullOrWhiteSpace(key!.Key));
    }

}
