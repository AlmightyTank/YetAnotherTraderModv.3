using System.Text.Json.Serialization;

namespace YetAnotherTraderMod.src.Models;

public sealed class YATMItemModificationRequest
{
    [JsonIgnore]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("copySlot")]
    public bool? CopySlot { get; set; }

    [JsonPropertyName("copySlotsInfo")]
    public List<YATMCopySlotInfo>? CopySlotsInfo { get; set; }

    // Optional support in case older files used "extras"
    [JsonPropertyName("extras")]
    public YATMItemModificationExtras? Extras { get; set; }

    public bool ShouldCopySlots =>
        CopySlot == true ||
        Extras?.CopySlot == true;

    public List<YATMCopySlotInfo>? GetCopySlots()
    {
        return CopySlotsInfo
               ?? Extras?.CopySlotsInfo
               ?? Extras?.CopySlots;
    }
}

public sealed class YATMItemModificationExtras
{
    [JsonPropertyName("copySlot")]
    public bool? CopySlot { get; set; }

    [JsonPropertyName("copySlotsInfo")]
    public List<YATMCopySlotInfo>? CopySlotsInfo { get; set; }

    [JsonPropertyName("copySlots")]
    public List<YATMCopySlotInfo>? CopySlots { get; set; }
}

public sealed class YATMCopySlotInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("newSlotName")]
    public string? NewSlotName { get; set; }

    [JsonPropertyName("tgtSlotName")]
    public string? TgtSlotName { get; set; }

    [JsonPropertyName("itemsAddToSlot")]
    public string[]? ItemsAddToSlot { get; set; }

    [JsonPropertyName("required")]
    public bool? Required { get; set; }
}