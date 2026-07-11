using EFT.InventoryLogic;

namespace YetAnotherTraderMod.Client.Patches
{
    /// <summary>
    /// Shared settings for custom rig inventory-grid patches.
    /// </summary>
    internal static class TonyRigGridLayout
    {
        /// <summary>
        /// 6B3TM-01 upgraded armored rig (Khaki).
        /// </summary>
        internal const string TargetTemplateId = "6a523f906dacb02fc0d2d167";

        internal static bool IsTarget(Item item)
        {
            return item != null
                && item.TemplateId.ToString() == TargetTemplateId;
        }
    }
}
