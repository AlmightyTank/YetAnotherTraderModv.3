using System;
using EFT.InventoryLogic;

namespace YetAnotherTraderMod.Client.Patches
{
    /// <summary>
    /// Shared settings for custom rig inventory-grid patches.
    /// Add future custom rig template IDs to <see cref="TargetTemplateIds"/>.
    /// </summary>
    internal static class TonyRigGridLayout
    {
        /// <summary>
        /// This rig uses four grids on the top row with grids 5 and 6 centered below.
        /// </summary>
        internal const string CenteredBottomPairTemplateId = "6a52c7dd6dacb02fc0d2d177";

        internal static readonly string[] TargetTemplateIds =
        {
            // 6B3TM-01 upgraded armored rig (Khaki)
            "6a523f906dacb02fc0d2d167",

            // Four grids on top, grids 5 and 6 centered on the bottom row.
            CenteredBottomPairTemplateId,
        };

        internal static bool IsTarget(Item item)
        {
            if (item == null)
            {
                return false;
            }

            string templateId = item.TemplateId.ToString();

            foreach (string targetTemplateId in TargetTemplateIds)
            {
                if (string.Equals(templateId, targetTemplateId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool UsesCenteredBottomPair(Item item)
        {
            return item != null
                && string.Equals(
                    item.TemplateId.ToString(),
                    CenteredBottomPairTemplateId,
                    StringComparison.Ordinal
                );
        }
    }
}
