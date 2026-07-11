using SPTarkov.DI.Annotations;
using YetAnotherTraderMod.src.Features.CustomConsumables;
using YetAnotherTraderMod.src.Services;
using YetAnotherTraderMod.src.Services.ItemHelpers;

namespace YetAnotherTraderMod.src;

/// <summary>
/// Small public facade for addon mods, shaped like WTTServerCommonLib.
///
/// Addon usage:
/// await yatmCommon.CustomTraderOfferServiceExtended.CreateCustomTraderOffers(assembly, path);
/// await yatmCommon.CustomConsumablesServiceExtended.CreateCustomConsumables(assembly, path);
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class YATMCommonLib(
    YATMTraderOfferFeedService customTraderOfferServiceExtended,
    CustomConsumablesLoader customConsumablesServiceExtended,
    YATMWeaponBuildService customWeaponBuildServiceExtended)
{
    /// <summary>
    /// Registers Tony trader offers before the YATM runtime builds the final assort.
    /// </summary>
    public YATMTraderOfferFeedService CustomTraderOfferServiceExtended { get; } = customTraderOfferServiceExtended;

    /// <summary>
    /// Registers YATM custom consumables from addon folders.
    /// Plural name matches the service method: CreateCustomConsumables.
    /// </summary>
    public CustomConsumablesLoader CustomConsumablesServiceExtended { get; } = customConsumablesServiceExtended;


    /// <summary>
    /// Loads reusable non-preset weapon build trees for addon mods.
    /// </summary>
    public YATMWeaponBuildService CustomWeaponBuildServiceExtended { get; } = customWeaponBuildServiceExtended;

    /// <summary>
    /// Backward-compatible singular alias for older addon code.
    /// </summary>
    public CustomConsumablesLoader CustomConsumableServiceExtended { get; } = customConsumablesServiceExtended;
}
