using System.Collections.Generic;
using HarmonyLib;

namespace StolenMeat
{
    /// <summary>Observes the native rat butcher result without modifying the returned list.</summary>
    [HarmonyPatch(typeof(HusbandryHelper), nameof(HusbandryHelper.CreateButcherCuts))]
    internal static class RatButcherPatch
    {
        internal static LootObserver Observer;

        private static void Postfix(GameItem gameItem, List<GameItem> __result)
        {
            // The original returns null for non-rat animals.
            if (__result != null) Observer?.RecordButcherCuts(gameItem, __result);
        }
    }

    /// <summary>Observes the surrounding native butcher operation and its normal return.</summary>
    [HarmonyPatch(typeof(HusbandryHelper), nameof(HusbandryHelper.OnButcher))]
    internal static class ButcherOperationPatch
    {
        private static void Prefix(GameItem gameItem, out bool __state)
        {
            __state = gameItem != null && HusbandryHelper.GetSpecies(gameItem) == AnimalSpecies.Rat;
            if (__state) RatButcherPatch.Observer?.RecordButcherEntered(gameItem);
        }

        private static void Postfix(bool __state)
        {
            if (__state) RatButcherPatch.Observer?.RecordButcherReturned();
        }
    }
}
