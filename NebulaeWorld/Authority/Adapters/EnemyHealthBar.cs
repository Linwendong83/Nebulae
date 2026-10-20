using UnityEngine;

namespace NebulaeWorld.Authority.Adapters;

/// <summary>Vanilla model geometry, independent of damage ownership.</summary>
internal static class EnemyHealthBar
{
    public static void Update(ref CombatStat stat, in EnemyData enemy, bool space)
    {
        var index = enemy.modelIndex;
        var height = index >= 0 && index < SkillSystem.BarHeightByModelIndex.Length ? SkillSystem.BarHeightByModelIndex[index] : 0;
        stat.size = index >= 0 && index < SkillSystem.BarWidthByModelIndex.Length ? SkillSystem.BarWidthByModelIndex[index] : 1;
        stat.dynamic = enemy.dynamic ? 1 : 0;
        stat.localPos = enemy.pos;
        if (!enemy.dynamic)
            stat.localPos += space ? new Vector3(0, Mathf.Sign((float)enemy.pos.y) * height, 0) : (Vector3)enemy.pos.normalized * height;
        else if (space && enemy.astroId == 0)
            stat.localPos = Maths.QInvRotateLF(GameMain.data.relativeRot, enemy.pos - GameMain.data.relativePos);
    }
}
