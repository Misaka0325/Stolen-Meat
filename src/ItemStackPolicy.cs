using System;
using System.Collections.Generic;
using HarmonyLib;

namespace StolenMeat
{
    /// <summary>Bounds native stack counts before Int32 addition can wrap.</summary>
    internal static class StackLimitPolicy
    {
        internal const int Maximum = int.MaxValue;
        internal const int OverflowKeep = (1 << 23) - 1;

        internal static int GeneratedCount(long requested) =>
            requested < 1 ? 0 : requested > Maximum ? Maximum : (int)requested;

        /// <summary>Trims only the units participating in an overflowing merge.</summary>
        internal static bool ReduceMerge(int target, int source, int amount,
            out int targetBefore, out int sourceBefore, out int moved, out long discarded)
        {
            targetBefore = target;
            sourceBefore = source;
            moved = amount == -1 ? source : amount;
            discarded = 0;
            if (target < 1 || source < 1 || moved < 1 || moved > source
                || (long)target + moved <= Maximum) return false;
            int keptTarget = Math.Min(target, OverflowKeep - 1);
            int keptMoved = OverflowKeep - keptTarget;
            discarded = (long)target + moved - OverflowKeep;
            targetBefore = keptTarget;
            sourceBefore = source - moved + keptMoved;
            moved = keptMoved;
            return true;
        }
    }

    /// <summary>Controls native stacking of meat and selected animal species.</summary>
    internal static class ItemStackPolicy
    {
        private static readonly HashSet<string> MeatIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "raw_meat", "small_raw_meat", "morsel", "small_morsel",
            "fat_meat", "meat_scrap", "processed_meat"
        };

        internal static bool SuppressStackingForSplit;
        // Future animal species can be enabled without changing the stack hooks.
        internal static bool AllAnimalsEnabled;

        internal static bool IsMeat(GameItem item) => item != null && MeatIds.Contains(item.identifier);
        internal static bool IsAnimal(GameItem item) => item != null
            && (item.identifier == "rat" && item.GetTagReadonly("ANIMAL_SPECIES_TAG").GetString() == "ANIMAL_RAT"
                || AllAnimalsEnabled && item.IsTag("ANIMAL_ITEM_TAG"));
        internal static bool IsFeces(GameItem item) => item != null && item.IsTag("ANIMAL_FECES_TAG")
            && (item.identifier == "feces_small" || item.identifier == "feces_medium"
                || item.identifier == "feces_large");
        internal static bool IsSupported(GameItem item) => IsMeat(item) || IsAnimal(item) || IsFeces(item);

        /// <summary>Uses individual records for animals and exact effective tags for meat.</summary>
        internal static bool CanStack(GameItem source, GameItem target)
        {
            if (SuppressStackingForSplit || !IsSupported(source) || !IsSupported(target)) return false;
            if (!string.Equals(source.identifier, target.identifier, StringComparison.Ordinal)
                || ReferenceEquals(source, target) || source.unitCount < 1 || target.unitCount < 1) return false;
            if (IsAnimal(source) || IsAnimal(target))
                return IsAnimal(source) && IsAnimal(target)
                    && string.Equals(source.GetTagReadonly("ANIMAL_SPECIES_TAG").GetString(),
                        target.GetTagReadonly("ANIMAL_SPECIES_TAG").GetString(), StringComparison.Ordinal)
                    && source.IsTag("ANIMAL_IS_DEAD_TAG") == target.IsTag("ANIMAL_IS_DEAD_TAG")
                    && source.contentWindow == null && target.contentWindow == null
                    && source.examineWindow == null && target.examineWindow == null
                    && AnimalStackState.IsValid(source) && AnimalStackState.IsValid(target)
                    && (source.IsTag("ANIMAL_IS_DEAD_TAG")
                        || !(AnimalStackState.HasHungryVirtual(source)
                            && AnimalStackState.HasNormalVirtual(target))
                        && !(AnimalStackState.HasNormalVirtual(source)
                            && AnimalStackState.HasHungryVirtual(target)))
                    && (source.IsTag("ANIMAL_IS_DEAD_TAG")
                        || AnimalStackState.CanMergeLineage(source, target));
            if (!string.Equals(source.spritePath, target.spritePath, StringComparison.Ordinal)
                || source.unitValue != target.unitValue
                || !string.Equals(source.customText, target.customText, StringComparison.Ordinal)) return false;
            return SameTags(source.modifiedState.dict, target.modifiedState.dict);
        }

        private static bool SameTags(Dictionary<string, TagState> left, Dictionary<string, TagState> right)
        {
            if (left == null || right == null) return left == right;
            foreach (var pair in left)
            {
                if (pair.Key == RawMeatStack.Marker || !HasValue(pair.Value)) continue;
                if (!right.TryGetValue(pair.Key, out TagState other) || !SameValue(pair.Value, other)) return false;
            }
            foreach (var pair in right)
            {
                if (pair.Key == RawMeatStack.Marker || !HasValue(pair.Value)) continue;
                if (!left.TryGetValue(pair.Key, out TagState other) || !SameValue(pair.Value, other)) return false;
            }
            return true;
        }

        private static bool HasValue(TagState tag) => tag != null && (tag.valueEnabled
            || !string.IsNullOrEmpty(tag.valueString) || tag.valueInt != 0
            || tag.valueFloat != 0f || tag.valueLong != 0L
            || tag.valueDouble != 0d || tag.valueBool);

        private static bool SameValue(TagState left, TagState right)
        {
            if (!HasValue(left) && !HasValue(right)) return true;
            return left != null && right != null && left.valueEnabled == right.valueEnabled
                && string.Equals(left.valueString, right.valueString, StringComparison.Ordinal)
                && left.valueInt == right.valueInt && left.valueFloat.Equals(right.valueFloat)
                && left.valueLong == right.valueLong && left.valueDouble.Equals(right.valueDouble)
                && left.valueBool == right.valueBool;
        }
    }

    /// <summary>Uses native drag placement for supported items.</summary>
    [HarmonyPatch(typeof(GameItem), nameof(GameItem.MayStack))]
    internal static class ItemMayStackPatch
    {
        private static void Postfix(GameItem __instance, GameItem otherItem, ref bool __result)
        {
            if (ItemStackPolicy.IsSupported(__instance) || ItemStackPolicy.IsSupported(otherItem))
                __result = ItemStackPolicy.CanStack(__instance, otherItem);
        }
    }

    /// <summary>Lets a free slot accept a ready stack without splitting it into units.</summary>
    [HarmonyPatch(typeof(GameItem), nameof(GameItem.MaxNumAdd), new Type[] { typeof(SlotMarker) })]
    internal static class ItemStackCapacityPatch
    {
        private static void Postfix(GameItem __instance, SlotMarker slot, ref int __result)
        {
            if (!ItemStackPolicy.IsSupported(__instance) || __instance.unitCount < 1
                || ItemStackPolicy.IsAnimal(__instance) && !AnimalStackState.IsValid(__instance)
                || slot == null || slot.inventory == null) return;
            if (slot.targetItem != null && !ReferenceEquals(slot.targetItem, __instance)) return;
            if (slot.inventory.maxSlotAmountItemFunc != null || slot.inventory.maxSlotAddItemFunc != null
                || __instance.maxSlotAmountItemFunc != null || __instance.maxSlotAddItemFunc != null) return;
            __result = slot.targetItem == null ? int.MaxValue : int.MaxValue - __instance.unitCount;
        }
    }

    /// <summary>Records the amount actually moved by the native stack operation.</summary>
    [HarmonyPatch(typeof(SlotMarker), nameof(SlotMarker.StackItemUnchecked))]
    internal static class ItemStackMergePatch
    {
        private sealed class MergeContext
        {
            internal AnimalStackState.MergeSnapshot Animal;
            internal long Discarded;
        }

        private static bool Prefix(GameItem item, GameItem targetItem, ref int amount,
            out MergeContext __state)
        {
            __state = new MergeContext();
            if (item == null || targetItem == null || ReferenceEquals(item, targetItem)) return true;
            int requested = amount == -1 ? item.unitCount : amount;
            if (requested > 0 && requested <= item.unitCount
                && (long)targetItem.unitCount + requested > StackLimitPolicy.Maximum
                && ItemStackPolicy.IsAnimal(item) && ItemStackPolicy.IsAnimal(targetItem))
            {
                if (!AnimalStackState.MergeOverflow(item, targetItem, requested))
                    RatButcherPatch.Observer?.RecordAnimal("animal merge overflow blocked: source="
                        + item.uniqueId + ", target=" + targetItem.uniqueId);
                return false;
            }
            if (StackLimitPolicy.ReduceMerge(targetItem.unitCount, item.unitCount, amount,
                out int targetBefore, out int sourceBefore, out int moved, out long discarded))
            {
                targetItem.unitCount = targetBefore;
                item.unitCount = sourceBefore;
                amount = moved;
                __state.Discarded = discarded;
            }
            if (ItemStackPolicy.CanStack(item, targetItem))
                __state.Animal = AnimalStackState.PrepareMerge(item, targetItem, amount);
            return true;
        }

        private static void Postfix(GameItem item, GameItem targetItem, MergeContext __state)
        {
            if (__state == null || targetItem == null) return;
            if (__state.Animal != null)
            {
                AnimalStackState.FinishMerge(item, targetItem, __state.Animal);
                RatButcherPatch.Observer?.RecordMerge(item, targetItem,
                    targetItem.unitCount - __state.Animal.TargetCount);
            }
            if (__state.Discarded > 0)
                RatButcherPatch.Observer?.RecordAnimal("stack overflow: item=" + targetItem.identifier
                    + ", retained=" + targetItem.unitCount + ", discarded=" + __state.Discarded);
            if (ItemStackPolicy.IsAnimal(targetItem) && targetItem.parentInventory != null)
            {
                try { AnimalStackState.CompressContainer(targetItem.parentInventory, true); }
                catch (Exception error)
                {
                    RatButcherPatch.Observer?.RecordAnimal("container real compression failed: "
                        + error);
                }
            }
        }
    }
}
