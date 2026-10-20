namespace NebulaeModel.Authority;

// Immutable identifiers used solely to decode historical NBAS records. Values are unchanged.

internal enum HostPlayerRole : byte
{
    Unknown = 0,

    /// <summary>The hosting player itself: plays and hosts.</summary>
    LocalHost = 1,

    /// <summary>A connected client player. Same rules as <see cref="LocalHost"/>.</summary>
    Remote = 2,

    /// <summary>
    /// A headless dedicated server with no mecha. Never a combat or repair owner.
    /// </summary>
    HeadlessDedicated = 3
}

internal enum LedgerOwnerKind : byte
{
    Unknown = 0,
    Player = 1,
    BattleBase = 2
}

internal enum LedgerResourceKind : byte
{
    Unknown = 0,
    CoreEnergy = 1,
    ReactorEnergy = 2,
    BaseEnergy = 3,
    Sand = 4,
    DroneSlot = 5,
    InventoryItem = 6,
    AmmoBullet = 7,
    AmmoStorageItem = 8,
    BombStorageItem = 9,
    FighterStorageItem = 10,
    ReactorStorageItem = 11,
    WarpStorageItem = 12,
    DeliveryItem = 13,
    ForgeItem = 14,
    BaseStockItem = 15
}

internal enum ConstructionTaskKind : byte
{
    Unknown = 0,
    Build = 1,
    Repair = 2,
    Reconstruct = 3
}

internal enum ConstructionTaskStage : byte
{
    Unknown = 0,
    Reserved = 1,
    Launching = 2,
    Travelling = 3,
    Working = 4,
    Returning = 5,
    Completed = 6,
    Cancelled = 7
}

internal enum ConstructionCancelReason : byte
{
    None = 0,
    TargetDemolished = 1,
    TargetDestroyed = 2,
    TargetRecycled = 3,
    OwnerOffline = 4,
    OwnerDead = 5,
    OwnerLeftPlanet = 6,
    SwitchDisabled = 7,
    BaseUnpowered = 8,
    InsufficientStock = 9,
    DemandCovered = 10,
    Retargeted = 11,
    Superseded = 12,
    WorldUnloaded = 13
}
