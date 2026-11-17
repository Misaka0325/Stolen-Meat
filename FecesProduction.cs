using System;
using HarmonyLib;

namespace StolenMeat
{
    /// <summary>Produces the native feces item after rat growth and uses native placement.</summary>
    internal static class FecesProduction
    {
        internal static bool TryGenerate(GameItem rat, GameInventory inventory, int growth, float weight)
        {
            if (rat == null || inventory == null || !float.IsFinite(weight)) return false;
            if (growth < 30) return true;
            string id = weight >= 1000f ? "feces_large"
                : weight >= 600f ? "feces_medium" : "feces_small";
            GameItem feces = null;
            try
            {
                feces = ItemSpawner.Spawn(id);
                if (feces == null) return false;
                int remaining = GraphUtils.TryAcceptAll(inventory, feces, -1);
                if (remaining > 0 && feces.parentInventory == null && feces.unitCount > 0)
                {
                    bool rescued = OverflowPatch.Coordinator != null
                        && OverflowPatch.Coordinator.TryRescue(feces, "rat feces inventory full");
                    if (!rescued && feces.parentInventory == null && feces.unitCount > 0)
                        GraphUtils.DestroyIfUnplaced(feces, null);
                }
                RatButcherPatch.Observer?.RecordAnimal("feces produced: source=" + rat.uniqueId
                    + ", item=" + id + ", units=1, growth=" + growth + ", weight=" + weight);
                return true;
            }
            catch (Exception error)
            {
                RatButcherPatch.Observer?.RecordAnimal("feces placement failed: source="
                    + rat.uniqueId + ", item=" + id + ", error=" + error);
                // A native callback may throw after acceptance; never spawn another copy.
                if (feces == null) return false;
                if (feces.parentInventory == null && feces.unitCount > 0)
                {
                    try { GraphUtils.DestroyIfUnplaced(feces, null); }
                    catch (Exception cleanupError)
                    {
                        RatButcherPatch.Observer?.RecordAnimal("feces cleanup failed: " + cleanupError);
                    }
                }
                return true;
            }
        }
    }

    /// <summary>Routes native single-rat feces through the same stack and overflow rules.</summary>
    [HarmonyPatch(typeof(HusbandryHelper), nameof(HusbandryHelper.Poop))]
    internal static class RatFecesPatch
    {
        private static bool Prefix(GameItem __0, int __1)
        {
            if (__0 == null || __0.identifier != "rat" || !ItemStackPolicy.IsAnimal(__0)
                || __0.parentInventory == null) return true;
            return !FecesProduction.TryGenerate(__0, __0.parentInventory, __1,
                __0.GetTagReadonly("ANIMAL_WEIGHT_TAG").GetFloat());
        }
    }

    /// <summary>Dissolves one unit of a feces stack with its native chemical contents.</summary>
    [HarmonyPatch(typeof(ChemicalHelper), nameof(ChemicalHelper.DissolveInto))]
    internal static class FecesDissolvePatch
    {
        private sealed class Context { internal GameItem Source; internal GameItem Clone; internal int Count; }

        private static bool Prefix(ref GameItem __0, out Context __state)
        {
            __state = null;
            if (!ItemStackPolicy.IsFeces(__0) || __0.unitCount <= 1) return true;
            try
            {
                GameItem clone = NativeSplitPreparation.Create(__0, 1);
                if (clone == null) return false;
                clone.SetUnitCount(1);
                __state = new Context { Source = __0, Clone = clone, Count = __0.unitCount };
                __0 = clone;
                return true;
            }
            catch (Exception error)
            {
                RatButcherPatch.Observer?.RecordAnimal("feces dissolve preparation failed: " + error);
                return false;
            }
        }

        private static void Postfix(Context __state)
        {
            if (__state == null) return;
            __state.Source.SetUnitCount(__state.Count - 1);
            __state.Source.Validate();
            RatButcherPatch.Observer?.RecordAnimal("feces dissolved: source="
                + __state.Source.uniqueId + ", units=1, remaining=" + __state.Source.unitCount);
        }

        private static Exception Finalizer(Exception __exception, Context __state)
        {
            if (__exception != null && __state?.Clone != null && __state.Clone.parentInventory == null
                && __state.Clone.unitCount > 0) __state.Clone.Destroy();
            return __exception;
        }
    }
}