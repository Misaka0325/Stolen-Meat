using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StolenMeat
{
    /// <summary>Creates official rat meat objects with their final unit counts before placement.</summary>
    internal static class RawMeatStack
    {
        internal const string Marker = "STOLEN_MEAT_STACK";
        internal const string ItemId = "raw_meat";

        internal static bool IsOurs(GameItem item) => item != null && item.identifier == ItemId && item.IsTag(Marker);

        /// <summary>Creates a native meat item with the original tags and count.</summary>
        internal static GameItem Create(string id, string speciesId, bool prion, long amount, bool mark)
        {
            int units = StackLimitPolicy.GeneratedCount(amount);
            if (units < 1) throw new ArgumentOutOfRangeException(nameof(amount));
            GameItem item = ItemSpawner.Spawn(id);
            if (item == null) throw new InvalidOperationException("ItemSpawner.Spawn returned null for " + id);
            item.ModifyTag("MEAT_TYPE_TAG", tag => { tag.Enable(); tag.SetString(speciesId); }, false);
            if (prion) item.EnableTag("ANIMAL_PRION_TAG", false);
            if (units > 1) item.SetUnitCount(units);
            if (mark) item.EnableTag(Marker, false);
            if (amount > units)
                RatButcherPatch.Observer?.RecordAnimal("generated stack capped: item=" + id
                    + ", requested=" + amount + ", retained=" + units);
            return item;
        }

        /// <summary>Shortens large labels without changing the stored count.</summary>
        internal static string FormatCount(int amount)
        {
            if (amount < 100) return amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int exponent = 0;
            double coefficient = amount;
            while (coefficient >= 10d) { coefficient /= 10d; exponent++; }
            coefficient = Math.Floor(coefficient * 100d) / 100d;
            return coefficient.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "E" + exponent;
        }
    }

    /// <summary>Reproduces the game's rat cut calculation as ready-to-place meat stacks.</summary>
    [HarmonyPatch(typeof(HusbandryHelper), nameof(HusbandryHelper.CreateButcherCuts))]
    internal static class RatCutsStackPatch
    {
        /// <summary>Builds rat cuts before vanilla creates one raw_meat object per unit.</summary>
        private static bool Prefix(GameItem gameItem, ref List<GameItem> __result)
        {
            if (gameItem == null || gameItem.GetTagReadonly("ANIMAL_SPECIES_TAG").GetString() != "ANIMAL_RAT") return true;
            var products = new List<GameItem>();
            try
            {
                float rawWeight = gameItem.GetTagReadonly("ANIMAL_WEIGHT_TAG").GetFloat();
                // The native weight tag is float; 2^31 is the first value outside Int32.
                if (float.IsNaN(rawWeight) || float.IsInfinity(rawWeight)
                    || rawWeight < 0f || rawWeight >= 2147483648f)
                {
                    __result = new List<GameItem>();
                    RatButcherPatch.Observer?.RecordUnsafeWeight(gameItem, rawWeight);
                    return false;
                }
                int weight = Mathf.RoundToInt(rawWeight);
                if (gameItem.IsTag("ANIMAL_DISEASED_TAG")) weight = Mathf.RoundToInt(weight * 0.75f);
                int large = weight / 400;
                weight %= 400;
                int medium = weight / 200;
                weight %= 200;
                int small = weight / 100;
                weight %= 100;
                int tiny = weight / 50 + (weight % 50 > 0 ? 1 : 0);
                bool prion = gameItem.IsTag("ANIMAL_PRION_TAG");
                if (large > 0) products.Add(RawMeatStack.Create("raw_meat", "ANIMAL_RAT", prion, large, true));
                AddProducts(products, "small_raw_meat", medium, prion);
                AddProducts(products, "morsel", small, prion);
                AddProducts(products, "small_morsel", tiny, prion);
                __result = products;
                RatButcherPatch.Observer?.RecordStackInterception(gameItem, large, products.Count);
                return false;
            }
            catch (Exception error)
            {
                foreach (GameItem item in products) if (item != null) item.Destroy();
                RatButcherPatch.Observer?.RecordStackFailure(error);
                return true;
            }
        }

        private static void AddProducts(List<GameItem> products, string id, int count, bool prion)
        {
            if (count > 0) products.Add(RawMeatStack.Create(id, "ANIMAL_RAT", prion, count, false));
        }
    }

    /// <summary>Keeps the native lower-right count badge for every supported meat stack.</summary>
    [HarmonyPatch(typeof(GameItemElement), nameof(GameItemElement.UpdateFakeReal))]
    internal static class StackCountLabelPatch
    {
        private static void Postfix(GameItemElement __instance, int numPickedUp, TreeNodeRender ___stackCountText, TreeNodeRender ___stackCountTextBackground)
        {
            int visibleCount = __instance.unitCount - numPickedUp;
            if (!ItemStackPolicy.IsSupported(__instance) || visibleCount < 100) return;
            string label = RawMeatStack.FormatCount(visibleCount);
            ___stackCountText.text.text = label;
            ___stackCountTextBackground.text.text = label;
        }
    }
}







