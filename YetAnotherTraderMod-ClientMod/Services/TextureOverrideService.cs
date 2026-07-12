using BepInEx.Logging;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using YetAnotherTraderMod.Client.Models;

namespace YetAnotherTraderMod.Client.Services
{
    public sealed class TextureOverrideService
    {
        private const string ConfigFileName = "texture_overrides.json";
        private const string DefaultTexturesDirectoryName = "DefaultTextures";
        private static readonly TimeSpan PendingRequestLifetime = TimeSpan.FromMinutes(2);

        public static TextureOverrideService Instance { get; private set; }

        private readonly ManualLogSource _logger;
        private readonly string _pluginDirectory;
        private readonly TextureOverrideConfig _config;
        private readonly bool _hasEnabledRules;

        private readonly object _pendingLock = new object();
        private readonly Dictionary<ResourceKey, Queue<PendingTextureRequest>> _pendingByPrefab
            = new Dictionary<ResourceKey, Queue<PendingTextureRequest>>();

        private readonly Dictionary<int, AppliedObjectState> _appliedByInstanceId
            = new Dictionary<int, AppliedObjectState>();

        private readonly Dictionary<string, Texture2D> _textureCache
            = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<Renderer> _patchedRenderers = new HashSet<Renderer>();
        private readonly HashSet<string> _exportedDefaultFolders
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private TextureOverrideService(ManualLogSource logger)
        {
            _logger = logger;
            _pluginDirectory = PluginPathService.PluginDirectory;
            _config = LoadConfig();
            _hasEnabledRules = _config.Enabled
                && _config.Overrides != null
                && _config.Overrides.Any(pair => pair.Value != null && pair.Value.Enabled);
        }

        public static void Initialize(ManualLogSource logger)
        {
            Instance = new TextureOverrideService(logger);

            int enabledCount = Instance._config.Overrides == null
                ? 0
                : Instance._config.Overrides.Count(pair => pair.Value != null && pair.Value.Enabled);

            logger.LogInfo($"[YATM Textures] Loaded {enabledCount} enabled template override(s).");
        }

        public void OnCreateItem(Item item, bool isAnimated)
        {
            if (!_hasEnabledRules || item == null)
            {
                return;
            }

            string templateId = item.StringTemplateId ?? string.Empty;
            TextureOverrideRule rule = null;

            if (_config.Overrides != null
                && _config.Overrides.TryGetValue(templateId, out TextureOverrideRule configuredRule)
                && configuredRule != null
                && configuredRule.Enabled)
            {
                rule = configuredRule;
            }

            // Queue every item while the feature is active. Custom clones and vanilla items can
            // share the same ResourceKey, so no-op entries keep the prefab queue aligned.
            lock (_pendingLock)
            {
                if (!_pendingByPrefab.TryGetValue(item.Prefab, out Queue<PendingTextureRequest> queue))
                {
                    queue = new Queue<PendingTextureRequest>();
                    _pendingByPrefab.Add(item.Prefab, queue);
                }

                queue.Enqueue(new PendingTextureRequest(templateId, rule, DateTime.UtcNow, isAnimated));
            }
        }

        public void OnCreatedItemGameObject(ResourceKey resourceKey, GameObject gameObject)
        {
            if (!_hasEnabledRules || gameObject == null)
            {
                return;
            }

            PendingTextureRequest request = DequeuePendingRequest(resourceKey);
            if (request == null)
            {
                return;
            }

            AssetPoolObject assetPoolObject = FindAssetPoolObject(gameObject);
            if (assetPoolObject == null)
            {
                if (request.Rule != null)
                {
                    _logger.LogWarning(
                        $"[YATM Textures] No AssetPoolObject found for custom TPL "
                        + $"{request.TemplateId}, animated={request.IsAnimated}.");
                }

                return;
            }

            // Always clear a previous YATM state before this pooled object is assigned again.
            Restore(assetPoolObject);

            if (request.Rule == null)
            {
                return;
            }

            IReadOnlyList<Renderer> renderers = GetRenderers(assetPoolObject);

            if (_config.ExportDefaultTextures)
            {
                ExportDefaultTextures(
                    request.TemplateId,
                    request.Rule,
                    assetPoolObject,
                    renderers,
                    request.IsAnimated);
            }

            ApplyOverride(
                request.TemplateId,
                request.Rule,
                assetPoolObject,
                renderers,
                request.IsAnimated);
        }

        public void Restore(AssetPoolObject assetPoolObject)
        {
            if (assetPoolObject == null || assetPoolObject.gameObject == null)
            {
                return;
            }

            int instanceId = assetPoolObject.gameObject.GetInstanceID();
            if (!_appliedByInstanceId.TryGetValue(instanceId, out AppliedObjectState appliedState))
            {
                return;
            }

            _appliedByInstanceId.Remove(instanceId);

            foreach (SavedMaterialState saved in appliedState.Materials)
            {
                if (saved.Renderer == null)
                {
                    continue;
                }

                saved.Renderer.SetPropertyBlock(saved.OriginalPropertyBlock, saved.MaterialIndex);
            }

            foreach (Renderer renderer in appliedState.Renderers)
            {
                if (renderer != null)
                {
                    _patchedRenderers.Remove(renderer);
                }
            }
        }

        public bool IsPatchedRenderer(Renderer renderer)
        {
            return renderer != null && _patchedRenderers.Contains(renderer);
        }

        private PendingTextureRequest DequeuePendingRequest(ResourceKey resourceKey)
        {
            lock (_pendingLock)
            {
                if (!_pendingByPrefab.TryGetValue(resourceKey, out Queue<PendingTextureRequest> queue))
                {
                    return null;
                }

                DateTime cutoff = DateTime.UtcNow - PendingRequestLifetime;
                while (queue.Count > 0 && queue.Peek().CreatedUtc < cutoff)
                {
                    PendingTextureRequest stale = queue.Dequeue();
                    _logger.LogWarning(
                        $"[YATM Textures] Dropped stale prefab request for TPL {stale.TemplateId}.");
                }

                if (queue.Count == 0)
                {
                    _pendingByPrefab.Remove(resourceKey);
                    return null;
                }

                PendingTextureRequest request = queue.Dequeue();
                if (queue.Count == 0)
                {
                    _pendingByPrefab.Remove(resourceKey);
                }

                return request;
            }
        }

        private void ApplyOverride(
            string customTemplateId,
            TextureOverrideRule rule,
            AssetPoolObject assetPoolObject,
            IReadOnlyList<Renderer> renderers,
            bool isAnimated)
        {
            Dictionary<string, string> configuredTextures = BuildTextureMap(rule);
            if (configuredTextures.Count == 0)
            {
                _logger.LogWarning(
                    $"[YATM Textures] TPL {customTemplateId} has no configured texture files.");
                return;
            }

            Dictionary<string, Texture2D> loadedTextures
                = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, string> pair in configuredTextures)
            {
                Texture2D texture = LoadTexture(pair.Value);
                if (texture != null)
                {
                    loadedTextures[pair.Key] = texture;
                }
            }

            if (loadedTextures.Count == 0)
            {
                return;
            }

            var savedMaterials = new List<SavedMaterialState>();
            var touchedRenderers = new HashSet<Renderer>();
            int changedSlots = 0;

            foreach (Renderer renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                Material[] materials = renderer.sharedMaterials;
                for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    Material material = materials[materialIndex];
                    if (material == null || !MatchesMaterial(material.name, rule.Materials))
                    {
                        continue;
                    }

                    var applicableTextures = new List<KeyValuePair<string, Texture2D>>();
                    foreach (KeyValuePair<string, Texture2D> pair in loadedTextures)
                    {
                        if (material.HasProperty(pair.Key))
                        {
                            applicableTextures.Add(pair);
                        }
                    }

                    Color parsedColor = default(Color);
                    bool applyColor = !string.IsNullOrWhiteSpace(rule.Color)
                        && !string.IsNullOrWhiteSpace(rule.ColorProperty)
                        && material.HasProperty(rule.ColorProperty)
                        && ColorUtility.TryParseHtmlString(rule.Color, out parsedColor);

                    if (applicableTextures.Count == 0 && !applyColor)
                    {
                        continue;
                    }

                    var originalBlock = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(originalBlock, materialIndex);

                    var updatedBlock = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(updatedBlock, materialIndex);

                    foreach (KeyValuePair<string, Texture2D> pair in applicableTextures)
                    {
                        int texturePropertyId = Shader.PropertyToID(pair.Key);
                        updatedBlock.SetTexture(texturePropertyId, pair.Value);

                        string transformProperty = pair.Key + "_ST";
                        if (material.HasProperty(transformProperty))
                        {
                            updatedBlock.SetVector(
                                Shader.PropertyToID(transformProperty),
                                new Vector4(rule.ScaleX, rule.ScaleY, rule.OffsetX, rule.OffsetY));
                        }
                    }

                    if (applyColor)
                    {
                        updatedBlock.SetColor(Shader.PropertyToID(rule.ColorProperty), parsedColor);
                    }

                    renderer.SetPropertyBlock(updatedBlock, materialIndex);

                    savedMaterials.Add(new SavedMaterialState(renderer, materialIndex, originalBlock));
                    touchedRenderers.Add(renderer);
                    _patchedRenderers.Add(renderer);
                    changedSlots++;
                }
            }

            if (changedSlots == 0)
            {
                string requestedMaterials = rule.Materials == null
                    ? "*"
                    : string.Join(", ", rule.Materials);

                _logger.LogWarning(
                    $"[YATM Textures] No matching material slots for TPL {customTemplateId}. "
                    + $"Requested: {requestedMaterials}. Check DefaultTextures manifest.json.");
                return;
            }

            int instanceId = assetPoolObject.gameObject.GetInstanceID();
            _appliedByInstanceId[instanceId] = new AppliedObjectState(savedMaterials, touchedRenderers);

            string context = isAnimated ? "Animated" : "Standard";
            _logger.LogInfo(
                $"[YATM Textures] Applied {context} texture override to TPL {customTemplateId} "
                + $"on {changedSlots} material slot(s).");
        }

        private void ExportDefaultTextures(
            string customTemplateId,
            TextureOverrideRule rule,
            AssetPoolObject assetPoolObject,
            IReadOnlyList<Renderer> renderers,
            bool isAnimated)
        {
            string sourceTemplateId = string.IsNullOrWhiteSpace(rule.SourceTemplateId)
                ? customTemplateId
                : rule.SourceTemplateId.Trim();

            string safeSourceTemplateId = MakeSafeFileName(sourceTemplateId);
            string contextDirectory = isAnimated ? "Animated" : "Standard";
            string outputDirectory = Path.Combine(
                _pluginDirectory,
                DefaultTexturesDirectoryName,
                safeSourceTemplateId,
                contextDirectory);

            string manifestPath = Path.Combine(outputDirectory, "manifest.json");

            if (!_config.OverwriteDefaultTextures
                && (File.Exists(manifestPath) || _exportedDefaultFolders.Contains(outputDirectory)))
            {
                _exportedDefaultFolders.Add(outputDirectory);
                return;
            }

            try
            {
                Directory.CreateDirectory(outputDirectory);

                var manifest = new DefaultTextureManifest
                {
                    CustomTemplateId = customTemplateId,
                    SourceTemplateId = sourceTemplateId,
                    Context = isAnimated ? "Animated" : "Standard",
                    ExportedUtc = DateTime.UtcNow.ToString("O")
                };

                string[] properties = _config.ExportTextureProperties ?? Array.Empty<string>();

                for (int rendererIndex = 0; rendererIndex < renderers.Count; rendererIndex++)
                {
                    Renderer renderer = renderers[rendererIndex];
                    if (renderer == null)
                    {
                        continue;
                    }

                    Material[] materials = renderer.sharedMaterials;
                    for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                    {
                        Material material = materials[materialIndex];
                        if (material == null)
                        {
                            continue;
                        }

                        var entry = new DefaultTextureMaterialEntry
                        {
                            RendererPath = GetTransformPath(assetPoolObject.transform, renderer.transform),
                            RendererIndex = rendererIndex,
                            MaterialIndex = materialIndex,
                            MaterialName = NormalizeMaterialName(material.name),
                            ShaderName = material.shader == null ? "Unknown" : material.shader.name
                        };

                        foreach (string propertyName in properties)
                        {
                            if (string.IsNullOrWhiteSpace(propertyName)
                                || !material.HasProperty(propertyName))
                            {
                                continue;
                            }

                            Texture texture = material.GetTexture(propertyName);
                            if (texture == null)
                            {
                                continue;
                            }

                            string fileName = string.Format(
                                "r{0:D2}_m{1:D2}_{2}_{3}.png",
                                rendererIndex,
                                materialIndex,
                                MakeSafeFileName(NormalizeMaterialName(material.name)),
                                MakeSafeFileName(propertyName.TrimStart('_')));

                            string outputPath = Path.Combine(outputDirectory, fileName);
                            if (_config.OverwriteDefaultTextures || !File.Exists(outputPath))
                            {
                                byte[] png = ConvertTextureToPng(texture);
                                if (png != null && png.Length > 0)
                                {
                                    File.WriteAllBytes(outputPath, png);
                                }
                            }

                            if (File.Exists(outputPath))
                            {
                                entry.Textures[propertyName] = fileName;
                            }
                        }

                        manifest.Materials.Add(entry);
                    }
                }

                File.WriteAllText(
                    manifestPath,
                    JsonConvert.SerializeObject(manifest, Formatting.Indented));

                _exportedDefaultFolders.Add(outputDirectory);
                _logger.LogInfo(
                    $"[YATM Textures] Saved {(isAnimated ? "animated" : "standard")} "
                    + $"default textures for {sourceTemplateId} to {outputDirectory}");
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    $"[YATM Textures] Failed exporting defaults for TPL {customTemplateId}: {exception}");
            }
        }

        private TextureOverrideConfig LoadConfig()
        {
            string configPath = PluginPathService.GetPluginFilePath(ConfigFileName);

            if (!File.Exists(configPath))
            {
                _logger.LogWarning($"[YATM Textures] Config not found: {configPath}");
                return new TextureOverrideConfig { Enabled = false };
            }

            try
            {
                string json = File.ReadAllText(configPath);
                TextureOverrideConfig config
                    = JsonConvert.DeserializeObject<TextureOverrideConfig>(json)
                      ?? new TextureOverrideConfig { Enabled = false };

                config.Overrides ??= new Dictionary<string, TextureOverrideRule>(
                    StringComparer.OrdinalIgnoreCase);

                return config;
            }
            catch (Exception exception)
            {
                _logger.LogError($"[YATM Textures] Failed loading {configPath}: {exception}");
                return new TextureOverrideConfig { Enabled = false };
            }
        }

        private Dictionary<string, string> BuildTextureMap(TextureOverrideRule rule)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (rule.Textures != null)
            {
                foreach (KeyValuePair<string, string> pair in rule.Textures)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key)
                        && !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        result[pair.Key.Trim()] = pair.Value.Trim();
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(rule.Texture))
            {
                string propertyName = string.IsNullOrWhiteSpace(rule.TextureProperty)
                    ? "_MainTex"
                    : rule.TextureProperty.Trim();

                result[propertyName] = rule.Texture.Trim();
            }

            return result;
        }

        private Texture2D LoadTexture(string relativePath)
        {
            string fullPath;
            try
            {
                fullPath = ResolvePluginPath(relativePath);
            }
            catch (Exception exception)
            {
                _logger.LogError($"[YATM Textures] Invalid texture path '{relativePath}': {exception.Message}");
                return null;
            }

            if (_textureCache.TryGetValue(fullPath, out Texture2D cached))
            {
                return cached;
            }

            if (!File.Exists(fullPath))
            {
                _logger.LogError($"[YATM Textures] Texture file not found: {fullPath}");
                return null;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, false)
                {
                    name = "YATM_" + Path.GetFileNameWithoutExtension(fullPath),
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear,
                    anisoLevel = 1
                };

                if (!ImageConversion.LoadImage(texture, bytes, false))
                {
                    UnityEngine.Object.Destroy(texture);
                    _logger.LogError($"[YATM Textures] Unity could not decode: {fullPath}");
                    return null;
                }

                _textureCache[fullPath] = texture;
                return texture;
            }
            catch (Exception exception)
            {
                _logger.LogError($"[YATM Textures] Failed loading {fullPath}: {exception}");
                return null;
            }
        }

        private string ResolvePluginPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("Path is empty.");
            }

            string pluginRoot = Path.GetFullPath(_pluginDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            string fullPath = Path.GetFullPath(Path.Combine(_pluginDirectory, relativePath));
            if (!fullPath.StartsWith(pluginRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Path escapes the YATM client plugin directory.");
            }

            return fullPath;
        }

        private static AssetPoolObject FindAssetPoolObject(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            AssetPoolObject assetPoolObject = gameObject.GetComponent<AssetPoolObject>();
            if (assetPoolObject != null)
            {
                return assetPoolObject;
            }

            assetPoolObject = gameObject.GetComponentInParent<AssetPoolObject>();
            if (assetPoolObject != null)
            {
                return assetPoolObject;
            }

            return gameObject.GetComponentInChildren<AssetPoolObject>(true);
        }

        private static IReadOnlyList<Renderer> GetRenderers(AssetPoolObject assetPoolObject)
        {
            var renderers = new List<Renderer>();

            if (assetPoolObject.Renderers != null)
            {
                foreach (Renderer renderer in assetPoolObject.Renderers)
                {
                    if (renderer != null && !renderers.Contains(renderer))
                    {
                        renderers.Add(renderer);
                    }
                }
            }

            foreach (Renderer renderer in assetPoolObject.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null && !renderers.Contains(renderer))
                {
                    renderers.Add(renderer);
                }
            }

            return renderers;
        }

        private static bool MatchesMaterial(string rawName, string[] configuredNames)
        {
            if (configuredNames == null || configuredNames.Length == 0)
            {
                return true;
            }

            string materialName = NormalizeMaterialName(rawName);
            foreach (string configuredName in configuredNames)
            {
                if (configuredName == "*")
                {
                    return true;
                }

                if (string.Equals(
                    NormalizeMaterialName(configuredName),
                    materialName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeMaterialName(string value)
        {
            return (value ?? string.Empty)
                .Replace("_LOD0", string.Empty)
                .Replace("_LOD1", string.Empty)
                .Replace(" (Instance)", string.Empty)
                .Trim();
        }

        private static string MakeSafeFileName(string value)
        {
            string result = string.IsNullOrWhiteSpace(value) ? "unnamed" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                result = result.Replace(invalid, '_');
            }

            return result.Replace(' ', '_').Trim('_');
        }

        private static string GetTransformPath(Transform root, Transform current)
        {
            if (current == null)
            {
                return string.Empty;
            }

            var names = new Stack<string>();
            Transform cursor = current;
            while (cursor != null)
            {
                names.Push(cursor.name);
                if (cursor == root)
                {
                    break;
                }

                cursor = cursor.parent;
            }

            return string.Join("/", names.ToArray());
        }

        private static byte[] ConvertTextureToPng(Texture source)
        {
            if (source == null || source.width <= 0 || source.height <= 0)
            {
                return null;
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);

            Texture2D readable = null;

            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;

                readable = new Texture2D(
                    source.width,
                    source.height,
                    TextureFormat.RGBA32,
                    false,
                    false);

                readable.ReadPixels(
                    new Rect(0f, 0f, source.width, source.height),
                    0,
                    0,
                    false);

                readable.Apply(false, false);
                return ImageConversion.EncodeToPNG(readable);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);

                if (readable != null)
                {
                    UnityEngine.Object.Destroy(readable);
                }
            }
        }

        private sealed class PendingTextureRequest
        {
            public PendingTextureRequest(
                string templateId,
                TextureOverrideRule rule,
                DateTime createdUtc,
                bool isAnimated)
            {
                TemplateId = templateId;
                Rule = rule;
                CreatedUtc = createdUtc;
                IsAnimated = isAnimated;
            }

            public string TemplateId { get; }

            public TextureOverrideRule Rule { get; }

            public DateTime CreatedUtc { get; }

            public bool IsAnimated { get; }
        }

        private sealed class AppliedObjectState
        {
            public AppliedObjectState(
                List<SavedMaterialState> materials,
                HashSet<Renderer> renderers)
            {
                Materials = materials;
                Renderers = renderers;
            }

            public List<SavedMaterialState> Materials { get; }

            public HashSet<Renderer> Renderers { get; }
        }

        private sealed class SavedMaterialState
        {
            public SavedMaterialState(
                Renderer renderer,
                int materialIndex,
                MaterialPropertyBlock originalPropertyBlock)
            {
                Renderer = renderer;
                MaterialIndex = materialIndex;
                OriginalPropertyBlock = originalPropertyBlock;
            }

            public Renderer Renderer { get; }

            public int MaterialIndex { get; }

            public MaterialPropertyBlock OriginalPropertyBlock { get; }
        }
    }
}
