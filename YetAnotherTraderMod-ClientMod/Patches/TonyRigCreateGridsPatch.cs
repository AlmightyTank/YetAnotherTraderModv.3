using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace YetAnotherTraderMod.Client.Patches
{
    /// <summary>
    /// Forces Tony's upgraded 6B3TM-01U to use <see cref="GeneratedGridsView"/> instead of static
    /// <see cref="TemplatedGridsView"/> layouts. Templated layouts bake grid positions into
    /// Unity prefabs, which means they cannot adapt when grid dimensions are modified at
    /// runtime. By intercepting <see cref="ContainedGridsView.CreateGrids"/> for the target rig
    /// that have a <see cref="GridLayoutComponent"/>, we force instantiation of the dynamic
    /// template so that grid views are created to match actual grid data.
    /// </summary>
    internal sealed class TonyRigCreateGridsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(
                typeof(ContainedGridsView),
                nameof(ContainedGridsView.CreateGrids),
                new[] { typeof(Item), typeof(ContainedGridsView) }
            );
        }

        /// <summary>
        /// Intercepts grid creation for the target rig that would normally load a static
        /// layout prefab. Instead, instantiates the dynamic <see cref="GeneratedGridsView"/>
        /// template which creates one <see cref="GridView"/> per grid at runtime.
        /// </summary>
        [PatchPrefix]
        private static bool Prefix(
            Item item,
            ContainedGridsView containedGridsTemplate,
            ref ContainedGridsView __result
        )
        {
            // Only replace the baked layout for Tony's upgraded 6B3TM-01U.
            if (!TonyRigGridLayout.IsTarget(item))
            {
                return true;
            }

            // If the backpack has no static layout component, it already uses the dynamic
            // template. Let the original method handle it.
            var layoutComponent = item.GetItemComponent<GridLayoutComponent>();
            if (layoutComponent == null)
            {
                return true;
            }

            // Instantiate the dynamic template, bypassing the static rig layout prefab.
            __result = Object.Instantiate(containedGridsTemplate);
            return false;
        }
    }
}
