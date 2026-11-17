using System;
using System.Collections.Generic;

namespace StolenMeat
{
    /// <summary>Turns the virtual hunger rate into two visible animal cohorts.</summary>
    internal static class AnimalHungerSplitter
    {
        private sealed class Allocation
        {
            internal int Index;
            internal int Hungry;
            internal decimal Remainder;
            internal double PriorHunger;
        }

        private sealed class TotalRates
        {
            internal double Hungry;
            internal double Fed;
        }

        /// <summary>Resolves previously starving virtual rats before welfare checks.</summary>
        internal static void ResolvePriorStarvation(GameItem source,
            List<AnimalStackState.AnimalRecord> records)
        {
            var hungryByIndex = AllocateHungry(records);
            var totalRates = AllocateTotalRates(records, hungryByIndex, true);
            var fedGains = FedGain(records, hungryByIndex);
            var resolved = new List<AnimalStackState.AnimalRecord>(records.Count);
            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record.WasExtracted || record.IsDead
                    || !AnimalStackState.IsNativeStarving(record))
                {
                    resolved.Add(record);
                    continue;
                }
                if (!hungryByIndex.TryGetValue(i, out int hungryUnits))
                {
                    // No distribution means the expected hunger is the native result.
                    if (AnimalStackState.UpdateStarvation(record,
                        AnimalStackState.Expected(record, "ANIMAL_HUNGER_TAG")))
                        KillPriorStarving(source, record);
                    resolved.Add(record);
                    continue;
                }
                TotalRates rates = totalRates[i];
                fedGains.TryGetValue(AnimalStackState.ProcessingPileKey(record, i), out double fedGain);
                if (hungryUnits > 0)
                {
                    var part = AnimalStackState.CopyUnits(record, hungryUnits);
                    part.TotalHungerRate = rates.Hungry;
                    part.FoodShare -= CurrentDayExpectedFood(record);
                    SetExpectedHunger(part, record.UnfedHunger);
                    part.HungerRate = 1f;
                    part.FedHunger = part.UnfedHunger;
                    part.PendingPrionIncrement = 0d;
                    AnimalStackState.SetHungryCohort(part, true);
                    if (AnimalStackState.UpdateStarvation(part, part.UnfedHunger))
                        KillPriorStarving(source, part);
                    resolved.Add(part);
                }
                int fedUnits = record.Quantity - hungryUnits;
                if (fedUnits > 0)
                {
                    var part = AnimalStackState.CopyUnits(record, fedUnits);
                    part.TotalHungerRate = rates.Fed;
                    float max = part.Tag("ANIMAL_MAX_HUNGER_TAG")?.GetFloat() ?? 0f;
                    if (max <= 0f) max = 1000f;
                    float fedHunger = (float)Math.Min(max,
                        Math.Max(0d, record.UnfedHunger + fedGain));
                    part.FoodShare += Math.Max(0f, fedHunger - record.UnfedHunger)
                        - CurrentDayExpectedFood(record);
                    SetExpectedHunger(part, fedHunger);
                    part.HungerRate = 0f;
                    part.FedHunger = fedHunger;
                    AnimalStackState.SetHungryCohort(part, false);
                    if (AnimalStackState.UpdateStarvation(part, fedHunger))
                    {
                        part.PendingPrionIncrement = 0d;
                        KillPriorStarving(source, part);
                    }
                    resolved.Add(part);
                }
            }
            records.Clear();
            records.AddRange(resolved);
        }

        private static void KillPriorStarving(GameItem source,
            AnimalStackState.AnimalRecord record)
        {
            AnimalStackState.FinalizeVirtualCorpse(source, record, false);
            RatButcherPatch.Observer?.RecordAnimal("native starvation death: source="
                + source.uniqueId + ", units=" + record.Quantity);
        }

        internal static bool SplitOrStore(GameItem source, GameInventory inventory,
            List<AnimalStackState.AnimalRecord> records)
        {
            if (source == null || records == null || records.Count == 0
                || AnimalStackState.Count(records) != source.unitCount) return false;
            var original = new List<AnimalStackState.AnimalRecord>(records.Count);
            foreach (var record in records)
                original.Add(AnimalStackState.CopyUnits(record, record.Quantity));
            var hungryByIndex = AllocateHungry(records);
            var totalRates = AllocateTotalRates(records, hungryByIndex, false);
            var fedGains = FedGain(records, hungryByIndex);
            var hungry = new List<AnimalStackState.AnimalRecord>();
            var normal = new List<AnimalStackState.AnimalRecord>();
            var dead = new List<AnimalStackState.AnimalRecord>();
            bool realStaysHungry = records.Exists(AnimalStackState.IsHungryCohort)
                && !records.Exists(record => !record.WasExtracted
                    && !AnimalStackState.IsHungryCohort(record));
            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record.IsDead)
                {
                    dead.Add(AnimalStackState.CopyUnits(record, record.Quantity));
                    continue;
                }
                if (record.WasExtracted)
                {
                    var part = AnimalStackState.CopyUnits(record, record.Quantity);
                    part.PendingPrionIncrement = 0d;
                    AnimalStackState.RecordLatestHealthPanel(part);
                    (realStaysHungry ? hungry : normal).Add(part);
                    continue;
                }
                if (AnimalStackState.IsHungryCohort(record)
                    && !AnimalStackState.IsNativeStarving(record))
                {
                    var part = AnimalStackState.CopyUnits(record, record.Quantity);
                    part.PendingPrionIncrement = 0d;
                    AddVirtualPart(source, part, true, normal, hungry, dead);
                    continue;
                }
                if (!hungryByIndex.TryGetValue(i, out int hungryUnits))
                {
                    var part = AnimalStackState.CopyUnits(record, record.Quantity);
                    AnimalStackState.AddPrionProbability(part, part.PendingPrionIncrement);
                    part.PendingPrionIncrement = 0d;
                    AddVirtualPart(source, part, false, normal, hungry, dead);
                    continue;
                }
                TotalRates rates = totalRates[i];
                fedGains.TryGetValue(AnimalStackState.ProcessingPileKey(record, i), out double fedGain);
                if (hungryUnits > 0)
                {
                    var part = AnimalStackState.CopyUnits(record, hungryUnits);
                    part.TotalHungerRate = rates.Hungry;
                    part.FoodShare -= CurrentDayExpectedFood(record);
                    SetExpectedHunger(part, record.UnfedHunger);
                    part.HungerRate = 1f;
                    part.FedHunger = part.UnfedHunger;
                    part.PendingPrionIncrement = 0d;
                    AnimalStackState.SetHungryCohort(part, true);
                    AddVirtualPart(source, part, true, normal, hungry, dead);
                }
                int fedUnits = record.Quantity - hungryUnits;
                if (fedUnits > 0)
                {
                    var part = AnimalStackState.CopyUnits(record, fedUnits);
                    part.TotalHungerRate = rates.Fed;
                    float max = part.Tag("ANIMAL_MAX_HUNGER_TAG")?.GetFloat() ?? 0f;
                    if (max <= 0f) max = 1000f;
                    float fedHunger = (float)Math.Min(max,
                        Math.Max(0d, record.UnfedHunger + fedGain));
                    part.FoodShare += Math.Max(0f, fedHunger - record.UnfedHunger)
                        - CurrentDayExpectedFood(record);
                    SetExpectedHunger(part, fedHunger);
                    part.HungerRate = 0f;
                    part.FedHunger = fedHunger;
                    AnimalStackState.AddPrionProbability(part, part.PendingPrionIncrement);
                    part.PendingPrionIncrement = 0d;
                    AnimalStackState.SetHungryCohort(part, false);
                    AddVirtualPart(source, part, false, normal, hungry, dead);
                }
            }
            if (dead.Count > 0)
                return SaveWithDeaths(source, inventory,
                    original, normal, hungry, dead);
            if (hungry.Count == 0) return AnimalStackState.Write(source, normal);
            if (normal.Count == 0) return AnimalStackState.Write(source, hungry);

            int originalCount = source.unitCount;
            int hungryCount = (int)AnimalStackState.Count(hungry);
            int normalCount = originalCount - hungryCount;
            GameItem hungryItem = NativeSplitPreparation.Create(source, hungryCount);
            hungryItem.SetUnitCount(hungryCount);
            if (!AnimalStackState.Write(hungryItem, hungry))
            {
                hungryItem.Destroy();
                return AnimalStackState.Write(source, Combine(hungry, normal));
            }
            source.SetUnitCount(normalCount);
            if (!AnimalStackState.Write(source, normal))
            {
                source.SetUnitCount(originalCount);
                AnimalStackState.Write(source, original);
                hungryItem.Destroy();
                return false;
            }
            bool placed = false;
            try
            {
                placed = TryPlaceWhole(inventory, hungryItem);
                if (!placed && OverflowPatch.Coordinator != null)
                    placed = OverflowPatch.Coordinator.TryPlaceAnimalCohort(hungryItem);
            }
            catch (Exception error)
            {
                placed = hungryItem.parentInventory != null;
                RatButcherPatch.Observer?.RecordAnimal("hungry cohort placement error: " + error);
            }
            if (!placed)
            {
                source.SetUnitCount(originalCount);
                bool saved = AnimalStackState.Write(source, Combine(hungry, normal));
                if (hungryItem.parentInventory == null) hungryItem.Destroy();
                RatButcherPatch.Observer?.RecordAnimal("hungry cohort kept inside original stack: "
                    + source.uniqueId + ", hungry=" + hungryCount);
                return saved;
            }
            AnimalStackHooks.MarkAdvancedToday(hungryItem);
            RatButcherPatch.Observer?.RecordAnimal("hungry cohort split: source=" + source.uniqueId
                + ", hungry_item=" + hungryItem.uniqueId + ", hungry=" + hungryCount
                + ", normal=" + normalCount);
            return true;
        }

        private sealed class PileTotals
        {
            internal double Expected, AssignedHungry, FedWeight, Energy;
            internal long FedCount;
        }

        private static Dictionary<int, int> AllocateHungry(
            List<AnimalStackState.AnimalRecord> records)
        {
            var groups = new Dictionary<string, List<Allocation>>(StringComparer.Ordinal);
            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record.WasExtracted || record.IsDead
                    || AnimalStackState.IsHungryCohort(record)
                        && !AnimalStackState.IsNativeStarving(record)
                    || !record.HasHungerDistribution) continue;
                string key = AnimalStackState.ProcessingPileKey(record, i);
                if (!groups.TryGetValue(key, out var shares))
                    groups.Add(key, shares = new List<Allocation>());
                decimal raw = record.Quantity * (decimal)record.HungerRate;
                int whole = (int)decimal.Floor(raw);
                shares.Add(new Allocation { Index = i, Hungry = whole,
                    Remainder = raw - whole, PriorHunger = record.TotalHungerRate });
            }
            var result = new Dictionary<int, int>();
            foreach (var shares in groups.Values)
            {
                decimal remainderSum = 0m;
                foreach (var share in shares) remainderSum += share.Remainder;
                shares.Sort((left, right) => {
                    int remainder = right.Remainder.CompareTo(left.Remainder);
                    return remainder != 0 ? remainder : right.PriorHunger.CompareTo(left.PriorHunger);
                });
                int extras = (int)decimal.Floor(remainderSum);
                for (int i = 0; i < extras; i++) shares[i].Hungry++;
                foreach (var share in shares) result.Add(share.Index, share.Hungry);
            }
            return result;
        }

        private static Dictionary<string, double> FedGain(
            List<AnimalStackState.AnimalRecord> records, Dictionary<int, int> hungryByIndex)
        {
            var totals = new Dictionary<string, PileTotals>(StringComparer.Ordinal);
            foreach (var pair in hungryByIndex)
            {
                var record = records[pair.Key];
                string key = AnimalStackState.ProcessingPileKey(record, pair.Key);
                if (!totals.TryGetValue(key, out var total))
                    totals.Add(key, total = new PileTotals());
                total.FedCount += record.Quantity - pair.Value;
                total.Energy += Math.Max(0d, AnimalStackState.Expected(record, "ANIMAL_HUNGER_TAG")
                    - record.UnfedHunger) * record.Quantity;
            }
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var pair in totals)
                result.Add(pair.Key, pair.Value.FedCount > 0
                    ? pair.Value.Energy / pair.Value.FedCount : 0d);
            return result;
        }

        /// <summary>Distributes saved hunger rates within each pile.</summary>
        private static Dictionary<int, TotalRates> AllocateTotalRates(
            List<AnimalStackState.AnimalRecord> records,
            Dictionary<int, int> hungryByIndex, bool priorStarvingOnly)
        {
            var totals = new Dictionary<string, PileTotals>(StringComparer.Ordinal);
            foreach (var pair in hungryByIndex)
            {
                var record = records[pair.Key];
                if (priorStarvingOnly && !AnimalStackState.IsNativeStarving(record)) continue;
                string key = AnimalStackState.ProcessingPileKey(record, pair.Key);
                if (!totals.TryGetValue(key, out var total))
                    totals.Add(key, total = new PileTotals());
                total.Expected += record.TotalHungerRate * record.Quantity;
                double prior = record.HungerRate > 0d
                    ? Math.Min(1d, record.TotalHungerRate / record.HungerRate) : 0d;
                total.AssignedHungry += pair.Value * prior;
                total.FedWeight += (record.Quantity - pair.Value) * record.TotalHungerRate;
            }
            var result = new Dictionary<int, TotalRates>();
            foreach (var pair in hungryByIndex)
            {
                var record = records[pair.Key];
                if (priorStarvingOnly && !AnimalStackState.IsNativeStarving(record)) continue;
                var total = totals[AnimalStackState.ProcessingPileKey(record, pair.Key)];
                double scale = total.AssignedHungry > total.Expected && total.AssignedHungry > 0d
                    ? total.Expected / total.AssignedHungry : 1d;
                double residual = Math.Max(0d, total.Expected - total.AssignedHungry * scale);
                result.Add(pair.Key, new TotalRates {
                    Hungry = record.HungerRate > 0d
                        ? Math.Min(1d, record.TotalHungerRate / record.HungerRate) * scale : 0d,
                    Fed = total.FedWeight > 0d ? Math.Max(0d, Math.Min(1d,
                        residual * record.TotalHungerRate / total.FedWeight)) : 0d
                });
            }
            return result;
        }
        private static float CurrentDayExpectedFood(
            AnimalStackState.AnimalRecord record) => Math.Max(0f,
                AnimalStackState.Expected(record, "ANIMAL_HUNGER_TAG") - record.UnfedHunger);

        private static void AddVirtualPart(GameItem source,
            AnimalStackState.AnimalRecord part, bool unfed,
            List<AnimalStackState.AnimalRecord> normal,
            List<AnimalStackState.AnimalRecord> hungry,
            List<AnimalStackState.AnimalRecord> dead)
        {
            if (part.IsDead) { dead.Add(part); return; }
            float hunger = AnimalStackState.Expected(part, "ANIMAL_HUNGER_TAG");
            bool wasStarving = AnimalStackState.IsNativeStarving(part);
            if (AnimalStackState.UpdateStarvation(part, hunger))
            {
                AnimalStackState.FinalizeVirtualCorpse(source, part);
                dead.Add(part);
                RatButcherPatch.Observer?.RecordAnimal("native starvation death: source="
                    + source.uniqueId + ", units=" + part.Quantity);
                return;
            }
            if (wasStarving && hunger > 0f)
                RatButcherPatch.Observer?.RecordAnimal("native starvation cleared: source="
                    + source.uniqueId + ", units=" + part.Quantity);
            else if (!wasStarving && hunger <= 0f)
                RatButcherPatch.Observer?.RecordAnimal("native starvation marked: source="
                    + source.uniqueId + ", units=" + part.Quantity);
            if (!unfed) AnimalStackState.SetHungryCohort(part, false);
            AnimalStackState.RecordLatestHealthPanel(part);
            (unfed ? hungry : normal).Add(part);
        }

        private static bool SaveWithDeaths(GameItem source,
            GameInventory inventory,
            List<AnimalStackState.AnimalRecord> original,
            List<AnimalStackState.AnimalRecord> normal,
            List<AnimalStackState.AnimalRecord> hungry,
            List<AnimalStackState.AnimalRecord> dead)
        {
            int originalCount = source.unitCount;
            int deadCount = (int)AnimalStackState.Count(dead);
            int liveCount = originalCount - deadCount;
            if (liveCount == 0)
            {
                if (AnimalStackState.Write(source, dead)) return true;
                source.SetUnitCount(originalCount);
                AnimalStackState.Write(source, original);
                return false;
            }
            GameItem corpses = NativeSplitPreparation.Create(source, deadCount);
            corpses.SetUnitCount(deadCount);
            if (!AnimalStackState.Write(corpses, dead))
            {
                corpses.Destroy();
                RatButcherPatch.Observer?.RecordAnimal("animal corpse save failed: units="
                    + deadCount);
                return false;
            }
            source.SetUnitCount(liveCount);
            if (!SaveLivingParts(source, inventory, normal, hungry))
            {
                source.SetUnitCount(originalCount);
                AnimalStackState.Write(source, original);
                corpses.Destroy();
                return false;
            }
            bool placed = false;
            try
            {
                placed = inventory != null && GraphUtils.TryAcceptAll(inventory,
                    corpses, -1) == 0;
                if (!placed && OverflowPatch.Coordinator != null)
                    placed = OverflowPatch.Coordinator.TryRescue(corpses,
                        "animal death full");
            }
            catch (Exception error)
            {
                RatButcherPatch.Observer?.RecordAnimal(
                    "animal corpse placement failed: " + error);
            }
            if (!placed && corpses.parentInventory == null) corpses.Destroy();
            RatButcherPatch.Observer?.RecordAnimal("animal corpses: source="
                + source.uniqueId + ", units=" + deadCount + ", outcome="
                + (placed ? "placed" : "discarded"));
            return true;
        }

        private static bool SaveLivingParts(GameItem source, GameInventory inventory,
            List<AnimalStackState.AnimalRecord> normal,
            List<AnimalStackState.AnimalRecord> hungry)
        {
            if (hungry.Count == 0) return AnimalStackState.Write(source, normal);
            if (normal.Count == 0) return AnimalStackState.Write(source, hungry);
            int liveCount = source.unitCount;
            int hungryCount = (int)AnimalStackState.Count(hungry);
            int normalCount = liveCount - hungryCount;
            GameItem hungryItem = NativeSplitPreparation.Create(source, hungryCount);
            hungryItem.SetUnitCount(hungryCount);
            if (!AnimalStackState.Write(hungryItem, hungry))
            {
                hungryItem.Destroy();
                return AnimalStackState.Write(source, Combine(hungry, normal));
            }
            source.SetUnitCount(normalCount);
            if (!AnimalStackState.Write(source, normal))
            {
                source.SetUnitCount(liveCount);
                hungryItem.Destroy();
                return false;
            }
            bool placed = false;
            try
            {
                placed = TryPlaceWhole(inventory, hungryItem);
                if (!placed && OverflowPatch.Coordinator != null)
                    placed = OverflowPatch.Coordinator.TryPlaceAnimalCohort(hungryItem);
            }
            catch (Exception error)
            {
                placed = hungryItem.parentInventory != null;
                RatButcherPatch.Observer?.RecordAnimal(
                    "hungry cohort placement error: " + error);
            }
            if (!placed)
            {
                source.SetUnitCount(liveCount);
                bool saved = AnimalStackState.Write(source, Combine(hungry, normal));
                if (hungryItem.parentInventory == null) hungryItem.Destroy();
                return saved;
            }
            AnimalStackHooks.MarkAdvancedToday(hungryItem);
            return true;
        }

        private static List<AnimalStackState.AnimalRecord> Combine(
            List<AnimalStackState.AnimalRecord> hungry,
            List<AnimalStackState.AnimalRecord> normal)
        {
            var both = new List<AnimalStackState.AnimalRecord>(hungry.Count + normal.Count);
            both.AddRange(hungry);
            both.AddRange(normal);
            return both;
        }

        private static void SetExpectedHunger(AnimalStackState.AnimalRecord record,
            float desired)
        {
            float current = AnimalStackState.Expected(record, "ANIMAL_HUNGER_TAG");
            record.VirtualHungerDelta += desired - current;
            record.UnfedHunger = desired;
        }

        private static bool TryPlaceWhole(GameInventory inventory, GameItem item)
        {
            if (inventory == null) return false;
            bool previous = ItemStackPolicy.SuppressStackingForSplit;
            ItemStackPolicy.SuppressStackingForSplit = true;
            try
            {
                SlotMarker slot = inventory.TryFindOneValidInventorySlot(item);
                return slot != null && slot.targetItem == null && slot.itemGridShape != null
                    && slot.numTransfer >= item.unitCount
                    && slot.TryAcceptOnce(item.unitCount) == item.unitCount;
            }
            finally { ItemStackPolicy.SuppressStackingForSplit = previous; }
        }
    }
}
