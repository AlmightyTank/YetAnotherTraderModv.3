using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils.Cloners;
using YetAnotherTraderMod.src.Models;

namespace YetAnotherTraderMod.src.Services.ItemHelpers;

[Injectable]
public sealed class YATMSlotCloneHelper(
    DatabaseService databaseService,
    ICloner cloner)
{
    public void Process(YATMItemModificationRequest request)
    {
        if (request == null)
        {
            LogError("Process received a null request.");
            return;
        }

        // Leave this enabled while testing. It confirms the helper is actually called.
        YATMLogger.LogDebug(
            $"[SlotCloneHelper] Process called for '{request.ItemId}'. " +
            $"CopySlot={request.CopySlot}, CopySlots={request.CopySlots.Count}");

        if (!request.CopySlot)
        {
            YATMLogger.LogDebug(
                $"[SlotCloneHelper] Slot copying disabled for '{request.ItemId}'.");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.ItemId))
        {
            LogError("Request.ItemId is null or empty.");
            return;
        }

        if (!MongoId.IsValidMongoId(request.ItemId))
        {
            LogError($"Target item id '{request.ItemId}' is not a valid MongoId.");
            return;
        }

        var copySlots = request.CopySlots;

        if (copySlots.Count == 0)
        {
            LogError(
                $"No copy-slot entries were resolved for target '{request.ItemId}'. " +
                "Check copySlot/copySlotsInfo deserialization.");
            return;
        }

        var itemDatabase = databaseService.GetItems();
        var targetItemId = new MongoId(request.ItemId);

        if (!itemDatabase.TryGetValue(targetItemId, out var targetItem) ||
            targetItem == null)
        {
            LogError(
                $"Target item '{request.ItemId}' was not found in the item database. " +
                "The slot helper must run after the custom item has been inserted.");
            return;
        }

        targetItem.Properties ??= new TemplateItemProperties();

        var existingSlots = targetItem.Properties.Slots?.ToList() ?? [];
        var createdSlots = new List<Slot>();

        foreach (var copyInfo in copySlots)
        {
            if (copyInfo == null)
            {
                continue;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(copyInfo.Id))
                {
                    LogError(
                        $"Source item id is empty while processing '{request.ItemId}'.");
                    continue;
                }

                if (!MongoId.IsValidMongoId(copyInfo.Id))
                {
                    LogError(
                        $"Source item id '{copyInfo.Id}' is not a valid MongoId " +
                        $"while processing '{request.ItemId}'.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(copyInfo.NewSlotName))
                {
                    LogError(
                        $"NewSlotName is missing while processing '{request.ItemId}'.");
                    continue;
                }

                var sourceSlotName = string.IsNullOrWhiteSpace(copyInfo.TgtSlotName)
                    ? copyInfo.NewSlotName
                    : copyInfo.TgtSlotName;

                if (SlotExists(existingSlots, copyInfo.NewSlotName) ||
                    SlotExists(createdSlots, copyInfo.NewSlotName))
                {
                    LogError(
                        $"Slot '{copyInfo.NewSlotName}' already exists on " +
                        $"'{request.ItemId}', skipping.");
                    continue;
                }

                if (!TryGetSlotByName(
                        new MongoId(copyInfo.Id),
                        sourceSlotName,
                        out var sourceSlot) ||
                    sourceSlot == null)
                {
                    LogError(
                        $"Source slot '{sourceSlotName}' was not found on item " +
                        $"'{copyInfo.Id}' while modifying '{request.ItemId}'.");
                    continue;
                }

                if (sourceSlot.Properties?.Filters == null)
                {
                    LogError(
                        $"Source slot '{sourceSlotName}' on '{copyInfo.Id}' " +
                        "has no filter collection.");
                    continue;
                }

                /*
                 * Clone only the filters.
                 *
                 * Do not clone the complete source Slot. A full clone can retain
                 * source-specific state, parent information, and property objects.
                 */
                var clonedFilters = cloner
                    .Clone(sourceSlot.Properties.Filters)?
                    .ToList();

                if (clonedFilters == null || clonedFilters.Count == 0)
                {
                    LogError(
                        $"Source slot '{sourceSlotName}' on '{copyInfo.Id}' " +
                        "has no usable filters.");
                    continue;
                }

                AddAdditionalItemsToFirstFilter(
                    clonedFilters,
                    copyInfo.ItemsAddToSlot,
                    request.ItemId,
                    copyInfo.NewSlotName);

                var newSlot = new Slot
                {
                    Name = copyInfo.NewSlotName,

                    // Matches the working CommonCore implementation.
                    Id = MongoId.Empty(),

                    Parent = request.ItemId,

                    Properties = new SlotProperties
                    {
                        Filters = clonedFilters,
                        MaxStackCount = sourceSlot.Properties.MaxStackCount
                    },

                    Required = copyInfo.Required ?? sourceSlot.Required,
                    MaxCount = sourceSlot.MaxCount,
                    MergeSlotWithChildren = sourceSlot.MergeSlotWithChildren,
                    Prototype = sourceSlot.Prototype
                };

                createdSlots.Add(newSlot);

                YATMLogger.LogDebug(
                    $"[SlotCloneHelper] Copied '{sourceSlotName}' from " +
                    $"'{copyInfo.Id}' to '{copyInfo.NewSlotName}' on " +
                    $"'{request.ItemId}'. Filters={clonedFilters.Count}");
            }
            catch (Exception exception)
            {
                LogError(
                    $"Failed while processing a copied slot for '{request.ItemId}': " +
                    $"{exception}");
            }
        }

        if (createdSlots.Count == 0)
        {
            LogError(
                $"No slots were added to '{request.ItemId}'. " +
                "Review the preceding SlotCloneHelper errors.");
            return;
        }

        /*
         * Keep every original slot intact.
         *
         * Do not run the original collection through a final validity filter,
         * because that can remove slots that existed before this helper ran.
         */
        existingSlots.AddRange(createdSlots);
        targetItem.Properties.Slots = existingSlots;

        YATMLogger.Log(
            $"[SlotCloneHelper] Added {createdSlots.Count} slot(s) to " +
            $"'{request.ItemId}'. Total slots: {existingSlots.Count}.");
    }

    private void AddAdditionalItemsToFirstFilter(
        List<SlotFilter> filters,
        IEnumerable<string>? additionalItems,
        string targetItemId,
        string slotName)
    {
        if (additionalItems == null)
        {
            return;
        }

        var firstFilter = filters.FirstOrDefault();

        if (firstFilter == null)
        {
            LogError(
                $"Cannot add extra items to slot '{slotName}' on " +
                $"'{targetItemId}' because its filter list is empty.");
            return;
        }

        firstFilter.Filter ??= new HashSet<MongoId>();

        foreach (var tpl in additionalItems)
        {
            if (string.IsNullOrWhiteSpace(tpl))
            {
                continue;
            }

            if (!MongoId.IsValidMongoId(tpl))
            {
                LogError(
                    $"Ignoring invalid item tpl '{tpl}' for slot " +
                    $"'{slotName}' on '{targetItemId}'.");
                continue;
            }

            firstFilter.Filter.Add(new MongoId(tpl));
        }
    }

    private bool TryGetSlotByName(
        MongoId itemId,
        string slotName,
        out Slot? slot)
    {
        slot = null;

        if (itemId.IsEmpty || string.IsNullOrWhiteSpace(slotName))
        {
            return false;
        }

        var itemDatabase = databaseService.GetItems();

        if (!itemDatabase.TryGetValue(itemId, out var item) ||
            item == null)
        {
            LogError($"Source item '{itemId}' was not found.");
            return false;
        }

        var slots = item.Properties?.Slots;

        if (slots == null)
        {
            LogError($"Source item '{itemId}' has no slot collection.");
            return false;
        }

        slot = slots.FirstOrDefault(candidate =>
            candidate != null &&
            !string.IsNullOrWhiteSpace(candidate.Name) &&
            candidate.Name.Equals(
                slotName,
                StringComparison.OrdinalIgnoreCase));

        return slot != null;
    }

    private static bool SlotExists(
        IEnumerable<Slot> slots,
        string slotName)
    {
        return slots.Any(slot =>
            slot != null &&
            !string.IsNullOrWhiteSpace(slot.Name) &&
            slot.Name.Equals(
                slotName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static void LogError(string message)
    {
        YATMLogger.Log($"[SlotCloneHelper] ERROR: {message}");
    }
}