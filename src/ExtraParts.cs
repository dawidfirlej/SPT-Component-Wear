namespace ComponentWear;

// The server advertises enabled categories in the same template field used by the
// original client. Keep these numeric values stable for matching client/server builds.
internal enum ExtraPart
{
    None = 0,
    Magazines = 1,
    GasBlocks = 2,
    ChargingHandles = 3,
    OtherReceivers = 4,
    UnderbarrelLaunchers = 5,
    Handguards = 6,
    Stocks = 7,
    PistolGrips = 8,
    Foregrips = 9,
    Bipods = 10,
    IronSights = 11,
    Optics = 12,
    TacticalDevices = 13,
    Mounts = 14,
    AuxiliaryParts = 15
}

internal static class ExtraParts
{
    internal const int FirstMarker = 23000;

    internal static ExtraPart Decode(int repairSpeed) =>
        repairSpeed > FirstMarker && repairSpeed <= FirstMarker + (int)ExtraPart.AuxiliaryParts
            ? (ExtraPart)(repairSpeed - FirstMarker)
            : ExtraPart.None;

    internal static float ShotRate(ExtraPart part) => part switch
    {
        ExtraPart.Magazines or ExtraPart.GasBlocks => 0.10f,
        ExtraPart.ChargingHandles or ExtraPart.OtherReceivers => 0.08f,
        ExtraPart.Handguards or ExtraPart.Stocks => 0.06f,
        ExtraPart.PistolGrips or ExtraPart.Foregrips or ExtraPart.Bipods => 0.04f,
        ExtraPart.IronSights or ExtraPart.Optics or ExtraPart.TacticalDevices => 0.03f,
        ExtraPart.Mounts or ExtraPart.AuxiliaryParts => 0.02f,
        _ => 0f
    };

    internal const double KitCostFactor = 0.06;
    internal static int TraderRepairCost(int? existing) =>
        existing is > 0 ? System.Math.Min(existing.Value, 6) : 6;
    internal const float LauncherShotRate = 0.08f;
    internal const float TraderDegradation = 0.012f;
    internal const float KitDegradation = 0.010f;
}
