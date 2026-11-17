using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StolenMeat
{
    /// <summary>Updates fertility for each saved rat cohort after the native welfare steps.</summary>
    internal static class AnimalFertilityCoordinator
    {
        private const string FertilityTag = "ANIMAL_FERTILITY_TAG";
        private const string CurrentFertilityTag = "ANIMAL_CURRENT_FERTILITY_TAG";
        internal const string VirtualFertilityTag = "STOLEN_MEAT_VIRTUAL_FERTILITY";

        /// <summary>Applies the expected loss to the game's genetic fertility value.</summary>
        internal static float Calculate(float current, float expressed, float hunger,
            double hungryRate, bool old, int crowding, double diseaseRate)
        {
            if (!float.IsFinite(current) || !float.IsFinite(expressed)
                || !float.IsFinite(hunger) || double.IsNaN(hungryRate)
                || double.IsNaN(diseaseRate)) return 0f;
            double s = Math.Max(0d, Math.Min(200d, hunger));
            double p = Math.Max(0d, Math.Min(1d, hungryRate));
            double b = Math.Max(0d, Math.Min(1d, diseaseRate));
            double hungerLoss = hunger < 200f
                ? 0.002d * s * (p - 1d) + 0.4d : 0.4d * p;
            double loss = hungerLoss + (old ? 0.15d : 0d)
                + 0.2d * Math.Max(0, crowding) + 0.75d * b;
            return (float)Math.Min(float.MaxValue,
                Math.Max(0d, (double)current - expressed * loss));
        }

        private sealed class PileInputs
        {
            internal long Count;
            internal double Hunger;
            internal double HungerRate;
        }

        private static string PileKey(AnimalStackState.AnimalRecord record, int index)
        {
            return AnimalStackState.ProcessingPileKey(record, index);
        }

        /// <summary>Reads the pile-level virtual value and the native value for real rats.</summary>
        internal static float AverageForDisplay(List<AnimalStackState.AnimalRecord> records)
        {
            long count = AnimalStackState.Count(records);
            if (count == 0) return 0f;
            double sum = 0d;
            foreach (var record in records)
            {
                TagState tag = record.WasExtracted ? record.Tag(CurrentFertilityTag)
                    : record.Tag(VirtualFertilityTag) ?? record.Tag(CurrentFertilityTag);
                sum += (double)(tag?.GetFloat() ?? 0f) * record.Quantity / count;
            }
            return (float)sum;
        }
        /// <summary>Updates each virtual parent record and lets real rats use native code.</summary>
        internal static void Update(GameItem source, GameInventory inventory,
            List<AnimalStackState.AnimalRecord> records)
        {
            int crowding = AnimalWelfareCoordinator.CrowdingLevel(inventory);
            var log = new StringBuilder(8192);
            var piles = new Dictionary<string, PileInputs>(StringComparer.Ordinal);
            for (int index = 0; index < records.Count; index++)
            {
                var record = records[index];
                if (record.IsDead || record.WasExtracted) continue;
                string key = PileKey(record, index);
                if (!piles.TryGetValue(key, out var pile))
                {
                    pile = new PileInputs();
                    piles.Add(key, pile);
                }
                pile.Count += record.Quantity;
                pile.Hunger += (double)AnimalStackState.Expected(record, "ANIMAL_HUNGER_TAG") * record.Quantity;
                pile.HungerRate += record.TotalHungerRate * record.Quantity;
            }
            for (int index = 0; index < records.Count; index++)
            {
                AnimalStackState.AnimalRecord record = records[index];
                if (record.IsDead) continue;
                GameItem proxy = AnimalStackHooks.CreateProxy(source, record);
                try
                {
                    if (record.WasExtracted)
                    {
                        HusbandryHelper.UpdateFertility(proxy, inventory);
                        record.Tags[CurrentFertilityTag] =
                            proxy.GetTagReadonly(CurrentFertilityTag).Clone();
                        Append(log, "fertility native: item=" + source.uniqueId
                            + ", parent="
                            + (record.Tag(AnimalStackState.LineageTag)?.GetString() ?? "")
                            + ", result=" + record.Tag(CurrentFertilityTag).GetFloat()
                                .ToString("G9", CultureInfo.InvariantCulture));
                        continue;
                    }
                    AnimalSpecies species = HusbandryHelper.GetSpecies(proxy);
                    bool old = species != null && species.growthStages != null
                        && species.growthStages.Length > 4
                        && AnimalStackState.CurrentAge(record)
                            >= species.growthStages[4]
                                * GeneticHelper.GetCurrentStat(proxy, "ANIMAL_LONGEVITY_TAG");
                    float current = GeneticHelper.GetCurrentStat(proxy, FertilityTag);
                    float expressed = GeneticHelper.GetExpressedValue(proxy, FertilityTag);
                    PileInputs pile = piles[PileKey(record, index)];
                    float hunger = (float)(pile.Hunger / pile.Count);
                    double hungerRate = pile.HungerRate / pile.Count;
                    float next = Calculate(current, expressed, hunger, hungerRate,
                        old, crowding, record.DiseaseRate);
                    if (!record.Tags.TryGetValue(VirtualFertilityTag, out TagState tag))
                    {
                        tag = new TagState(VirtualFertilityTag, VirtualFertilityTag);
                        record.Tags.Add(VirtualFertilityTag, tag);
                    }
                    tag.Enable();
                    tag.SetFloat(next);
                    Append(log, "fertility updated: item="
                        + source.uniqueId + ", parent="
                        + (record.Tag(AnimalStackState.LineageTag)?.GetString() ?? "")
                        + ", units=" + record.Quantity + ", current="
                        + current.ToString("G9", CultureInfo.InvariantCulture)
                        + ", hunger=" + hunger.ToString("G9", CultureInfo.InvariantCulture)
                        + ", total_hunger_rate=" + hungerRate.ToString("G9", CultureInfo.InvariantCulture)
                        + ", old=" + old + ", crowding=" + crowding
                        + ", disease_rate=" + record.DiseaseRate.ToString("G9", CultureInfo.InvariantCulture)
                        + ", result=" + next.ToString("G9", CultureInfo.InvariantCulture));
                }
                finally { if (proxy.parentInventory == null) proxy.Destroy(); }
            }
            if (log.Length > 0) RatButcherPatch.Observer?.RecordAnimal(log.ToString());
        }

        private static void Append(StringBuilder log, string detail)
        {
            if (log.Length + detail.Length > 8192)
            {
                RatButcherPatch.Observer?.RecordAnimal(log.ToString());
                log.Clear();
            }
            log.Append(detail).Append('\n');
        }
    }
}
