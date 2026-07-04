using System.Security.Cryptography;
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
            LogError("Request is null");
            return;
        }

        if (!request.ShouldCopySlots)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.ItemId))
        {
            LogError("Request.ItemId is null or empty");
            return;
        }

        var copySlots = request.GetCopySlots();

        if (copySlots == null || copySlots.Count == 0)
        {
            LogError($"Invalid copySlotsInfo for {request.ItemId}");
            return;
        }

        var itemDb = databaseService.GetItems();

        if (!itemDb.TryGetValue(request.ItemId, out var targetItem) || targetItem == null)
        {
            LogError($"Target item {request.ItemId} not found.");
            return;
        }

        targetItem.Properties ??= new TemplateItemProperties();
        targetItem.Properties.Slots ??= [];

        var existingSlots = targetItem.Properties.Slots.ToList();
        var createdSlots = new List<Slot>();

        foreach (var copyInfo in copySlots)
        {
            if (copyInfo == null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(copyInfo.Id))
            {
                LogError($"Source item id missing for {request.ItemId}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(copyInfo.NewSlotName))
            {
                LogError($"NewSlotName missing for {request.ItemId}");
                continue;
            }

            var sourceSlotName = string.IsNullOrWhiteSpace(copyInfo.TgtSlotName)
                ? copyInfo.NewSlotName
                : copyInfo.TgtSlotName;

            if (SlotExists(existingSlots, copyInfo.NewSlotName) ||
                SlotExists(createdSlots, copyInfo.NewSlotName))
            {
                LogError($"Slot '{copyInfo.NewSlotName}' already exists on {request.ItemId}, skipping.");
                continue;
            }

            if (!TryGetSlotByName(copyInfo.Id, sourceSlotName, out var sourceSlot) || sourceSlot == null)
            {
                LogError($"Slot '{sourceSlotName}' of id '{copyInfo.Id}' not found when adding to {request.ItemId}");
                continue;
            }

            var clonedSlot = cloner.Clone(sourceSlot);

            if (clonedSlot == null)
            {
                LogError($"Failed to clone full slot '{sourceSlotName}' from '{copyInfo.Id}'");
                continue;
            }

            if (clonedSlot.Properties == null)
            {
                LogError($"Cloned slot '{sourceSlotName}' from '{copyInfo.Id}' has null Properties");
                continue;
            }

            var clonedFilters = cloner.Clone(sourceSlot.Properties.Filters ?? []);

            if (clonedFilters == null)
            {
                LogError($"Failed to clone filters for slot '{sourceSlotName}' from '{copyInfo.Id}'");
                continue;
            }

            if (copyInfo.ItemsAddToSlot != null && copyInfo.ItemsAddToSlot.Length > 0)
            {
                var firstFilter = clonedFilters.FirstOrDefault();

                if (firstFilter?.Filter != null)
                {
                    foreach (var tpl in copyInfo.ItemsAddToSlot)
                    {
                        if (string.IsNullOrWhiteSpace(tpl))
                        {
                            continue;
                        }

                        if (!firstFilter.Filter.Contains(tpl))
                        {
                            firstFilter.Filter.Add(tpl);
                        }
                    }
                }
            }

            clonedSlot.Name = copyInfo.NewSlotName;
            clonedSlot.Parent = request.ItemId;
            clonedSlot.Id = new MongoId(GenerateMongoId());
            clonedSlot.Required = copyInfo.Required ?? sourceSlot.Required;
            clonedSlot.Properties.Filters = clonedFilters;

            if (!IsValidSlot(clonedSlot, request.ItemId))
            {
                continue;
            }

            createdSlots.Add(clonedSlot);

            YATMLogger.LogDebug(
                $"[SlotCloneHelper] Copied slot '{sourceSlotName}' from '{copyInfo.Id}' to '{copyInfo.NewSlotName}' on {request.ItemId}");
        }

        existingSlots.AddRange(createdSlots);

        targetItem.Properties.Slots = existingSlots
            .Where(x => x != null && IsValidFinalSlot(x))
            .ToList();
    }

    private bool TryGetSlotByName(string itemId, string slotName, out Slot? slot)
    {
        slot = null;

        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(slotName))
        {
            return false;
        }

        if (!databaseService.GetItems().TryGetValue(itemId, out var item) || item == null)
        {
            return false;
        }

        var slots = item.Properties?.Slots;

        if (slots == null)
        {
            return false;
        }

        foreach (var s in slots)
        {
            if (s != null &&
                !string.IsNullOrWhiteSpace(s.Name) &&
                s.Name.Equals(slotName, StringComparison.OrdinalIgnoreCase))
            {
                slot = s;
                return true;
            }
        }

        return false;
    }

    private static bool SlotExists(IEnumerable<Slot> slots, string slotName)
    {
        return slots.Any(x =>
            x != null &&
            !string.IsNullOrWhiteSpace(x.Name) &&
            x.Name.Equals(slotName, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsValidSlot(Slot slot, string targetItemId)
    {
        if (slot == null)
        {
            LogError($"Null slot generated for {targetItemId}");
            return false;
        }

        if (string.IsNullOrWhiteSpace(slot.Name))
        {
            LogError($"Generated slot has null/empty Name for {targetItemId}");
            return false;
        }

        if (slot.Properties == null)
        {
            LogError($"Generated slot '{slot.Name}' has null Properties for {targetItemId}");
            return false;
        }

        slot.Properties.Filters ??= [];

        return true;
    }

    private static bool IsValidFinalSlot(Slot? slot)
    {
        return slot != null &&
               !string.IsNullOrWhiteSpace(slot.Name) &&
               slot.Properties != null;
    }

    private static string GenerateMongoId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    }

    private static void LogError(string message)
    {
        YATMLogger.Log($"[SlotCloneHelper] ERROR: {message}");
    }
}