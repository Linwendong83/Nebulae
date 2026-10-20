using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.Packets.Statistics;
using NebulaeModel.Networking;
using UnityEngine;

namespace NebulaeWorld.Statistics;

internal sealed class LocalReferenceStatistics
{
    private readonly Dictionary<int, FactoryProductionStat> scratch = new();
    private readonly Dictionary<string, (byte[] Bytes, float At)> tips = new();

    internal FactoryProductionStat GetScratch(int planet)
    {
        if (scratch.TryGetValue(planet, out var stat)) return stat;
        stat = new FactoryProductionStat(); stat.Init(); scratch[planet] = stat;
        return stat;
    }
    public bool CanCalculate(int astroFilter)
    {
        var planets = Multiplayer.Session.Planets;
        if (!planets.BaselineKnown) return false;
        if (astroFilter > 0 && astroFilter % 100 != 0)
            return GameMain.galaxy.PlanetById(astroFilter)?.factory != null || !planets.KnownFactoryPlanets.Contains(astroFilter);
        return planets.KnownFactoryPlanets.Where(p => astroFilter <= 0 || p / 100 == astroFilter / 100)
            .All(p => GameMain.galaxy.PlanetById(p)?.factory != null);
    }

    // Never let a client's local factory index address the host's statistics pool.
    public static FactoryProductionStat ResolveStat(FactoryProductionStat[] pool, PlanetFactory factory) =>
        Multiplayer.IsActive && Multiplayer.Session.IsClient
            ? Multiplayer.Session.Statistics.LocalReferences.GetScratch(factory.planetId) : pool[factory.index];

    public void RefreshRates(int astroFilter)
    {
        var data = GameMain.data; var manager = Multiplayer.Session.Statistics;
        for (var i = 0; i < data.factoryCount; i++)
        {
            var factory = data.factories[i];
            if (factory == null || (astroFilter > 0 && (astroFilter % 100 == 0 ? factory.planetId / 100 != astroFilter / 100 : factory.planetId != astroFilter))) continue;
            var hostIndex = manager.GetFactoryIndex(factory.planet);
            if (hostIndex < 0 || hostIndex >= data.statistics.production.factoryStatPool.Length) continue;
            var destination = data.statistics.production.factoryStatPool[hostIndex];
            if (destination == null) continue;
            data.statistics.production.extraInfoCalculator.CalculateFactory(data, factory);
            var calculated = GetScratch(factory.planetId);
            // Reference speed is presentation. Authoritative stock/history remains untouched.
            destination.ResetRefSpeed();
            for (var j = 1; j < calculated.productCursor; j++)
            {
                var product = calculated.productPool[j];
                destination.AddRefProductSpeed(product.itemId, product.refProductSpeed);
                destination.AddRefConsumeSpeed(product.itemId, product.refConsumeSpeed);
            }
        }
    }
    private static string Key(int item, int astro, int cycle, int proto) => $"{item}:{astro}:{cycle}:{proto}";
    public bool HasTip(int item, int astro, int cycle, int proto) => tips.TryGetValue(Key(item, astro, cycle, proto), out var cached) && Time.realtimeSinceStartup - cached.At < 1;
    public void ReceiveTip(StatisticsReferenceSpeedTipPacket packet)
    {
        if (tips.Count >= 32) tips.Clear();
        tips[Key(packet.ItemId, packet.AstroFilter, packet.ItemCycle, packet.ProductionProtoId)] = ((byte[])packet.BinaryData.Clone(), Time.realtimeSinceStartup);
    }
    public void ApplyTip(UIReferenceSpeedTip tip, int proto)
    {
        if (!HasTip(tip.itemId, tip.astroFilter, (int)tip.itemCycle, proto)) return;
        using var reader = new BinaryUtils.Reader(tips[Key(tip.itemId, tip.astroFilter, (int)tip.itemCycle, proto)].Bytes);
        Multiplayer.Session.Statistics.SetReferenceSpeedTip(reader.BinaryReader, tip.itemId, tip.astroFilter, (int)tip.itemCycle, proto);
    }
}
