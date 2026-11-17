using System;
using System.Collections.Generic;
using HarmonyLib;

namespace StolenMeat
{
    /// <summary>Rescues capacity overflow at the native final unplaced-item boundary.</summary>
    [HarmonyPatch(typeof(GraphUtils), nameof(GraphUtils.DestroyIfUnplaced))]
    internal static class OverflowPatch
    {
        internal static OverflowCoordinator Coordinator;

        /// <summary>Replaces native discard only when overflow rescue succeeds.</summary>
        private static bool Prefix(GameItem item, string context, ref bool __result)
        {
            if (Coordinator == null || item == null || item.parentInventory != null || !IsCapacityContext(context)) return true;
            if (!Coordinator.TryRescue(item, context)) return true;
            __result = true;
            return false;
        }

        private static bool IsCapacityContext(string context)
        {
            if (string.IsNullOrEmpty(context)) return false;
            return context.IndexOf("full", StringComparison.OrdinalIgnoreCase) >= 0
                || context == "ras breeding" || context == "ras hatch"
                || context == "ras fry growth" || context == "rancher starter cage"
                || context == "ras sold with tank";
        }
    }

    /// <summary>Uses game storage rules and a one-time native 10x10 overflow window.</summary>
    internal sealed class OverflowCoordinator
    {
        private readonly LootObserver observer;
        private PixelWindow window;
        private GameInventory inventory;
        private bool rescuing;

        internal OverflowCoordinator(LootObserver observer) { this.observer = observer; }

        /// <summary>Places remaining units in native storage, then temporary storage.</summary>
        internal bool TryRescue(GameItem item, string context)
        {
            // Native placement can call DestroyIfUnplaced again while merging stacks. Keep one rescue active.
            if (rescuing) return false;
            rescuing = true;
            string id = item.identifier;
            try
            {
                EmporiumEntry store = EmporiumEntry.Instance;
                if (store != null)
                {
                    foreach (GraphNodeStorage target in StoreTargets(store))
                    {
                        if (target == null) continue;
                        int before = item.unitCount;
                        try
                        {
                            bool complete = TryPlace(target, item, out int placed);
                            if (placed > 0) observer.RecordOverflow(context, id, placed, "native store container");
                            if (complete)
                            {
                                // The native overflow event still opens the window even if another
                                // store container accepts this item during the extended search.
                                EnsureWindow();
                                return true;
                            }
                        }
                        catch (Exception error)
                        {
                            // A container can accept an item before its UI callback throws.
                            int accepted = item.parentInventory != null ? before : Math.Max(0, before - item.unitCount);
                            observer.RecordOverflow(context, id, accepted,
                                "native container callback error; continuing: " + error.GetType().Name + ": " + error.Message);
                            if (item.parentInventory != null)
                            {
                                EnsureWindow();
                                return true;
                            }
                        }
                    }
                }
                EnsureWindow();
                bool overflowComplete = TryPlace(inventory, item, out int overflowPlaced);
                if (overflowPlaced > 0) observer.RecordOverflow(context, id, overflowPlaced, "temporary 10x10 window");
                if (overflowComplete) return true;
                observer.RecordOverflow(context, id, item.unitCount, "temporary window full; remaining units discarded by game");
                return false;
            }
            catch (Exception error)
            {
                observer.RecordOverflow(context, id, item.unitCount, "rescue error; game fallback: " + error);
                return false;
            }
            finally { rescuing = false; }
        }

        /// <summary>Routes a split without silently moving it to another store container.</summary>
        internal bool RouteSplit(GameItem item, out int consumed, out int discarded)
        {
            consumed = 0;
            discarded = 0;
            if (rescuing || item == null || item.unitCount < 1) return false;
            rescuing = true;
            int requested = item.unitCount;
            bool priorSuppression = ItemStackPolicy.SuppressStackingForSplit;
            // A split needs its own slot; merging with the source would undo it.
            ItemStackPolicy.SuppressStackingForSplit = true;
            try
            {
                EnsureWindow();
                bool complete = TryPlace(inventory, item, out int placed);
                consumed += placed;
                if (placed > 0)
                    observer.RecordOverflow("split", item.identifier, placed, "temporary 10x10 window");
                if (!complete)
                {
                    discarded = item.unitCount;
                    observer.RecordOverflow("split", item.identifier, discarded,
                        "temporary window full; remaining units discarded by game");
                    GraphUtils.DestroyIfUnplaced(item, null);
                    consumed += discarded;
                }
                return true;
            }
            catch (Exception error)
            {
                // Keep the source units corresponding to any unplaced clone remainder.
                consumed = item.parentInventory != null ? requested
                    : Math.Max(consumed, requested - item.unitCount);
                observer.RecordOverflow("split", item.identifier, requested - consumed,
                    "placement error; source remainder retained: " + error);
                if (item.parentInventory == null) GraphUtils.DestroyIfUnplaced(item, null);
                return consumed > 0;
            }
            finally
            {
                ItemStackPolicy.SuppressStackingForSplit = priorSuppression;
                rescuing = false;
            }
        }

        /// <summary>Places an entire animal cohort in temporary storage without discarding rats.</summary>
        internal bool TryPlaceAnimalCohort(GameItem item)
        {
            if (item == null || item.unitCount < 1 || rescuing) return false;
            bool previous = ItemStackPolicy.SuppressStackingForSplit;
            ItemStackPolicy.SuppressStackingForSplit = true;
            rescuing = true;
            try
            {
                EnsureWindow();
                SlotMarker slot = inventory.TryFindOneValidInventorySlot(item);
                return slot != null && slot.targetItem == null && slot.itemGridShape != null
                    && slot.numTransfer >= item.unitCount
                    && slot.TryAcceptOnce(item.unitCount) == item.unitCount;
            }
            finally
            {
                rescuing = false;
                ItemStackPolicy.SuppressStackingForSplit = previous;
            }
        }

        /// <summary>Returns whether all units were accepted and reports units moved.</summary>
        private static bool TryPlace(GraphNodeStorage target, GameItem item, out int placed)
        {
            placed = 0;
            if (target == null || item == null || item.unitCount <= 0) return false;
            int requested = item.unitCount;
            int remaining = GraphUtils.TryAcceptAll(target, item, -1);
            placed = Math.Max(0, requested - remaining);
            if (remaining > 0) return false;
            if (item.parentInventory == null) GraphUtils.DestroyIfUnplaced(item, null);
            return true;
        }

        private static IEnumerable<GraphNodeStorage> StoreTargets(EmporiumEntry store)
        {
            // Original placement is already attempted; these are additional capacity checks.
            yield return store.afterhourInventory;
            yield return store.backInvinvElement;
            yield return store.invElement;
            yield return store.showcaseElement;
            yield return store.frontInvinvElement;
            yield return store.backInvinvElementCounter;
            yield return store.bazarLeftinvElement;
            yield return store.hiddenElement;
            yield return store.trashcanInvElement;
            yield return store.trashInvElement;
        }

        /// <summary>Creates and shows the native inventory window on first overflow.</summary>
        private void EnsureWindow()
        {
            if (window != null)
            {
                window.Show();
                return;
            }
            var pair = DirectoryUtils.CreateInventoryWindow(10, 10, true);
            window = pair.Item1;
            inventory = pair.Item2;
            window.SetTitle("临时窗口");
            window.closeCallback = () => CloseAndClear("manual close");
            window.Show();
            observer.RecordOverflow("window", "<none>", 0, "temporary window opened");
        }

        /// <summary>Discards items still inside and removes the temporary window.</summary>
        internal void CloseAndClear(string context)
        {
            PixelWindow oldWindow = window;
            GameInventory oldInventory = inventory;
            window = null;
            inventory = null;
            if (oldWindow == null) return;
            long removed = 0;
            if (oldInventory != null)
            {
                var snapshot = new List<GameItem>(oldInventory.childItems);
                foreach (GameItem item in snapshot)
                {
                    if (item == null) continue;
                    removed += item.unitCount;
                    item.Destroy();
                }
            }
            // Prevent the native close handler from invoking this cleanup again.
            oldWindow.closeCallback = null;
            oldWindow.Hide();
            UnityEngine.Object.Destroy(oldWindow.handler.gameObject);
            observer.RecordOverflow(context, "<all>", removed, "temporary window cleared and closed");
        }
    }
}







