using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace YetAnotherTraderMod.src.Models;

/// <summary>
/// A reusable full weapon item tree that is not registered in SPT's global preset table.
///
/// Preferred JSON format:
/// {
///   "BUILD_ID": {
///     "name": "Build name",
///     "id": "BUILD_ID",
///     "parentId": "SOURCE_ROOT_ITEM_ID",
///     "encyclopedia": "ROOT_WEAPON_TPL",
///     "items": [ ... ]
///   }
/// }
///
/// rootItemId/weaponTpl remain accepted as backwards-compatible aliases.
/// </summary>
public sealed class YATMWeaponBuildDefinition
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Optional duplicate of the dictionary's outer build key.
    /// When present, it must match the outer key.
    /// </summary>
    [JsonPropertyName("id")]
    public string? DeclaredId { get; set; }

    /// <summary>
    /// Preferred field: source _id of the root weapon row inside items[].
    /// This follows the layout used by exported weapon-build data.
    /// </summary>
    [JsonPropertyName("parentId")]
    public string? ParentId { get; set; }

    /// <summary>
    /// Preferred field: template id of the root weapon.
    /// It must match the root item's _tpl when supplied.
    /// </summary>
    [JsonPropertyName("encyclopedia")]
    public string? Encyclopedia { get; set; }

    /// <summary>
    /// Backwards-compatible alias for parentId.
    /// </summary>
    [JsonPropertyName("rootItemId")]
    public string? RootItemId { get; set; }

    /// <summary>
    /// Backwards-compatible alias for encyclopedia.
    /// </summary>
    [JsonPropertyName("weaponTpl")]
    public string? WeaponTpl { get; set; }

    [JsonPropertyName("items")]
    public List<Item> Items { get; set; } = [];

    [JsonIgnore]
    public string BuildId { get; set; } = string.Empty;

    [JsonIgnore]
    public string SourceFile { get; set; } = string.Empty;

    [JsonIgnore]
    public string? EffectiveRootItemId =>
        FirstNonEmpty(ParentId, RootItemId);

    [JsonIgnore]
    public string? EffectiveWeaponTpl =>
        FirstNonEmpty(Encyclopedia, WeaponTpl);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}

/// <summary>
/// Resolved immutable view of a registered weapon build.
/// Source item ids are reference ids only and are always replaced before use.
/// </summary>
public sealed record YATMResolvedWeaponBuild(
    string BuildId,
    string SourceFile,
    string RootTemplateId,
    Item RootItem,
    IReadOnlyList<Item> Items);
