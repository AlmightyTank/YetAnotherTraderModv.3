using System;
using System.Collections.Generic;

namespace YetAnotherTraderMod.Client.Models
{
    public sealed class TextureOverrideConfig
    {
        public bool Enabled { get; set; } = true;

        public bool ExportDefaultTextures { get; set; } = true;

        public bool OverwriteDefaultTextures { get; set; } = false;

        public string[] ExportTextureProperties { get; set; } = new[]
        {
            "_MainTex",
            "_BumpMap",
            "_SpecGlossMap",
            "_MetallicGlossMap",
            "_OcclusionMap",
            "_EmissionMap",
            "_ColorTex",
            "_AlphaTex"
        };

        public Dictionary<string, TextureOverrideRule> Overrides { get; set; }
            = new Dictionary<string, TextureOverrideRule>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class TextureOverrideRule
    {
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Original EFT template ID used for the exported DefaultTextures folder.
        /// Leave blank to use the custom template ID instead.
        /// </summary>
        public string SourceTemplateId { get; set; } = string.Empty;

        /// <summary>
        /// Simple single-texture form. This is normally the custom _MainTex PNG.
        /// </summary>
        public string Texture { get; set; } = string.Empty;

        public string TextureProperty { get; set; } = "_MainTex";

        /// <summary>
        /// Optional multi-map form. Entries here are applied in addition to Texture.
        /// Example: { "_MainTex": "Textures/Custom/item.png", "_BumpMap": "..." }
        /// </summary>
        public Dictionary<string, string> Textures { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Exact normalized material names, or "*" for every compatible material.
        /// Material names are written to DefaultTextures/.../manifest.json.
        /// </summary>
        public string[] Materials { get; set; } = new[] { "*" };

        public float ScaleX { get; set; } = 1f;

        public float ScaleY { get; set; } = 1f;

        public float OffsetX { get; set; } = 0f;

        public float OffsetY { get; set; } = 0f;

        /// <summary>
        /// Optional HTML color such as #FFFFFF. Leave blank to preserve the EFT color.
        /// </summary>
        public string Color { get; set; } = string.Empty;

        public string ColorProperty { get; set; } = "_Color";
    }

    internal sealed class DefaultTextureManifest
    {
        public string CustomTemplateId { get; set; } = string.Empty;

        public string SourceTemplateId { get; set; } = string.Empty;

        public string Context { get; set; } = string.Empty;

        public string ExportedUtc { get; set; } = string.Empty;

        public List<DefaultTextureMaterialEntry> Materials { get; set; }
            = new List<DefaultTextureMaterialEntry>();
    }

    internal sealed class DefaultTextureMaterialEntry
    {
        public string RendererPath { get; set; } = string.Empty;

        public int RendererIndex { get; set; }

        public int MaterialIndex { get; set; }

        public string MaterialName { get; set; } = string.Empty;

        public string ShaderName { get; set; } = string.Empty;

        public Dictionary<string, string> Textures { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
