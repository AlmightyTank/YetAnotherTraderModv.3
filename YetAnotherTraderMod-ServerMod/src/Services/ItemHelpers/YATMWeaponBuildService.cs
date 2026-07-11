using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Utils;
using YetAnotherTraderMod.src.Models;
using Path = System.IO.Path;

namespace YetAnotherTraderMod.src.Services.ItemHelpers;

/// <summary>
/// Loads reusable full weapon trees from db/CustomWeaponBuilds.
/// These builds are deliberately not added to Globals.ItemPresets.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class YATMWeaponBuildService(JsonUtil jsonUtil)
{
    private readonly Dictionary<string, YATMResolvedWeaponBuild> _builds =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<YATMResolvedWeaponBuild>> _buildsByRootTpl =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count => _builds.Count;

    public async Task LoadWeaponBuilds(Assembly assembly, string relativePath)
    {
        var modPath = Path.GetDirectoryName(assembly.Location)
            ?? throw new InvalidOperationException("Could not resolve mod path.");

        var fullPath = Path.Combine(modPath, relativePath);
        if (!Directory.Exists(fullPath))
        {
            YATMLogger.LogDebug($"[WeaponBuilds] Folder not found: {fullPath}");
            return;
        }

        var files = Directory
            .EnumerateFiles(fullPath, "*.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(fullPath, "*.jsonc", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var loaded = 0;
        foreach (var file in files)
        {
            loaded += await LoadFile(file);
        }

        YATMLogger.Log($"[WeaponBuilds] Loaded {loaded} reusable full weapon build(s) from {relativePath}.");
    }

    public bool TryGetBuild(string? buildId, string? expectedRootTpl, out YATMResolvedWeaponBuild build)
    {
        build = null!;
        if (string.IsNullOrWhiteSpace(buildId)
            || !_builds.TryGetValue(buildId.Trim(), out var resolved))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedRootTpl)
            && !resolved.RootTemplateId.Equals(expectedRootTpl, StringComparison.OrdinalIgnoreCase))
        {
            YATMLogger.Log(
                $"[WeaponBuilds] Build '{resolved.BuildId}' root tpl {resolved.RootTemplateId} does not match expected weapon tpl {expectedRootTpl}.");
            return false;
        }

        build = resolved;
        return true;
    }

    /// <summary>
    /// Automatically resolves a build when exactly one registered build uses the weapon tpl.
    /// Multiple builds require an explicit weaponBuildId in JSON.
    /// </summary>
    public bool TryGetUniqueBuildForWeaponTpl(string weaponTpl, out YATMResolvedWeaponBuild build)
    {
        build = null!;
        if (string.IsNullOrWhiteSpace(weaponTpl)
            || !_buildsByRootTpl.TryGetValue(weaponTpl, out var matches)
            || matches.Count != 1)
        {
            return false;
        }

        build = matches[0];
        return true;
    }

    private async Task<int> LoadFile(string file)
    {
        try
        {
            var json = await File.ReadAllTextAsync(file);
            var definitions = jsonUtil.Deserialize<Dictionary<string, YATMWeaponBuildDefinition>>(json);
            if (definitions == null || definitions.Count == 0)
            {
                return 0;
            }

            var loaded = 0;
            foreach (var (buildId, definition) in definitions)
            {
                if (TryRegisterBuild(buildId, definition, file))
                {
                    loaded++;
                }
            }

            return loaded;
        }
        catch (Exception ex)
        {
            YATMLogger.Log($"[WeaponBuilds] Failed to load '{file}': {ex.Message}");
            return 0;
        }
    }

    private bool TryRegisterBuild(string buildId, YATMWeaponBuildDefinition? definition, string file)
    {
        if (string.IsNullOrWhiteSpace(buildId) || definition?.Items is not { Count: > 0 })
        {
            YATMLogger.Log($"[WeaponBuilds] Skipped an empty build in '{file}'.");
            return false;
        }

        buildId = buildId.Trim();

        if (!string.IsNullOrWhiteSpace(definition.DeclaredId)
            && !definition.DeclaredId.Trim().Equals(buildId, StringComparison.OrdinalIgnoreCase))
        {
            YATMLogger.Log(
                $"[WeaponBuilds] Build outer key '{buildId}' does not match its id field '{definition.DeclaredId}' in '{file}'.");
            return false;
        }

        if (_builds.ContainsKey(buildId))
        {
            YATMLogger.Log($"[WeaponBuilds] Duplicate build id '{buildId}' in '{file}'. The first definition is kept.");
            return false;
        }

        var duplicateItemId = definition.Items
            .GroupBy(x => x.Id.ToString(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1)?.Key;

        if (!string.IsNullOrWhiteSpace(duplicateItemId))
        {
            YATMLogger.Log($"[WeaponBuilds] Build '{buildId}' contains duplicate source item id {duplicateItemId}.");
            return false;
        }

        var itemIds = definition.Items
            .Select(x => x.Id.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var configuredRootItemId = definition.EffectiveRootItemId;

        Item? root;
        if (!string.IsNullOrWhiteSpace(configuredRootItemId))
        {
            root = definition.Items.FirstOrDefault(x =>
                x.Id.ToString().Equals(configuredRootItemId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var roots = definition.Items.Where(x =>
            {
                var parentId = x.ParentId?.ToString();
                return string.IsNullOrWhiteSpace(parentId)
                    || parentId.Equals("hideout", StringComparison.OrdinalIgnoreCase)
                    || !itemIds.Contains(parentId);
            }).ToList();

            root = roots.Count == 1 ? roots[0] : null;
        }

        if (root == null)
        {
            YATMLogger.Log(
                $"[WeaponBuilds] Build '{buildId}' does not have one resolvable root item. Set parentId to the root row's _id.");
            return false;
        }

        var rootTpl = root.Template.ToString();
        var configuredWeaponTpl = definition.EffectiveWeaponTpl;
        if (!string.IsNullOrWhiteSpace(configuredWeaponTpl)
            && !configuredWeaponTpl.Equals(rootTpl, StringComparison.OrdinalIgnoreCase))
        {
            YATMLogger.Log(
                $"[WeaponBuilds] Build '{buildId}' encyclopedia {configuredWeaponTpl} does not match root _tpl {rootTpl}.");
            return false;
        }

        definition.BuildId = buildId;
        definition.SourceFile = file;
        definition.DeclaredId ??= buildId;
        definition.ParentId = root.Id.ToString();
        definition.Encyclopedia = rootTpl;
        definition.RootItemId ??= definition.ParentId;
        definition.WeaponTpl ??= definition.Encyclopedia;

        var resolved = new YATMResolvedWeaponBuild(
            buildId,
            file,
            rootTpl,
            root,
            definition.Items);

        _builds.Add(buildId, resolved);
        if (!_buildsByRootTpl.TryGetValue(rootTpl, out var rootBuilds))
        {
            rootBuilds = [];
            _buildsByRootTpl[rootTpl] = rootBuilds;
        }

        rootBuilds.Add(resolved);
        YATMLogger.LogDebug(
            $"[WeaponBuilds] Registered '{buildId}' ({definition.Items.Count} item rows, root tpl {rootTpl}) from {file}.");
        return true;
    }
}
