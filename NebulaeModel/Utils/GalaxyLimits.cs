using System;
using System.Reflection;

namespace NebulaeModel.Utils;

internal static class GalaxyLimits
{
    public const int MinStars = 32;
    public const int VanillaMaxStars = 64;
    public const int MaxStars = 8191;
    public const int MaxGalaxyAstroId = (MaxStars + 1) * 100 - 1;
    private static readonly FieldInfo ReferenceData = typeof(UIReferenceSpeedTip).GetField("loadedSubTipDatas", BindingFlags.Instance | BindingFlags.NonPublic);

    public static void ValidateStarCount(int starCount)
    {
        if (starCount < MinStars || starCount > MaxStars)
            throw new ArgumentOutOfRangeException(nameof(starCount), starCount, $"Supported star count is {MinStars} to {MaxStars}.");
    }

    public static int AstroCapacity(int starCount) => Math.Max(25700, checked((starCount + 1) * 100));

    // The native short owner is retained on disk and on the wire. Space enemies
    // already carry their full hive identity, including after leaving its frame.
    public static int SpaceFormationOwner(ref EnemyData enemy) => enemy.originAstroId > 1000000
        ? enemy.originAstroId - 1000000 : enemy.owner;

    public static void SetSpaceFormationOwner(ref EnemyData enemy, int hiveIndex)
    {
        enemy.originAstroId = checked(1000000 + hiveIndex);
        enemy.owner = unchecked((short)hiveIndex);
    }

    public static void EnsureAstros(GalaxyData galaxy)
    {
        var capacity = AstroCapacity(galaxy.starCount);
        if (galaxy.astrosData.Length < capacity) Array.Resize(ref galaxy.astrosData, capacity);
        if (galaxy.astrosFactory.Length < capacity) Array.Resize(ref galaxy.astrosFactory, capacity);
    }

    public static void EnsureReferenceCapacity(UIReferenceSpeedTip tip)
    {
        var capacity = AstroCapacity(GameMain.galaxy?.starCount ?? 0);
        var entries = (RefSpeedSubTipEntryData[])ReferenceData.GetValue(tip);
        if (entries != null && entries.Length >= capacity) return;
        Array.Resize(ref entries, capacity);
        ReferenceData.SetValue(tip, entries);
    }
}
