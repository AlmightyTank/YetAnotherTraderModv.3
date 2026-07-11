using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace YetAnotherTraderMod.src.Services;

/// <summary>
/// Prevents JsonExtensionData from repeating properties that are already emitted by
/// an SPT model. For example, Item.Template is serialized as "_tpl". Keeping a second
/// "_tpl" entry in Item.ExtensionData produces invalid JSON with duplicate keys.
/// </summary>
public static class YATMJsonExtensionDataSanitizer
{
    private static readonly (string CanonicalJsonName, string[] Aliases)[] KnownAliasGroups =
    [
        ("_id", ["_id", "id", "Id"]),
        ("_tpl", ["_tpl", "tpl", "Tpl", "Template", "TemplateId"]),
        ("parentId", ["parentId", "ParentId"]),
        ("slotId", ["slotId", "SlotId"]),
        ("target", ["target", "Target"]),
        ("traderId", ["traderId", "TraderId"]),
        ("items", ["items", "Items"]),
        ("barter_scheme", ["barter_scheme", "BarterScheme"]),
        ("loyal_level_items", ["loyal_level_items", "LoyalLevelItems"]),
        ("count", ["count", "Count"])
    ];

    public static bool IsDeclaredSerializedMember(object target, string key)
    {
        return IsDeclaredSerializedMember(target.GetType(), key);
    }

    public static bool IsDeclaredSerializedMember(Type type, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return GetSerializableMembers(type).Any(member =>
        {
            var jsonName = member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? member.Name;
            return key.Equals(jsonName, StringComparison.OrdinalIgnoreCase)
                   || key.Equals(member.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    public static void SanitizeObject(object? target)
    {
        if (target == null || GetExtensionData(target) is not IDictionary dictionary || dictionary.Count == 0)
        {
            return;
        }

        var type = target.GetType();
        var serializedNames = GetSerializableMembers(type)
            .Select(member => member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? member.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var memberNames = GetSerializableMembers(type)
            .Select(member => member.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var keysToRemove = new HashSet<object>();

        foreach (DictionaryEntry entry in dictionary)
        {
            var key = entry.Key?.ToString();
            if (!string.IsNullOrWhiteSpace(key)
                && (serializedNames.Contains(key) || memberNames.Contains(key)))
            {
                keysToRemove.Add(entry.Key!);
            }
        }

        foreach (var (canonicalJsonName, aliases) in KnownAliasGroups)
        {
            if (!serializedNames.Contains(canonicalJsonName))
            {
                continue;
            }

            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString();
                if (!string.IsNullOrWhiteSpace(key)
                    && aliases.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    keysToRemove.Add(entry.Key!);
                }
            }
        }

        foreach (var key in keysToRemove)
        {
            dictionary.Remove(key);
        }
    }

    public static void RemoveExtensionKey(object? target, string key)
    {
        RemoveExtensionKeys(target, [key]);
    }

    public static void RemoveExtensionKeys(object? target, IEnumerable<string> keys)
    {
        if (target == null || GetExtensionData(target) is not IDictionary dictionary || dictionary.Count == 0)
        {
            return;
        }

        var keySet = keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (keySet.Count == 0)
        {
            return;
        }

        var keysToRemove = new List<object>();
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key != null && keySet.Contains(entry.Key.ToString() ?? string.Empty))
            {
                keysToRemove.Add(entry.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            dictionary.Remove(key);
        }
    }

    public static void SanitizeAssort(TraderAssort? assort)
    {
        if (assort == null)
        {
            return;
        }

        SanitizeObject(assort);

        if (assort.Items != null)
        {
            foreach (var item in assort.Items)
            {
                SanitizeObject(item);
                SanitizeObject(item?.Upd);
            }
        }

        if (assort.BarterScheme == null)
        {
            return;
        }

        foreach (var paymentOptions in assort.BarterScheme.Values)
        {
            if (paymentOptions == null)
            {
                continue;
            }

            foreach (var paymentOption in paymentOptions)
            {
                if (paymentOption == null)
                {
                    continue;
                }

                foreach (var paymentComponent in paymentOption)
                {
                    SanitizeObject(paymentComponent);
                }
            }
        }
    }

    private static IEnumerable<MemberInfo> GetSerializableMembers(Type type)
    {
        return type
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(member => member is PropertyInfo or FieldInfo)
            .Where(member => member.GetCustomAttribute<JsonExtensionDataAttribute>() == null)
            .Where(member => member.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .Where(member => !member.Name.Equals("ExtensionData", StringComparison.OrdinalIgnoreCase));
    }

    private static object? GetExtensionData(object target)
    {
        var type = target.GetType();

        return type.GetProperty(
                   "ExtensionData",
                   BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
               ?.GetValue(target)
            ?? type.GetField(
                   "ExtensionData",
                   BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
               ?.GetValue(target);
    }
}
