using UnityEngine;

namespace NebulaeWorld.Combat;

internal static class MechaInvincibilityVisual
{
    private static readonly int InvincibleShaderId = Shader.PropertyToID("_Global_Player_Invicible");
    private static readonly int InitPositionShaderId = Shader.PropertyToID("_InitPositionSet");

    public static void Apply(MechaArmorModel armor)
    {
        if (!Multiplayer.IsActive) return;

        // Keep the fallback off: each mecha supplies the original shader uniform through
        // its own materials, including uniforms not exposed in the Properties block.
        Shader.SetGlobalInt(InvincibleShaderId, 0);
        var invincible = armor.player?.invincible == true ? 1 : 0;
        var position = armor.player?.transform != null ? armor.player.transform.position : armor.transform.position;
        Apply(armor.inst_part_ar_mat, invincible, position);
        Apply(armor.inst_part_sk_mat, invincible, position);
        Apply(armor.part_mats_inst, invincible, position);
        Apply(armor.bone_mats_inst, invincible, position);
        // Individually colored stock parts have another set of per-part materials.
        var customMask = armor.mechaAppearance?.partCustomMask ?? 0;
        if (customMask == 0 || armor.partModels == null) return;
        foreach (var part in armor.partModels)
        {
            if (part == null || (customMask & (1UL << part.partId)) == 0) continue;
            Apply(part.inst_part_ar_mat, invincible, position);
            Apply(part.inst_part_sk_mat, invincible, position);
            Apply(part.inst_part_ar_em_mat, invincible, position);
            Apply(part.inst_part_sk_em_mat, invincible, position);
        }
    }

    private static void Apply(Material[] materials, int invincible, Vector3 position)
    {
        if (materials == null) return;
        foreach (var material in materials)
            Apply(material, invincible, position);
    }

    private static void Apply(Material material, int invincible, Vector3 position)
    {
        if (material == null) return;
        material.SetInt(InvincibleShaderId, invincible);
        material.SetVector(InitPositionShaderId, position);
    }

    public static void RestoreAlive(MechaArmorModel armor)
    {
        // _Open refreshes stock parts but does not rebuild custom bone armor. Refresh both
        // from the saved appearance, retaining intentionally hidden stock parts.
        armor.RefreshAllPartObjects();
        armor.RefreshAllBoneObjects();
        if (armor.partModels != null)
            foreach (var part in armor.partModels) if (part != null) part.Respawn();
        if (armor.boneModels != null)
            foreach (var bone in armor.boneModels) if (bone != null) bone.Respawn();
        Apply(armor);
    }
}
