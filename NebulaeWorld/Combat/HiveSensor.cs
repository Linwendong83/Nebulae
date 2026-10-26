using System;

namespace NebulaeWorld.Combat;

/// <summary>Native hive detection geometry, shared by host hatred and local warning colors.</summary>
internal static class HiveSensor
{
    internal static bool Detect(EnemyDFHiveSystem hive, VectorLF3 position, EAggressiveLevel level,
        bool player, out double hatred, bool alerted = false)
    {
        hatred = 0;
        if (level <= EAggressiveLevel.Passive) return false;
        var scale = level switch
        {
            EAggressiveLevel.Rampage => 1.25f,
            EAggressiveLevel.Sharp => 1.1f,
            EAggressiveLevel.Normal => 1f,
            _ => 0.8f
        };
        var range = scale * 16000f * (hive.evolve.rank / 127f);
        var radialRange = scale * 6000f;
        var relative = position - hive.starData.uPosition;
        var radius = relative.magnitude;
        if (radius <= 0 || double.IsNaN(radius) || double.IsInfinity(radius)) return false;
        var radialDistance = Math.Abs(hive.orbitRadius - radius);
        if (player ? radialDistance >= radialRange : radialDistance > radialRange) return false;
        var projected = relative * (hive.orbitRadius / radius) + hive.starData.uPosition;
        var distance = (projected - hive.sector.astros[hive.hiveAstroId - 1000000].uPos).magnitude;
        var hysteresis = player && !alerted && hive.hatred.max.targetType != ETargetType.Player ? 0.8f : 1f;
        if (distance >= range * hysteresis) return false;
        hatred = Maths.Clamp01((range - distance) / (range - 5500f)) * 15.0 + 3.0;
        hatred *= Maths.Clamp01((radialRange - radialDistance) / (radialRange - 1500f));
        return true;
    }

    internal static void UpdateAlert(SpaceSector sector)
    {
        var model = sector?.model;
        if (model == null) return;
        var previous = model.aggressiveHiveAstroId;
        model.aggressiveHiveAstroId = 0;
        var game = sector.gameData;
        var star = game?.localStar;
        var player = game?.mainPlayer;
        if (star == null || player == null || !player.isAlive || game.history.dfTruceTimer > 0) return;
        var level = game.history.combatSettings.aggressiveLevel;
        if (level <= EAggressiveLevel.Passive) return;
        // The client skips DecisionAI entirely. Derive its shader input without running AI or
        // changing the authoritative hatred list, and inspect only the local star's siblings.
        for (var hive = sector.dfHives[star.index]; hive != null; hive = hive.nextSibling)
        {
            if (!hive.realized || hive.isEmpty || !hive.isAlive) continue;
            if (game.localPlanet == null && Detect(hive, player.uPosition, level, true, out _, previous == hive.hiveAstroId))
            { model.aggressiveHiveAstroId = hive.hiveAstroId; return; }
            for (var i = 1; i < sector.craftCursor; i++)
            {
                ref var craft = ref sector.craftPool[i];
                if (craft.id != i || craft.astroId != star.astroId || craft.isInvincible) continue;
                sector.TransformFromAstro(craft.astroId, out var position, craft.pos);
                if (!Detect(hive, position, level, false, out _)) continue;
                model.aggressiveHiveAstroId = hive.hiveAstroId;
                return;
            }
        }
    }
}
