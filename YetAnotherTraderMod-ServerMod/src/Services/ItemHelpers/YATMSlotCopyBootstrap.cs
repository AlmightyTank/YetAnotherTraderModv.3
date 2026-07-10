using System.Reflection;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using YetAnotherTraderMod.src.Models;

namespace YetAnotherTraderMod.src.Services.ItemHelpers;

[Injectable]
public sealed class YATMSlotCopyBootstrap(YATMSlotCloneHelper slotCloneHelper)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task ProcessSlotCopies(Assembly assembly, string relativePath)
    {
        var modPath = Path.GetDirectoryName(assembly.Location)
            ?? throw new InvalidOperationException("Could not resolve mod path.");

        var fullPath = Path.Combine(modPath, relativePath);

        if (!Directory.Exists(fullPath))
        {
            YATMLogger.LogDebug($"[SlotCopyBootstrap] Folder not found, skipping: {fullPath}");
            return;
        }

        var files = Directory.GetFiles(fullPath, "*.json", SearchOption.AllDirectories);

        if (files.Length == 0)
        {
            YATMLogger.LogDebug($"[SlotCopyBootstrap] No JSON files found in: {fullPath}");
            return;
        }

        foreach (var file in files)
        {
            await ProcessFile(file);
        }
    }

    private async Task ProcessFile(string file)
    {
        try
        {
            var json = await File.ReadAllTextAsync(file);

            var requests = JsonSerializer.Deserialize<Dictionary<string, YATMItemModificationRequest>>(
                json,
                JsonOptions);

            if (requests == null || requests.Count == 0)
            {
                return;
            }

            foreach (var pair in requests)
            {
                var itemId = pair.Key;
                var request = pair.Value;

                if (request == null)
                {
                    continue;
                }

                request.ItemId = itemId;

                if (!request.CopySlot)
                {
                    continue;
                }

                slotCloneHelper.Process(request);
            }
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[SlotCopyBootstrap] Failed to process slot copies in file '{file}': {ex}");
            YATMLogger.LogDebug(ex.StackTrace ?? "No stack trace");
        }
    }
}