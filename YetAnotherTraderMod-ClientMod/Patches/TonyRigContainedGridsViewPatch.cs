using System.Collections.Generic;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.UI;

namespace YetAnotherTraderMod.Client.Patches
{
    /// <summary>
    /// Arranges configured YATM custom-rig grids using the Maximal Rectangles Best Short Side Fit
    /// (BSSF) bin packing algorithm. When the rig has more grids than can fit in a
    /// single row (exceeding <see cref="MaxColumns"/> cells wide), this patch repositions
    /// all grid views into a compact layout and resizes the parent container to fit.
    /// </summary>
    internal sealed class TonyRigContainedGridsViewShowPatch : ModulePatch
    {
        /// <summary>Maximum number of cell columns before grids must wrap.</summary>
        private const int MaxColumns = 4;

        /// <summary>Pixel width of a single inventory cell (CellSize 62 + BorderSize 1).</summary>
        private const int CellStep = 63;

        /// <summary>Pixel gap reserved between grids on all sides.</summary>
        private const float GridSpacing = 5f;

        /// <summary>Effectively infinite bin height.</summary>
        private const float BinMaxHeight = 100000f;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(
                typeof(ContainedGridsView),
                nameof(ContainedGridsView.Show),
                new[]
                {
                    typeof(CompoundItem),
                    typeof(ItemContextAbstractClass),
                    typeof(GridView[]),
                    typeof(SlotView[]),
                    typeof(TraderControllerClass),
                    typeof(FilterPanel),
                    typeof(ItemUiContext),
                    typeof(bool),
                }
            );
        }

        /// <summary>
        /// Rectangle used by the Maximal Rectangles algorithm to track placed grids and
        /// available free space within the bin/container.
        /// </summary>
        private readonly struct PackingRect
        {
            public readonly float X;
            public readonly float Y;
            public readonly float W;
            public readonly float H;

            public PackingRect(float x, float y, float w, float h)
            {
                X = x;
                Y = y;
                W = w;
                H = h;
            }

            public float Right => X + W;
            public float Bottom => Y + H;

            /// <summary>
            /// Returns <c>true</c> if this rectangle contains <paramref name="other"/> rectangle.
            /// Used during the pruning step to remove redundant rectangles.
            /// </summary>
            public bool Contains(PackingRect other)
            {
                return X <= other.X && Y <= other.Y && Right >= other.Right && Bottom >= other.Bottom;
            }

            /// <summary>
            /// Returns <c>true</c> if this rectangle overlaps with <paramref name="other"/> rectangle.
            /// Used to determine which free rectangles need to be split after a placement.
            /// </summary>
            public bool Overlaps(PackingRect other)
            {
                return X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
            }
        }

        /// <summary>
        /// After all grid views have been created and shown by the original method, this
        /// postfix repositions them using the BSSF bin packing algorithm if the total grid
        /// width exceeds <see cref="MaxColumns"/>.
        /// </summary>
        [PatchPostfix]
        private static void Postfix(ContainedGridsView __instance, CompoundItem compoundItem)
        {
            // Only repack configured YATM custom rigs.
            if (!TonyRigGridLayout.IsTarget(compoundItem))
            {
                return;
            }

            // Collect grid views that were actually initialized with grid data. The
            // GridViews array may contain uninitialized entries from templated layouts
            // where the preset view count exceeds the actual grid count.
            var activeViews = CollectActiveGridViews(__instance.GridViews);
            if (activeViews == null)
            {
                return;
            }

            // If all grids fit side-by-side in a single row, the default horizontal
            // layout is fine and no repacking is needed.
            int totalCellWidth = 0;
            foreach (var gv in activeViews)
            {
                totalCellWidth += gv.Grid.GridWidth;
            }

            if (totalCellWidth <= MaxColumns)
            {
                return;
            }

            // Disable any layout group on the ContainedGridsView that would fight with
            // our manual positioning. The prefab may have a HorizontalLayoutGroup that
            // arranges grids in a single row.
            var existingLayout = __instance.GetComponent<LayoutGroup>();
            if (existingLayout != null)
            {
                existingLayout.enabled = false;
            }

            float totalWidth;
            float totalHeight;

            // This six-grid rig uses a deliberate visual layout: grids 1-4 form the
            // top row and grids 5-6 are centered beneath them. Preserve the original
            // grid order instead of allowing the packing algorithm to reorder them.
            if (TonyRigGridLayout.UsesCenteredBottomPair(compoundItem) && activeViews.Count == 6)
            {
                LayoutCenteredBottomPair(activeViews, out totalWidth, out totalHeight);
            }
            else
            {
                // Use the general BSSF algorithm for every other configured rig.
                PackGridViews(activeViews, out totalWidth, out totalHeight);
            }

            // Update the container's size and layout element so parent layout systems
            // (equipment container, grid window) can accommodate the new dimensions.
            var containerRt = (RectTransform)__instance.transform;
            containerRt.sizeDelta = new Vector2(totalWidth, totalHeight);
            SetLayoutElementSize(__instance, totalWidth, totalHeight);

            // Resize the parent to fit the repacked content.
            ResizeParent(__instance, containerRt);
        }

        /// <summary>
        /// Filters the grid views array to only include views that were initialized with
        /// actual grid data via <see cref="GridView.Show"/>.
        /// </summary>
        /// <returns>
        /// A list of active grid views with at least two entries, or <c>null</c> if
        /// repacking is not needed (zero or one active grids).
        /// </returns>
        private static List<GridView> CollectActiveGridViews(GridView[] gridViews)
        {
            if (gridViews == null || gridViews.Length <= 1)
            {
                return null;
            }

            var active = new List<GridView>(gridViews.Length);
            foreach (var gv in gridViews)
            {
                if (gv != null && gv.Grid != null)
                {
                    active.Add(gv);
                }
            }

            return active.Count > 1 ? active : null;
        }

        /// <summary>
        /// Places grids 1-4 in the top row and centers grids 5-6 on the bottom row.
        /// Grid order is the order supplied by the item's Grids array.
        /// </summary>
        private static void LayoutCenteredBottomPair(
            List<GridView> activeViews,
            out float totalWidth,
            out float totalHeight
        )
        {
            const int topRowCount = 4;

            float topRowWidth = MeasureRowWidth(activeViews, 0, topRowCount);
            float bottomRowWidth = MeasureRowWidth(activeViews, topRowCount, 2);
            float topRowHeight = MeasureRowHeight(activeViews, 0, topRowCount);
            float bottomRowHeight = MeasureRowHeight(activeViews, topRowCount, 2);

            totalWidth = Mathf.Max(topRowWidth, bottomRowWidth);
            totalHeight = topRowHeight + GridSpacing + bottomRowHeight;

            // The first row normally defines the full width. Center it too in case a
            // future edit makes the bottom pair wider than the first four grids.
            float topStartX = (totalWidth - topRowWidth) * 0.5f;
            float bottomStartX = (totalWidth - bottomRowWidth) * 0.5f;

            PositionRow(activeViews, 0, topRowCount, topStartX, 0f);
            PositionRow(activeViews, topRowCount, 2, bottomStartX, topRowHeight + GridSpacing);
        }

        private static float MeasureRowWidth(List<GridView> views, int startIndex, int count)
        {
            float width = 0f;

            for (int i = 0; i < count; i++)
            {
                var rt = (RectTransform)views[startIndex + i].transform;
                width += rt.sizeDelta.x;

                if (i < count - 1)
                {
                    width += GridSpacing;
                }
            }

            return width;
        }

        private static float MeasureRowHeight(List<GridView> views, int startIndex, int count)
        {
            float height = 0f;

            for (int i = 0; i < count; i++)
            {
                var rt = (RectTransform)views[startIndex + i].transform;
                height = Mathf.Max(height, rt.sizeDelta.y);
            }

            return height;
        }

        private static void PositionRow(
            List<GridView> views,
            int startIndex,
            int count,
            float startX,
            float topY
        )
        {
            float x = startX;

            for (int i = 0; i < count; i++)
            {
                var rt = (RectTransform)views[startIndex + i].transform;
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2(x, -topY);

                x += rt.sizeDelta.x + GridSpacing;
            }
        }

        /// <summary>
        /// Positions all grid views using the Maximal Rectangles BSSF algorithm.
        /// </summary>
        /// <param name="activeViews">The grid views to pack.</param>
        /// <param name="totalWidth">Receives the total pixel width of all placed grids.</param>
        /// <param name="totalHeight">Receives the total pixel height of all placed grids.</param>
        private static void PackGridViews(List<GridView> activeViews, out float totalWidth, out float totalHeight)
        {
            totalWidth = 0f;
            totalHeight = 0f;

            // The bin width must accommodate the worst case: MaxColumns individual 1-wide
            // grids with GridSpacing gaps between each pair. A 1-wide grid is (CellStep + 1)
            // pixels wide (due to the border pixel).
            float binWidth = MaxColumns * (CellStep + 1) + (MaxColumns - 1) * GridSpacing;

            // Initialize with a single free rectangle spanning the entire bin.
            var freeRects = new List<PackingRect> { new PackingRect(0f, 0f, binWidth, BinMaxHeight) };

            // Sort grids by area descending for better packing density. Larger grids
            // placed first leave more regular gaps for smaller grids to fill. Ties are
            // broken by height so taller grids create wider horizontal space.
            var sorted = new List<GridView>(activeViews);
            sorted.Sort(
                (a, b) =>
                {
                    int areaA = a.Grid.GridWidth * a.Grid.GridHeight;
                    int areaB = b.Grid.GridWidth * b.Grid.GridHeight;
                    if (areaB != areaA)
                    {
                        return areaB.CompareTo(areaA);
                    }
                    return b.Grid.GridHeight.CompareTo(a.Grid.GridHeight);
                }
            );

            foreach (var gv in sorted)
            {
                var rt = (RectTransform)gv.transform;
                float pixelW = rt.sizeDelta.x;
                float pixelH = rt.sizeDelta.y;

                // Find the best free rectangle using the BSSF algorithm: choose the free
                // rectangle where the shorter of the two leftover sides (horizontal and
                // vertical) is minimized. This favors tight fits and avoids creating thin
                // slivers of waste that are more difficult to fill.
                int bestIdx = FindBestFreeRect(freeRects, pixelW, pixelH);

                // Skip this grid if no free rectangle can contain it.
                if (bestIdx < 0)
                {
                    continue; // This should not happen. kek
                }

                float pixelX = freeRects[bestIdx].X;
                float pixelY = freeRects[bestIdx].Y;

                // Anchor the grid view to the top-left and set its position.
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2(pixelX, -pixelY);

                // The placed area is padded by GridSpacing on the right and bottom edges.
                var placed = new PackingRect(pixelX, pixelY, pixelW + GridSpacing, pixelH + GridSpacing);

                // Split all free rectangles that overlap with the placed area, then prune
                // any that are fully contained within larger free rectangles.
                SplitAndPruneFreeRects(freeRects, placed);

                // Track the actual content size (without the padding).
                float rightEdge = pixelX + pixelW;
                float bottomEdge = pixelY + pixelH;
                if (rightEdge > totalWidth)
                {
                    totalWidth = rightEdge;
                }
                if (bottomEdge > totalHeight)
                {
                    totalHeight = bottomEdge;
                }
            }
        }

        /// <summary>
        /// Scans all free rectangles and returns the index of the one that best fits the
        /// given dimensions using the Best Short Side Fit algorithm.
        /// </summary>
        /// <returns>
        /// The index of the best free rectangle, or <c>-1</c> if none can contain the grid.
        /// </returns>
        private static int FindBestFreeRect(List<PackingRect> freeRects, float width, float height)
        {
            int bestIdx = -1;
            float bestShortSide = float.MaxValue;
            float bestLongSide = float.MaxValue;

            for (int i = 0; i < freeRects.Count; i++)
            {
                var fr = freeRects[i];
                if (width > fr.W || height > fr.H)
                {
                    continue;
                }

                float leftoverH = fr.W - width;
                float leftoverV = fr.H - height;
                float shortSide = Mathf.Min(leftoverH, leftoverV);
                float longSide = Mathf.Max(leftoverH, leftoverV);

                if (shortSide < bestShortSide || (shortSide == bestShortSide && longSide < bestLongSide))
                {
                    bestIdx = i;
                    bestShortSide = shortSide;
                    bestLongSide = longSide;
                }
            }

            return bestIdx;
        }

        /// <summary>
        /// Splits every free rectangle that overlaps with the placed area into up to four
        /// new free rectangles (the portions above, below, left, and right of the placed
        /// area), then prunes any free rectangle that is fully contained within another.
        /// </summary>
        private static void SplitAndPruneFreeRects(List<PackingRect> freeRects, PackingRect placed)
        {
            var candidates = new List<PackingRect>(freeRects.Count * 2);

            // Split: for each existing free rectangle, either keep it unchanged (if it doesn't
            // overlap the placed area) or replace it with up to four fragments.
            for (int i = 0; i < freeRects.Count; i++)
            {
                var fr = freeRects[i];
                if (!fr.Overlaps(placed))
                {
                    candidates.Add(fr);
                    continue;
                }

                // Right fragment: the portion of the free rect to the right of the placed area.
                if (placed.Right < fr.Right)
                {
                    candidates.Add(new PackingRect(placed.Right, fr.Y, fr.Right - placed.Right, fr.H));
                }

                // Left fragment: the portion to the left.
                if (placed.X > fr.X)
                {
                    candidates.Add(new PackingRect(fr.X, fr.Y, placed.X - fr.X, fr.H));
                }

                // Bottom fragment: the portion below.
                if (placed.Bottom < fr.Bottom)
                {
                    candidates.Add(new PackingRect(fr.X, placed.Bottom, fr.W, fr.Bottom - placed.Bottom));
                }

                // Top fragment: the portion above.
                if (placed.Y > fr.Y)
                {
                    candidates.Add(new PackingRect(fr.X, fr.Y, fr.W, placed.Y - fr.Y));
                }
            }

            // Prune: discard any free rectangle that is fully contained within a larger one.
            // Only maximal free rectangles are kept. This is O(n^2) but is... probably... fine.
            freeRects.Clear();
            for (int i = 0; i < candidates.Count; i++)
            {
                bool contained = false;
                for (int j = 0; j < candidates.Count; j++)
                {
                    if (i != j && candidates[j].Contains(candidates[i]))
                    {
                        contained = true;
                        break;
                    }
                }

                if (!contained)
                {
                    freeRects.Add(candidates[i]);
                }
            }
        }

        /// <summary>
        /// Sets the <see cref="LayoutElement"/> on the <see cref="ContainedGridsView"/> so
        /// that parent layout systems correctly allocate space for the repacked content.
        /// Creates a new <see cref="LayoutElement"/> component if one does not already exist.
        /// </summary>
        private static void SetLayoutElementSize(ContainedGridsView instance, float width, float height)
        {
            var layoutElement = instance.GetComponent<LayoutElement>();
            if (layoutElement == null)
            {
                layoutElement = instance.gameObject.AddComponent<LayoutElement>();
            }

            layoutElement.minWidth = width;
            layoutElement.minHeight = height;
            layoutElement.preferredWidth = width;
            layoutElement.preferredHeight = height;
        }

        /// <summary>
        /// Pushes the new container size to the parent.
        /// </summary>
        private static void ResizeParent(ContainedGridsView instance, RectTransform containerRt)
        {
            var parent = instance.transform.parent;
            if (parent == null)
            {
                return;
            }

            var gridWindow = parent.GetComponent<GridWindow>();
            if (gridWindow != null)
            {
                ResizeGridWindow((RectTransform)parent, containerRt);
            }
            else
            {
                // Schedule a deferred layout rebuild rather than forcing an immediate one.
                LayoutRebuilder.MarkLayoutForRebuild((RectTransform)parent);
            }
        }

        /// <summary>
        /// Resizes a <see cref="GridWindow"/> to fit the repacked grid content. Uses
        /// corner positions to compute the required size, which is independent of the
        /// window's anchor and pivot configuration.
        /// </summary>
        private static void ResizeGridWindow(RectTransform windowRt, RectTransform containerRt)
        {
            // Measure the window's top-left corner in world space.
            var windowCorners = new Vector3[4];
            windowRt.GetWorldCorners(windowCorners);
            float windowTop = windowCorners[1].y;
            float windowLeft = windowCorners[0].x;

            // Measure the container's bottom-right corner after resizing.
            var containerCorners = new Vector3[4];
            containerRt.GetWorldCorners(containerCorners);
            float containerBottom = containerCorners[0].y;
            float containerRight = containerCorners[3].x;

            // Expand the window to span from its top-left to the container's bottom-right,
            // with a small padding margin.
            const float padding = 5f;
            float neededWidth = containerRight - windowLeft + padding;
            float neededHeight = windowTop - containerBottom + padding;

            windowRt.sizeDelta = new Vector2(
                Mathf.Max(windowRt.sizeDelta.x, neededWidth),
                Mathf.Max(windowRt.sizeDelta.y, neededHeight)
            );
        }
    }
}
