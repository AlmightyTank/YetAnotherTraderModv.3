using System.Text.Json.Serialization;

namespace YetAnotherTraderMod.src.Models;

/// <summary>
/// Only the YATM properties needed by the slot-copy pass.
/// The rest of each WTT custom-item JSON file is intentionally ignored by System.Text.Json.
/// </summary>
public sealed class YATMItemModificationRequest
{
    [JsonIgnore]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("copySlot")]
    public bool CopySlot { get; set; }

    [JsonPropertyName("copySlotsInfo")]
    public List<CopySlotConfig> CopySlots { get; set; } = [];
}

public sealed class CopySlotConfig
{
    /// <summary>
    /// Source template item containing the slot to copy.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Name the copied slot will have on the target item.
    /// When tgtSlotName is omitted, this is also the source slot name.
    /// </summary>
    [JsonPropertyName("newSlotName")]
    public string NewSlotName { get; set; } = string.Empty;

    /// <summary>
    /// Optional source slot name when it differs from newSlotName.
    /// </summary>
    [JsonPropertyName("tgtSlotName")]
    public string? TgtSlotName { get; set; }

    /// <summary>
    /// Optional item template ids to append to the copied slot's first filter.
    /// </summary>
    [JsonPropertyName("itemsAddToSlot")]
    public string[] ItemsAddToSlot { get; set; } = [];

    /// <summary>
    /// Optional override for the copied slot's Required value.
    /// </summary>
    [JsonPropertyName("required")]
    public bool? Required { get; set; }
}
