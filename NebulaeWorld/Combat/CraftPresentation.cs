using UnityEngine;
namespace NebulaeWorld.Combat;
internal static class CraftPresentation
{
    private static void UpdateStatPosition(in CraftData craft)
    {
        var stats = GameMain.spaceSector?.skillSystem?.combatStats;
        if (stats != null && craft.combatStatId > 0 && craft.combatStatId < stats.cursor && stats.buffer[craft.combatStatId].id == craft.combatStatId)
            stats.buffer[craft.combatStatId].localPos = craft.pos;
    }

    internal static void RefreshGround(PlanetFactory factory, int id, bool moved, bool rotated)
    {
        ref var craft = ref factory.craftPool[id];
        if (moved)
        {
            var hash = craft.dynamic ? factory.hashSystemDynamic : factory.hashSystemStatic;
            craft.hashAddress = hash.UpdateObjectHashAddress(craft.hashAddress, id, craft.pos, EObjectType.Craft);
            UpdateStatPosition(craft);
        }
        if ((moved || rotated) && craft.colliderId > 0)
        {
            var descriptors = PlanetFactory.PrefabDescByModelIndex[craft.modelIndex]?.colliders;
            var colliderId = craft.colliderId;
            if (descriptors != null)
                for (var i = descriptors.Length - 1; i >= 0 && colliderId > 0; i--)
                {
                    var previous = factory.planet.physics.GetColliderData(colliderId);
                    if (previous.objType != EObjectType.Craft || previous.objId != id) break;
                    factory.planet.physics.SetColliderData(colliderId, descriptors[i].BindToObject(id, previous.link, EObjectType.Craft, craft.pos, craft.rot));
                    colliderId = previous.link;
                }
        }
        var model = factory.planet.factoryModel;
        var renderers = model?.gpuiManager?.objectRenderers;
        if (renderers == null || craft.modelId <= 0 || craft.modelIndex >= renderers.Length) return;
        var renderer = renderers[craft.modelIndex];
        if (renderer == null || craft.modelId >= renderer.instPool.Length) return;
        ref var instance = ref renderer.instPool[craft.modelId];
        if (instance.posx == (float)craft.pos.x && instance.posy == (float)craft.pos.y && instance.posz == (float)craft.pos.z &&
            instance.rotx == craft.rot.x && instance.roty == craft.rot.y && instance.rotz == craft.rot.z && instance.rotw == craft.rot.w) return;
        instance.posx = (float)craft.pos.x; instance.posy = (float)craft.pos.y; instance.posz = (float)craft.pos.z;
        instance.rotx = craft.rot.x; instance.roty = craft.rot.y; instance.rotz = craft.rot.z; instance.rotw = craft.rot.w;
        model.craftDirty = true;
    }

    internal static void RefreshSpace(SpaceSector sector, int id)
    {
        ref var craft = ref sector.craftPool[id];
        UpdateStatPosition(craft);
        var model = sector.model;
        var renderers = model?.gpuiManager?.objectRenderers;
        if (renderers == null || craft.modelId <= 0 || craft.modelIndex >= renderers.Length) return;
        var renderer = renderers[craft.modelIndex];
        if (renderer == null || craft.modelId >= renderer.instPool.Length) return;
        var position = craft.pos;
        var rotation = craft.rot;
        if (craft.astroId == 0)
        { position = Maths.QInvRotateLF(GameMain.data.relativeRot, position - GameMain.data.relativePos); rotation = Maths.QInvMultiply(GameMain.data.relativeRot, rotation); }
        ref var instance = ref renderer.instPool[craft.modelId];
        instance.astroId = (uint)craft.astroId;
        instance.posx = (float)position.x; instance.posy = (float)position.y; instance.posz = (float)position.z;
        instance.rotx = rotation.x; instance.roty = rotation.y; instance.rotz = rotation.z; instance.rotw = rotation.w;
        model.craftDirty = true;
    }
}
