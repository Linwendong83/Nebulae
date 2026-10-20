#region

using System.Collections.Generic;
using System.IO;

#endregion

namespace NebulaeModel.DataStructures;

internal class StatisticalSnapShot
{
    public readonly long CapturedGameTick;
    public readonly long[] EnergyStored;
    public readonly long[] HashRegister;
    public readonly long[] PowerChargingRegister;
    public readonly long[] PowerConsumptionRegister;
    public readonly long[] PowerDischargingRegister;

    public readonly long[] PowerGenerationRegister;

    //List of statistical changes for each planet that happened in one gameTick
    public readonly List<ProductionChangeStruct>[] ProductionChangesPerFactory;

    public StatisticalSnapShot(long gameTick, int numOfActiveFactories)
    {
        ProductionChangesPerFactory = new List<ProductionChangeStruct>[numOfActiveFactories];
        for (var i = 0; i < numOfActiveFactories; i++)
        {
            ProductionChangesPerFactory[i] = [];
        }
        PowerGenerationRegister = new long[numOfActiveFactories];
        PowerConsumptionRegister = new long[numOfActiveFactories];
        PowerChargingRegister = new long[numOfActiveFactories];
        PowerDischargingRegister = new long[numOfActiveFactories];
        HashRegister = new long[numOfActiveFactories];
        EnergyStored = new long[numOfActiveFactories];
        CapturedGameTick = gameTick;
    }

    public StatisticalSnapShot(BinaryReader br)
    {
        CapturedGameTick = br.ReadInt64();
        var factoryCount = br.ReadInt32();

        ProductionChangesPerFactory = new List<ProductionChangeStruct>[factoryCount];
        PowerGenerationRegister = new long[factoryCount];
        PowerConsumptionRegister = new long[factoryCount];
        PowerChargingRegister = new long[factoryCount];
        PowerDischargingRegister = new long[factoryCount];
        EnergyStored = new long[factoryCount];
        HashRegister = new long[factoryCount];

        for (var factoryId = 0; factoryId < factoryCount; factoryId++)
        {
            ProductionChangesPerFactory[factoryId] = [];
            var changesCount = br.ReadInt32();
            for (var changeId = 0; changeId < changesCount; changeId++)
            {
                ProductionChangesPerFactory[factoryId].Add(new ProductionChangeStruct(br));
            }
            PowerGenerationRegister[factoryId] = br.ReadInt64();
            PowerConsumptionRegister[factoryId] = br.ReadInt64();
            PowerChargingRegister[factoryId] = br.ReadInt64();
            PowerDischargingRegister[factoryId] = br.ReadInt64();
            EnergyStored[factoryId] = br.ReadInt64();
            HashRegister[factoryId] = br.ReadInt64();
        }
    }

    public void Export(BinaryWriter bw)
    {
        bw.Write(CapturedGameTick);

        //Collect production/consumption statistics from factories
        bw.Write(ProductionChangesPerFactory.Length);
        for (var factoryId = 0; factoryId < ProductionChangesPerFactory.Length; factoryId++)
        {
            bw.Write(ProductionChangesPerFactory[factoryId].Count);
            for (var changeId = 0; changeId < ProductionChangesPerFactory[factoryId].Count; changeId++)
            {
                ProductionChangesPerFactory[factoryId][changeId].Export(bw);
            }

            //Collect info about power system of the factory
            bw.Write(PowerGenerationRegister[factoryId]);
            bw.Write(PowerConsumptionRegister[factoryId]);
            bw.Write(PowerChargingRegister[factoryId]);
            bw.Write(PowerDischargingRegister[factoryId]);
            bw.Write(EnergyStored[factoryId]);
            bw.Write(HashRegister[factoryId]);
        }
    }

    /// <summary>
    /// True when every field of this snapshot is a zero delta or an absolute value that still equals
    /// the previous snapshot's, i.e. when a receiver already holds everything this snapshot carries.
    /// </summary>
    /// <remarks>
    /// Every register is a per-tick delta, so a zero one says "nothing happened"; <see
    /// cref="EnergyStored"/> is the only absolute, and the receiver keeps the last array it was sent.
    /// A tick that is information-free is dropped rather than transmitted: it would cost
    /// <c>12 + 52 * factoryCount</c> bytes and teach the receiver nothing, and an idle base produces
    /// one such tick per simulation frame.
    /// </remarks>
    /// <param name="previousEnergyStored">
    /// Energy-stored values of the last snapshot that was kept, or null/empty when none was.
    /// </param>
    public bool IsInformationFree(long[] previousEnergyStored)
    {
        var factoryCount = ProductionChangesPerFactory.Length;
        for (var factoryId = 0; factoryId < factoryCount; factoryId++)
        {
            if (ProductionChangesPerFactory[factoryId].Count > 0) return false;
            if (PowerGenerationRegister[factoryId] != 0 || PowerConsumptionRegister[factoryId] != 0 ||
                PowerChargingRegister[factoryId] != 0 || PowerDischargingRegister[factoryId] != 0 ||
                HashRegister[factoryId] != 0) return false;
            var stored = EnergyStored[factoryId];
            // Before the first kept snapshot there is no previous value to compare against, so a
            // non-zero reading is news; afterwards only a change is.
            if (previousEnergyStored == null || previousEnergyStored.Length != factoryCount)
            {
                if (stored != 0) return false;
            }
            else if (stored != previousEnergyStored[factoryId])
            {
                return false;
            }
        }
        return true;
    }

    internal readonly struct ProductionChangeStruct //12 bytes total
        (bool isProduction, ushort productId, int amount)
    {
        public readonly bool IsProduction = isProduction; //1-byte
        public readonly ushort ProductId = productId; //2-byte
        public readonly int Amount = amount; //4-byte

        public ProductionChangeStruct(BinaryReader r) : this(r.ReadBoolean(), r.ReadUInt16(), r.ReadInt32())
        {
        }

        public void Export(BinaryWriter w)
        {
            w.Write(IsProduction);
            w.Write(ProductId);
            w.Write(Amount);
        }
    }
}
