using System;
using System.Collections.Generic;
using System.Globalization;

namespace StolenMeat
{
    /// <summary>Applies one virtual group's feeder and loose-food allocation.</summary>
    internal static class AnimalGroupFeeding
    {
        internal sealed class Result
        {
            internal double FeederRate;
            internal double LooseRate;
            internal double HungerRate;
            internal double EnergyUsed;
            internal double HungerGainPerAnimal;
            internal float HungerBeforeFood;
            internal float MaxHunger;
            internal long FeederKcal;
            internal long LooseItems;
            internal double RatMeatDrawChance;
            internal double PrionTaggedRatMeatDrawChance;
            internal double FeederPrionRate;
            internal double LoosePrionRate;
            internal double PrionIncrement;
            internal long LooseFoodUnits;
            internal long CertainPrionRatMeatUnits;
            internal long PossiblePrionRatMeatUnits;
            internal float Immunity;
            internal int NativeImmunity;
        }

        internal static Result Feed(GameItem proxy, GameInventory inventory, long animals)
        {
            var result = new Result
            {
                HungerBeforeFood = proxy.GetTagReadonly("ANIMAL_HUNGER_TAG").GetFloat(),
                MaxHunger = HusbandryHelper.GetMaxHunger(proxy)
            };
            if (animals < 1 || !float.IsFinite(result.HungerBeforeFood)
                || !float.IsFinite(result.MaxHunger) || result.MaxHunger <= 0f) return result;
            if (result.HungerBeforeFood >= result.MaxHunger * 0.75f) return result;

            var dispensers = ReachableDispensers(inventory);
            string species = proxy.GetTagReadonly("ANIMAL_SPECIES_TAG").GetString();
            string storedSpecies = MachineFeedDispenser.GetStoredTag(species);
            string storedPrion = MachineFeedDispenser.GetPrionStoredTag(species);
            long availableKcal = 0;
            float targetRatio = 0.85f;
            bool targetChosen = false;
            foreach (GameItem dispenser in dispensers)
            {
                int available = MachineFeedDispenser.GetAnimalKcal(dispenser);
                if (available <= 0) continue;
                availableKcal += available;
                if (!targetChosen)
                {
                    targetRatio = MachineFeedDispenser.GetTargetRatio(dispenser);
                    targetChosen = true;
                }
            }
            float nativeTarget = result.MaxHunger * targetRatio;
            double feederNeed = Math.Max(0d,
                (double)(nativeTarget - result.HungerBeforeFood));
            result.FeederRate = Rate(availableKcal, animals, feederNeed);
            long kcalRequested = result.FeederRate >= 1d
                ? Math.Min(availableKcal, Ceiling(feederNeed)) : availableKcal;
            double ratMeatDrawWeight = 0d, prionRatMeatDrawWeight = 0d;
            foreach (GameItem dispenser in dispensers)
            {
                if (kcalRequested <= 0) break;
                int available = MachineFeedDispenser.GetAnimalKcal(dispenser);
                int ask = (int)Math.Min(kcalRequested, available);
                if (ask <= 0) continue;
                int storedTotal = dispenser.GetTagReadonly("STORED_KCAL_TAG").GetInt();
                int storedRatMeat = dispenser.GetTagReadonly(storedSpecies).GetInt();
                bool prionMarked = dispenser.IsTag(storedPrion);
                MachineFeedDispenser.DrawKcal(dispenser, ask);
                int after = Math.Max(0, MachineFeedDispenser.GetAnimalKcal(dispenser));
                int drawn = Math.Max(0, available - after);
                result.FeederKcal += drawn;
                kcalRequested -= drawn;
                double categoryChance = storedTotal > 0
                    ? Math.Max(0d, Math.Min(1d, (double)storedRatMeat / storedTotal)) : 0d;
                ratMeatDrawWeight += drawn * categoryChance;
                if (prionMarked) prionRatMeatDrawWeight += drawn * categoryChance;
            }
            if (result.FeederKcal > 0)
            {
                result.RatMeatDrawChance = ratMeatDrawWeight / result.FeederKcal;
                result.PrionTaggedRatMeatDrawChance = prionRatMeatDrawWeight
                    / result.FeederKcal;
            }
            result.FeederPrionRate = result.FeederRate
                * result.PrionTaggedRatMeatDrawChance;

            float nativeLooseTarget = result.MaxHunger * 0.75f;
            double looseNeed = Math.Max(0d,
                (double)(nativeLooseTarget - result.HungerBeforeFood));
            long looseCount = CountLooseFood(inventory, species,
                out long certainPrion, out long possiblePrion);
            result.LooseFoodUnits = looseCount;
            result.CertainPrionRatMeatUnits = certainPrion;
            result.PossiblePrionRatMeatUnits = possiblePrion;
            double remainingAnimals = (1d - result.FeederRate) * animals;
            result.LooseRate = Rate(looseCount, remainingAnimals, looseNeed);
            result.HungerRate = Math.Max(0d,
                Math.Min(1d, (1d - result.FeederRate) * (1d - result.LooseRate)));
            result.Immunity = GeneticHelper.GetCurrentStat(proxy, "ANIMAL_IMMUNITY_TAG");
            result.NativeImmunity = float.IsFinite(result.Immunity)
                ? Math.Max(0, Math.Min(100,
                    UnityEngine.Mathf.RoundToInt(result.Immunity))) : 0;
            double loosePrionShare = looseCount > 0
                ? (certainPrion + possiblePrion
                    * ((100d - result.NativeImmunity) / 100d))
                    / looseCount : 0d;
            result.LoosePrionRate = result.LooseRate * (1d - result.FeederRate)
                * Math.Max(0d, Math.Min(1d, loosePrionShare));
            result.PrionIncrement = Math.Min(1d,
                result.FeederPrionRate + result.LoosePrionRate);
            long requestedItems = result.LooseRate > 0d
                ? Math.Min(looseCount, Ceiling(looseCount / looseNeed)) : 0L;
            double looseEnergy = ConsumeLooseFood(inventory, requestedItems, out long consumed);
            result.LooseItems = consumed;
            result.EnergyUsed = result.FeederKcal + looseEnergy;
            result.HungerGainPerAnimal = Math.Min(result.MaxHunger - result.HungerBeforeFood,
                result.EnergyUsed / animals);
            RatButcherPatch.Observer?.RecordAnimal("group feeding: animals=" + animals
                + ", feeder_rate=" + result.FeederRate.ToString("G9", CultureInfo.InvariantCulture)
                + ", loose_rate=" + result.LooseRate.ToString("G9", CultureInfo.InvariantCulture)
                + ", hunger_rate=" + result.HungerRate.ToString("G9", CultureInfo.InvariantCulture)
                + ", feeder_kcal=" + result.FeederKcal + ", loose_items=" + consumed
                + ", energy_used=" + result.EnergyUsed.ToString("G9", CultureInfo.InvariantCulture)
                + ", hunger_gain_each=" + result.HungerGainPerAnimal.ToString("G9", CultureInfo.InvariantCulture)
                + ", rat_meat_draw_chance=" + result.RatMeatDrawChance.ToString("G9", CultureInfo.InvariantCulture)
                + ", prion_feeder_rate=" + result.FeederPrionRate.ToString("G9", CultureInfo.InvariantCulture)
                + ", loose_T=" + looseCount + ", loose_T1=" + certainPrion
                + ", loose_T2=" + possiblePrion
                + ", immunity=" + result.Immunity.ToString("G9", CultureInfo.InvariantCulture)
                + ", native_immunity=" + result.NativeImmunity
                + ", prion_loose_rate=" + result.LoosePrionRate.ToString("G9", CultureInfo.InvariantCulture));
            return result;
        }

        private static double Rate(double available, double animals, double need)
        {
            if (available <= 0d || animals <= 0d || need <= 0d) return 0d;
            return Math.Min(1d, available / (animals * need));
        }

        private static long Ceiling(double value) => value <= 0d || double.IsNaN(value) ? 0L
            : value >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(value);

        private static List<GameItem> ReachableDispensers(GameInventory inventory)
        {
            var result = new List<GameItem>();
            if (inventory == null) return result;
            var seen = new HashSet<GameItem>();
            GameItem slotted = AnimalCage.GetSlottedDispenser(inventory);
            if (slotted != null && seen.Add(slotted)) result.Add(slotted);
            foreach (GameItem item in inventory.childItems)
                if (item != null && item.IsTag("FEED_DISPENSER_TAG") && seen.Add(item))
                    result.Add(item);
            return result;
        }

        private static bool IsLooseFood(GameItem item) => item != null
            && !GutterflowPelletHelper.IsPellet(item)
            && !IndustrialFishFeedHelper.IsFeed(item)
            && item.IsTag("CALORIE_VALUE_TAG") && !item.IsTag("CANNED_FOOD_TAG");

        private static long CountLooseFood(GameInventory inventory, string species,
            out long certainPrion, out long possiblePrion)
        {
            long total = 0;
            certainPrion = 0;
            possiblePrion = 0;
            if (inventory == null) return total;
            foreach (GameItem item in inventory.childItems)
                if (IsLooseFood(item) && item.unitCount > 0)
                {
                    total = total > long.MaxValue - item.unitCount
                        ? long.MaxValue : total + item.unitCount;
                    if (item.GetTagReadonly("MEAT_TYPE_TAG").GetString() != species) continue;
                    if (item.IsTag("ANIMAL_PRION_TAG"))
                        certainPrion += item.unitCount;
                    else possiblePrion += item.unitCount;
                }
            return total;
        }

        /// <summary>Consumes stack units in the same item order as the native feed search.</summary>
        private static double ConsumeLooseFood(GameInventory inventory, long requested,
            out long consumed)
        {
            consumed = 0;
            double energy = 0d;
            if (inventory == null || requested <= 0) return energy;
            foreach (GameItem item in new List<GameItem>(inventory.childItems))
            {
                if (!IsLooseFood(item) || item.unitCount < 1) continue;
                int take = (int)Math.Min(requested - consumed, item.unitCount);
                if (take < 1) break;
                energy += (double)Math.Max(0, item.GetTagReadonly("CALORIE_VALUE_TAG").GetInt()) * take;
                consumed += take;
                if (take == item.unitCount) item.Destroy();
                else item.SetUnitCount(item.unitCount - take);
                if (consumed >= requested) break;
            }
            return energy;
        }
    }
}
