using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace StolenMeat
{
    /// <summary>Persists each animal's native state inside its visible stack item.</summary>
    internal static class AnimalStackState
    {
        internal const string PayloadTag = "STOLEN_MEAT_ANIMAL_STACK_V1";
        internal const string LineageTag = "STOLEN_MEAT_PARENT_GROUP";
        internal const string PregnancyLineageTag = "STOLEN_MEAT_PREGNANCY_GROUP";
        internal const string ExtractedTag = "STOLEN_MEAT_EXTRACTED_RAT";
        internal const string IdentityTag = "STOLEN_MEAT_RAT_ID";
        internal const string HungryCohortTag = "STOLEN_MEAT_HUNGRY_GROUP";
        internal const string PrionTag = "ANIMAL_PRION_TAG";
        internal const string DiseaseTag = "ANIMAL_DISEASED_TAG";
        internal const string StarvingTag = "ANIMAL_IS_STARVING_TAG";
        private const string GroupLineageTag = "STOLEN_MEAT_GROUP_LINEAGE";
        internal const int MaxAnimalsPerStack = int.MaxValue;
        internal const int MaxIndividualRecords = 4096;
        internal const int MaxRealAnimalsPerContainer = 128;
        private const int MaxHealthEvents = 4096;
        private const int MaxPayloadCharacters = 32 * 1024 * 1024;
        private static bool compressingContainer;
        private static readonly ConditionalWeakTable<GameItem, Validity> Validated =
            new ConditionalWeakTable<GameItem, Validity>();
        private static readonly Random Samples = new Random();

        private static bool ValidHealthEvent(HealthEvent item) => item != null
            && !double.IsNaN(item.InfectionChance) && item.InfectionChance >= 0d
            && item.InfectionChance <= 1d && !double.IsInfinity(item.InfectionChance)
            && float.IsFinite(item.Regen) && float.IsFinite(item.DiseaseDamage)
            && float.IsFinite(item.MaxHealth) && item.MaxHealth >= 0f
            && float.IsFinite(item.Growth) && float.IsFinite(item.AlternativeGrowth)
            && !double.IsNaN(item.GrowthProbability) && item.GrowthProbability >= 0d
            && item.GrowthProbability <= 1d && !double.IsInfinity(item.GrowthProbability)
            && float.IsFinite(item.Hunger)
            && !double.IsNaN(item.PrionRate) && item.PrionRate >= 0d
            && item.PrionRate <= 1d && !double.IsInfinity(item.PrionRate)
            && !double.IsNaN(item.CureChance) && item.CureChance >= 0d
            && item.CureChance <= 1d && !double.IsInfinity(item.CureChance);

        private sealed class Validity
        {
            internal string Payload;
            internal int Count;
        }

        internal sealed class AnimalRecord
        {
            internal readonly Dictionary<string, TagState> Tags = new Dictionary<string, TagState>(StringComparer.Ordinal);
            internal readonly Dictionary<string, double> Variances =
                new Dictionary<string, double>(StringComparer.Ordinal);
            internal readonly Dictionary<string, Dictionary<string, double>> Choices =
                new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
            internal int Quantity = 1;
            internal string SpritePath;
            internal string CustomText;
            internal long UnitValue;
            internal int DeferredDays;
            internal float VirtualGrowth;
            internal float VirtualHungerDelta;
            internal float FoodShare;
            internal bool HasHungerDistribution;
            /// <summary>Tonight's allocation rate; never serialized in version 8.</summary>
            internal double HungerRate;
            /// <summary>Saved probability after multiplying nightly unfed rates.</summary>
            internal double TotalHungerRate = 1d;
            internal float UnfedHunger;
            internal float FedHunger;
            internal double PrionRate;
            internal double AppliedPrionFraction;
            internal double PendingPrionIncrement;
            internal double DiseaseRate;
            internal float HealthStart;
            internal double DiseaseStart;
            internal float HealthStartGrowth;
            internal int HealthStartDays;
            internal bool PendingGrowthEvent;
            internal readonly List<HealthEvent> HealthHistory = new List<HealthEvent>();
            internal bool PendingNativeDeath;

            internal TagState Tag(string id) => Tags.TryGetValue(id, out TagState tag) ? tag : null;
            internal bool IsDead => Tag("ANIMAL_IS_DEAD_TAG")?.valueEnabled == true;
            internal bool WasExtracted => Tag(ExtractedTag)?.valueEnabled == true;
        }

        /// <summary>One native-order health update deferred until individual extraction.</summary>
        internal sealed class HealthEvent
        {
            internal double InfectionChance;
            internal float Regen;
            internal float DiseaseDamage;
            internal float MaxHealth;
            internal double CureChance;
            internal float Growth;
            internal bool HasGrowthDistribution;
            internal float AlternativeGrowth;
            internal double GrowthProbability;
            internal float Hunger;
            internal double PrionRate;
        }

        internal sealed class MergeSnapshot
        {
            internal int TargetCount;
            internal List<AnimalRecord> Source;
            internal List<AnimalRecord> Target;
            internal int Amount;
        }

        internal static bool IsValid(GameItem item)
        {
            if (item == null || item.unitCount < 1) return false;
            if (!ItemStackPolicy.IsAnimal(item)) return true;
            string payload = item.GetTagReadonly(PayloadTag).GetString();
            if (string.IsNullOrEmpty(payload)) return true;
            if (payload.Length > MaxPayloadCharacters) return false;
            if (Validated.TryGetValue(item, out Validity previous)
                && previous.Count == item.unitCount
                && string.Equals(previous.Payload, payload, StringComparison.Ordinal)) return true;
            if (!TryRead(item, out _)) return false;
            Validated.Remove(item);
            Validated.Add(item, new Validity { Count = item.unitCount, Payload = payload });
            return true;
        }

        /// <summary>Reads the serialized records; a native unmodified stack repeats its visible state.</summary>
        internal static bool TryRead(GameItem item, out List<AnimalRecord> records)
        {
            records = null;
            if (item == null || item.unitCount < 1) return false;
            string payload = item.GetTagReadonly(PayloadTag).GetString();
            if (payload != null && payload.Length > MaxPayloadCharacters) return false;
            if (string.IsNullOrEmpty(payload))
            {
                if (item.unitCount > MaxIndividualRecords)
                {
                    AnimalRecord compact = Capture(item);
                    compact.Quantity = item.unitCount;
                    compact.Tags.Remove(ExtractedTag);
                    compact.Tags.Remove(IdentityTag);
                    records = new List<AnimalRecord> { compact };
                }
                else
                {
                    records = new List<AnimalRecord>(item.unitCount);
                    for (int i = 0; i < item.unitCount; i++) records.Add(Capture(item));
                }
                return true;
            }
            try
            {
                byte[] bytes = Convert.FromBase64String(payload);
                using (var stream = new MemoryStream(bytes, false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    byte version = reader.ReadByte();
                    if ((version != 1 && version != 2 && version != 3 && version != 4
                            && version != 5 && version != 6 && version != 7
                            && version != 8 && version != 9)
                        || reader.ReadInt32() != item.unitCount) return false;
                    int recordCount = version >= 2 ? reader.ReadInt32() : item.unitCount;
                    if (recordCount < 1 || recordCount > MaxIndividualRecords) return false;
                    records = new List<AnimalRecord>(recordCount);
                    long total = 0;
                    for (int i = 0; i < recordCount; i++)
                    {
                        var record = new AnimalRecord
                        {
                            Quantity = version >= 2 ? reader.ReadInt32() : 1,
                            SpritePath = reader.ReadString(),
                            CustomText = reader.ReadString(),
                            UnitValue = reader.ReadInt64(),
                            DeferredDays = reader.ReadInt32(),
                            VirtualGrowth = reader.ReadSingle(),
                            VirtualHungerDelta = reader.ReadSingle(),
                            FoodShare = reader.ReadSingle()
                        };
                        if (version >= 3)
                        {
                            record.HasHungerDistribution = reader.ReadBoolean();
                            if (version < 8) record.HungerRate = reader.ReadDouble();
                            record.UnfedHunger = reader.ReadSingle();
                            record.FedHunger = reader.ReadSingle();
                            if (version >= 7) record.TotalHungerRate = reader.ReadDouble();
                        }
                        if (version >= 4)
                        {
                            record.PrionRate = reader.ReadDouble();
                            record.AppliedPrionFraction = reader.ReadDouble();
                        }
                        if (version >= 5)
                        {
                            record.DiseaseRate = reader.ReadDouble();
                            record.HealthStart = reader.ReadSingle();
                            record.DiseaseStart = reader.ReadDouble();
                            if (version >= 6)
                            {
                                record.HealthStartGrowth = reader.ReadSingle();
                                record.HealthStartDays = reader.ReadInt32();
                            }
                            int eventCount = reader.ReadInt32();
                            if (eventCount < 0 || eventCount > MaxHealthEvents) return false;
                            for (int eventIndex = 0; eventIndex < eventCount; eventIndex++)
                            {
                                var healthEvent = new HealthEvent
                                {
                                    InfectionChance = reader.ReadDouble(),
                                    Regen = reader.ReadSingle(),
                                    DiseaseDamage = reader.ReadSingle(),
                                    MaxHealth = reader.ReadSingle(),
                                    CureChance = reader.ReadDouble()
                                };
                                if (version >= 6)
                                {
                                    healthEvent.Growth = reader.ReadSingle();
                                    healthEvent.Hunger = reader.ReadSingle();
                                    healthEvent.PrionRate = reader.ReadDouble();
                                }
                                if (version >= 9)
                                {
                                    healthEvent.HasGrowthDistribution = reader.ReadBoolean();
                                    healthEvent.AlternativeGrowth = reader.ReadSingle();
                                    healthEvent.GrowthProbability = reader.ReadDouble();
                                }
                                if (!ValidHealthEvent(healthEvent)) return false;
                                record.HealthHistory.Add(healthEvent);
                            }
                        }
                        int tagCount = reader.ReadInt32();
                        if (record.Quantity < 1 || tagCount < 0 || tagCount > 512 || record.DeferredDays < 0
                            || float.IsNaN(record.VirtualGrowth) || float.IsInfinity(record.VirtualGrowth)
                            || float.IsNaN(record.VirtualHungerDelta) || float.IsInfinity(record.VirtualHungerDelta)
                            || double.IsNaN(record.HungerRate) || record.HungerRate < 0d
                            || record.HungerRate > 1d || double.IsInfinity(record.HungerRate)
                            || double.IsNaN(record.TotalHungerRate)
                            || record.TotalHungerRate < 0d || record.TotalHungerRate > 1d
                            || double.IsInfinity(record.TotalHungerRate)
                            || double.IsNaN(record.PrionRate) || record.PrionRate < 0d
                            || record.PrionRate > 1d || double.IsInfinity(record.PrionRate)
                            || double.IsNaN(record.AppliedPrionFraction)
                            || record.AppliedPrionFraction < 0d
                            || record.AppliedPrionFraction > 1d
                            || double.IsInfinity(record.AppliedPrionFraction)
                            || double.IsNaN(record.DiseaseRate) || record.DiseaseRate < 0d
                            || record.DiseaseRate > 1d || double.IsInfinity(record.DiseaseRate)
                            || double.IsNaN(record.DiseaseStart) || record.DiseaseStart < 0d
                            || record.DiseaseStart > 1d || double.IsInfinity(record.DiseaseStart)
                            || !float.IsFinite(record.HealthStart)
                            || !float.IsFinite(record.HealthStartGrowth)
                            || record.HealthStartDays < 0
                            || !float.IsFinite(record.UnfedHunger)
                            || !float.IsFinite(record.FedHunger)) return false;
                        for (int j = 0; j < tagCount; j++)
                        {
                            string key = reader.ReadString();
                            var tag = new TagState(key, reader.ReadString())
                            {
                                valueEnabled = reader.ReadBoolean(),
                                valueString = reader.ReadString(),
                                valueInt = reader.ReadInt32(),
                                valueFloat = reader.ReadSingle(),
                                valueLong = reader.ReadInt64(),
                                valueDouble = reader.ReadDouble(),
                                valueBool = reader.ReadBoolean()
                            };
                            if (!record.Tags.TryAdd(key, tag)) return false;
                        }
                        if (version >= 2)
                        {
                            int varianceCount = reader.ReadInt32();
                            if (varianceCount < 0 || varianceCount > 2048) return false;
                            for (int j = 0; j < varianceCount; j++)
                            {
                                string key = reader.ReadString();
                                double variance = reader.ReadDouble();
                                if (variance < 0d || double.IsNaN(variance)
                                    || double.IsInfinity(variance)
                                    || !record.Variances.TryAdd(key, variance)) return false;
                            }
                        }
                        if (version >= 6)
                        {
                            int choiceCount = reader.ReadInt32();
                            if (choiceCount < 0 || choiceCount > 2048) return false;
                            for (int choiceIndex = 0; choiceIndex < choiceCount; choiceIndex++)
                            {
                                string key = reader.ReadString();
                                int optionCount = reader.ReadInt32();
                                if (optionCount < 1 || optionCount > 4096
                                    || record.Choices.ContainsKey(key)) return false;
                                var options = new Dictionary<string, double>(StringComparer.Ordinal);
                                for (int optionIndex = 0; optionIndex < optionCount; optionIndex++)
                                {
                                    string value = reader.ReadString();
                                    double probability = reader.ReadDouble();
                                    if (probability < 0d || double.IsNaN(probability)
                                        || double.IsInfinity(probability)
                                        || !options.TryAdd(value, probability)) return false;
                                }
                                record.Choices.Add(key, options);
                            }
                        }
                        else if (version == 5 && record.HealthHistory.Count > 0)
                        {
                            record.HealthStartGrowth = record.VirtualGrowth;
                            record.HealthStartDays = record.DeferredDays;
                        }
                        if (version < 4)
                        {
                            if (record.Variances.TryGetValue("P:" + PrionTag, out double prior))
                            {
                                prior = Math.Max(0d, Math.Min(1d, prior));
                                record.PrionRate = prior;
                                record.AppliedPrionFraction = prior;
                                record.Variances.Remove("P:" + PrionTag);
                                if (prior < 1d) record.Tags.Remove(PrionTag);
                            }
                            else if (record.Tag(PrionTag)?.valueEnabled == true)
                                record.PrionRate = record.AppliedPrionFraction = 1d;
                        }
                        if (version < 5)
                            record.DiseaseRate = record.Tag(DiseaseTag)?.valueEnabled == true ? 1d : 0d;
                        if (version < 7 && record.HasHungerDistribution)
                            record.TotalHungerRate = record.HungerRate;
                        total += record.Quantity;
                        if (total > item.unitCount) return false;
                        records.Add(record);
                    }
                    if (total != item.unitCount || stream.Position != stream.Length) return false;
                    if (item.unitCount <= AnimalPopulationPolicy.ForcedUnifiedAbove)
                        ExpandSmall(records);
                    return true;
                }
            }
            catch (Exception)
            {
                records = null;
                return false;
            }
        }

        private static AnimalRecord Capture(GameItem item)
        {
            ClampNativeHealth(item);
            var record = new AnimalRecord
            {
                SpritePath = item.spritePath ?? string.Empty,
                CustomText = item.customText ?? string.Empty,
                UnitValue = item.unitValue,
                PrionRate = item.IsTag(PrionTag) ? 1d : 0d,
                AppliedPrionFraction = item.IsTag(PrionTag) ? 1d : 0d,
                DiseaseRate = item.IsTag(DiseaseTag) ? 1d : 0d
            };
            foreach (var pair in item.modifiedState.dict)
                if (pair.Key != PayloadTag && pair.Key != GroupLineageTag && pair.Value != null)
                    record.Tags.Add(pair.Key, pair.Value.Clone());
            return record;
        }

        internal static long Count(List<AnimalRecord> records)
        {
            if (records == null) return 0;
            long total = 0;
            foreach (AnimalRecord record in records) total += record.Quantity;
            return total;
        }

        private static AnimalRecord Copy(AnimalRecord source, int quantity)
        {
            var result = new AnimalRecord
            {
                Quantity = quantity,
                SpritePath = source.SpritePath,
                CustomText = source.CustomText,
                UnitValue = source.UnitValue,
                DeferredDays = source.DeferredDays,
                VirtualGrowth = source.VirtualGrowth,
                VirtualHungerDelta = source.VirtualHungerDelta,
                FoodShare = source.FoodShare,
                HasHungerDistribution = source.HasHungerDistribution,
                HungerRate = source.HungerRate,
                TotalHungerRate = source.TotalHungerRate,
                UnfedHunger = source.UnfedHunger,
                FedHunger = source.FedHunger,
                PrionRate = source.PrionRate,
                AppliedPrionFraction = source.AppliedPrionFraction,
                PendingPrionIncrement = source.PendingPrionIncrement,
                DiseaseRate = source.DiseaseRate,
                HealthStart = source.HealthStart,
                DiseaseStart = source.DiseaseStart,
                HealthStartGrowth = source.HealthStartGrowth,
                HealthStartDays = source.HealthStartDays,
                PendingGrowthEvent = source.PendingGrowthEvent,
                PendingNativeDeath = source.PendingNativeDeath
            };
            foreach (var pair in source.Tags) result.Tags.Add(pair.Key, pair.Value.Clone());
            foreach (var pair in source.Variances) result.Variances.Add(pair.Key, pair.Value);
            foreach (var pair in source.Choices)
                result.Choices.Add(pair.Key,
                    new Dictionary<string, double>(pair.Value, StringComparer.Ordinal));
            foreach (HealthEvent item in source.HealthHistory) result.HealthHistory.Add(new HealthEvent
            {
                InfectionChance = item.InfectionChance,
                Regen = item.Regen,
                DiseaseDamage = item.DiseaseDamage,
                MaxHealth = item.MaxHealth,
                CureChance = item.CureChance,
                Growth = item.Growth,
                HasGrowthDistribution = item.HasGrowthDistribution,
                AlternativeGrowth = item.AlternativeGrowth,
                GrowthProbability = item.GrowthProbability,
                Hunger = item.Hunger,
                PrionRate = item.PrionRate
            });
            return result;
        }

        internal static AnimalRecord Representative(AnimalRecord source) => Copy(source, 1);

        internal static AnimalRecord CopyUnits(AnimalRecord source, int quantity) =>
            Copy(source, quantity);

        internal static bool IsHungryCohort(AnimalRecord record) =>
            record != null && !record.WasExtracted
                && record.Tag(HungryCohortTag)?.valueEnabled == true;

        internal static bool IsNativeStarving(AnimalRecord record) => record != null
            && record.Tag(StarvingTag)?.valueEnabled == true;

        /// <summary>Multiplies tonight's unfed rate into the saved cross-day rate.</summary>
        internal static void AccumulateHungerRate(AnimalRecord record, double nightlyRate)
        {
            if (record == null || double.IsNaN(nightlyRate)) return;
            double rate = Math.Max(0d, Math.Min(1d, nightlyRate));
            record.TotalHungerRate = Math.Max(0d,
                Math.Min(1d, record.TotalHungerRate * rate));
        }
        /// <summary>Applies the native zero-hunger, two-day starvation tag transition.</summary>
        internal static bool UpdateStarvation(AnimalRecord record, float hunger)
        {
            if (record == null || record.WasExtracted || record.IsDead) return false;
            if (hunger > 0f)
            {
                record.Tags.Remove(StarvingTag);
                return false;
            }
            if (IsNativeStarving(record)) return true;
            EnableNativeTag(record, StarvingTag);
            return false;
        }

        /// <summary>Applies native corpse changes to a counted animal cohort.</summary>
        internal static void FinalizeVirtualCorpse(GameItem source,
            AnimalRecord record, bool currentHealthAlreadyRecorded = true)
        {
            if (currentHealthAlreadyRecorded && record.HealthHistory.Count > 0)
            {
                HealthEvent latest = record.HealthHistory[record.HealthHistory.Count - 1];
                record.VirtualGrowth -= latest.Growth;
                record.HealthHistory.RemoveAt(record.HealthHistory.Count - 1);
                double disease = record.DiseaseStart;
                foreach (HealthEvent earlier in record.HealthHistory)
                    disease = (disease + (1d - disease) * earlier.InfectionChance)
                        * (1d - earlier.CureChance);
                record.DiseaseRate = disease;
                record.PendingGrowthEvent = false;
            }
            GameItem proxy = AnimalStackHooks.CreateProxy(source, record);
            try
            {
                proxy.ModifyTag(PayloadTag, tag => tag.SetString(string.Empty), false);
                proxy.EnableTag(ExtractedTag, false);
                HusbandryHelper.OnAnimalDead(proxy, false);
                foreach (var pair in proxy.modifiedState.dict)
                {
                    if (pair.Key == PayloadTag || pair.Key == GroupLineageTag
                        || pair.Key == ExtractedTag || pair.Value == null) continue;
                    if (!record.Tags.TryGetValue(pair.Key, out TagState old)
                        || !SameTag(old, pair.Value))
                        record.Tags[pair.Key] = pair.Value.Clone();
                }
                record.SpritePath = proxy.spritePath;
                record.CustomText = proxy.customText;
                record.UnitValue = proxy.unitValue;
                record.Tags.Remove(HungryCohortTag);
                record.PendingNativeDeath = false;
            }
            finally { if (proxy.parentInventory == null) proxy.Destroy(); }
        }

        private static bool SameTag(TagState first, TagState second) =>
            first.valueEnabled == second.valueEnabled
            && first.valueBool == second.valueBool
            && first.valueInt == second.valueInt
            && first.valueLong == second.valueLong
            && first.valueFloat == second.valueFloat
            && first.valueDouble == second.valueDouble
            && string.Equals(first.valueString, second.valueString,
                StringComparison.Ordinal);

        internal static bool KeepVirtualSingleton(AnimalRecord record) => record != null
            && !record.WasExtracted
            && (record.HasHungerDistribution || IsHungryCohort(record)
                || record.HealthHistory.Count > 0);

        internal static double PrionProbability(AnimalRecord record) => record == null ? 0d
            : Math.Max(record.PrionRate, record.Tag(PrionTag)?.valueEnabled == true ? 1d : 0d);

        /// <summary>Adds an expected infection rate to a fed virtual cohort.</summary>
        internal static void AddPrionProbability(AnimalRecord record, double increment)
        {
            if (record == null || increment <= 0d || double.IsNaN(increment)) return;
            double previous = PrionProbability(record);
            double current = Math.Min(1d, previous + increment);
            double added = current - previous;
            ApplyPrionBonusChange(record, added);
            record.PrionRate = current;
            record.AppliedPrionFraction = Math.Min(1d,
                record.AppliedPrionFraction + added);
            SetPrionTag(record, current >= 1d);
        }

        /// <summary>Matches the three native prion gene bonus adjustments.</summary>
        private static void ApplyPrionBonusChange(AnimalRecord record, double fraction)
        {
            if (fraction == 0d) return;
            foreach (string gene in new[] { "ANIMAL_GROWTH_RATE_TAG",
                "ANIMAL_IMMUNITY_TAG", "ANIMAL_LONGEVITY_TAG" })
            {
                if (record.Tag(gene + "_ALLELE_A")?.valueEnabled != true) continue;
                string bonusId = gene + "_BONUS_PERCENT";
                if (!record.Tags.TryGetValue(bonusId, out TagState bonus))
                {
                    bonus = new TagState(bonusId, bonusId);
                    record.Tags.Add(bonusId, bonus);
                }
                float next = bonus.GetFloat() - (float)(0.2d * fraction);
                bonus.SetFloat(next);
                bonus.valueEnabled = next != 0f;
            }
        }

        private static void SetPrionTag(AnimalRecord record, bool infected)
        {
            if (!infected) { record.Tags.Remove(PrionTag); return; }
            if (!record.Tags.TryGetValue(PrionTag, out TagState tag))
            {
                tag = new TagState(PrionTag, PrionTag);
                record.Tags.Add(PrionTag, tag);
            }
            tag.Enable();
        }

        /// <summary>Stores native-order disease and health inputs without choosing an outcome.</summary>
        internal static void DeferHealth(AnimalRecord record, double infectionChance,
            float regen, float damage, float maxHealth, double cureChance)
        {
            if (record == null) return;
            var next = new HealthEvent
            {
                InfectionChance = Math.Max(0d, Math.Min(1d, infectionChance)),
                Regen = regen,
                DiseaseDamage = damage,
                MaxHealth = Math.Max(0f, maxHealth),
                CureChance = Math.Max(0d, Math.Min(1d, cureChance)),
                Hunger = Expected(record, "ANIMAL_HUNGER_TAG"),
                PrionRate = Math.Min(1d, PrionProbability(record)
                    + record.PendingPrionIncrement * (IsHungryCohort(record) ? 0d
                        : record.HasHungerDistribution ? 1d - record.HungerRate : 1d))
            };
            if (!ValidHealthEvent(next)) return;
            TagState health = record.Tag("ANIMAL_HEALTH_TAG");
            if (health == null) return;
            if (record.HealthHistory.Count == 0)
            {
                record.HealthStart = health.GetFloat();
                record.DiseaseStart = record.DiseaseRate;
                record.HealthStartGrowth = record.VirtualGrowth;
                record.HealthStartDays = Math.Max(0, record.DeferredDays - 1);
            }
            if (record.HealthHistory.Count == MaxHealthEvents)
            {
                // Fold only extreme, multi-thousand-day histories into their expected start.
                HealthEvent oldest = record.HealthHistory[0];
                double first = record.DiseaseStart
                    + (1d - record.DiseaseStart) * oldest.InfectionChance;
                record.HealthStart = GroupHealthAfterDisease(record.HealthStart,
                    oldest.Regen, oldest.DiseaseDamage, oldest.MaxHealth, first);
                record.DiseaseStart = first * (1d - oldest.CureChance);
                record.HealthStartGrowth += oldest.Growth;
                record.HealthStartDays = Math.Min(int.MaxValue,
                    record.HealthStartDays + 1);
                record.HealthHistory.RemoveAt(0);
            }
            record.HealthHistory.Add(next);
            record.PendingGrowthEvent = true;
            double diseased = record.DiseaseRate
                + (1d - record.DiseaseRate) * next.InfectionChance;
            health.SetFloat(GroupHealthAfterDisease(health.GetFloat(), regen,
                damage, next.MaxHealth, diseased));
            record.DiseaseRate = diseased * (1d - next.CureChance);
            record.Tags.Remove(DiseaseTag);
        }

        internal static void AddVirtualGrowth(AnimalRecord record, float growth)
        {
            record.VirtualGrowth += growth;
            if (!record.PendingGrowthEvent || record.HealthHistory.Count == 0) return;
            record.HealthHistory[record.HealthHistory.Count - 1].Growth += growth;
            record.PendingGrowthEvent = false;
        }

        /// <summary>Stores expected and conditional growth for a later individual draw.</summary>
        internal static void AddVirtualGrowthDistribution(AnimalRecord record,
            float normalGrowth, float alternativeGrowth, double probability)
        {
            probability = Math.Max(0d, Math.Min(1d, probability));
            float expected = (float)(normalGrowth * (1d - probability)
                + alternativeGrowth * probability);
            bool hasCurrentEvent = record.PendingGrowthEvent && record.HealthHistory.Count > 0;
            AddVirtualGrowth(record, expected);
            if (!hasCurrentEvent) return;
            var last = record.HealthHistory[record.HealthHistory.Count - 1];
            last.HasGrowthDistribution = true;
            last.AlternativeGrowth = alternativeGrowth;
            last.GrowthProbability = probability;
        }
        internal static void RecordLatestHealthPanel(AnimalRecord record)
        {
            if (record == null || record.HealthHistory.Count == 0) return;
            HealthEvent latest = record.HealthHistory[record.HealthHistory.Count - 1];
            latest.Hunger = Expected(record, "ANIMAL_HUNGER_TAG");
            latest.PrionRate = PrionProbability(record);
        }

        internal static float ClampHealth(float value, float maximum)
        {
            if (!float.IsFinite(maximum) || maximum <= 0f
                || float.IsNaN(value) || value <= 0f) return 0f;
            return value >= maximum ? maximum : value;
        }

        /// <summary>Uses the current native maximum to correct a real animal.</summary>
        internal static float ClampNativeHealth(GameItem item)
        {
            if (item == null) return 0f;
            float maximum = GeneticHelper.GetCurrentStat(item, "ANIMAL_MAX_HEALTH_TAG");
            float before = item.GetTagReadonly("ANIMAL_HEALTH_TAG").GetFloat();
            float corrected = ClampHealth(before, maximum);
            if (before != corrected)
                item.ModifyTag("ANIMAL_HEALTH_TAG", tag => tag.SetFloat(corrected), false);
            return corrected;
        }

        /// <summary>Applies shared immunity change, then proportional disease damage.</summary>
        internal static float GroupHealthAfterDisease(float health, float regen,
            float diseaseDamage, float maxHealth, double diseasedShare)
        {
            float afterImmunity = ClampHealth(health + regen, maxHealth);
            return ClampHealth((float)(afterImmunity - diseaseDamage * diseasedShare),
                maxHealth);
        }

        /// <summary>Replays saved native health steps when one virtual rat leaves a pile.</summary>
        private static void MaterializeHealth(AnimalRecord record)
        {
            if (record.HealthHistory.Count == 0) return;
            float health = record.HealthStart;
            bool diseased;
            lock (Samples) diseased = Samples.NextDouble() < record.DiseaseStart;
            bool dead = false;
            int elapsed = 0;
            float survivedGrowth = record.HealthStartGrowth;
            HealthEvent last = null;
            foreach (HealthEvent item in record.HealthHistory)
            {
                elapsed++;
                last = item;
                lock (Samples)
                {
                    if (!diseased && Samples.NextDouble() < item.InfectionChance)
                        diseased = true;
                }
                health = ClampHealth(health + item.Regen, item.MaxHealth);
                if (diseased)
                {
                    health = ClampHealth(health - item.DiseaseDamage, item.MaxHealth);
                    lock (Samples)
                        if (Samples.NextDouble() < item.CureChance) diseased = false;
                }
                if (health <= 0f) { dead = true; break; }
                float individualGrowth = item.Growth;
                if (item.HasGrowthDistribution)
                {
                    bool alternative;
                    lock (Samples) alternative = Samples.NextDouble() < item.GrowthProbability;
                    if (alternative) individualGrowth = item.AlternativeGrowth;
                    else if (item.GrowthProbability < 1d)
                        individualGrowth = (float)((item.Growth
                            - item.GrowthProbability * item.AlternativeGrowth)
                            / (1d - item.GrowthProbability));
                }
                survivedGrowth += individualGrowth;
            }
            TagState panel = record.Tag("ANIMAL_HEALTH_TAG");
            panel?.SetFloat(health);
            SetDiseaseTag(record, diseased);
            if (dead)
            {
                record.DeferredDays = (int)Math.Min(int.MaxValue,
                    (long)record.HealthStartDays + elapsed);
                record.VirtualGrowth = survivedGrowth;
                record.VirtualHungerDelta = 0f;
                record.HasHungerDistribution = false;
                record.Tag("ANIMAL_HUNGER_TAG")?.SetFloat(last.Hunger);
                record.PrionRate = last.PrionRate;
                EnableNativeTag(record, "ANIMAL_IS_DEAD_TAG");
                record.PendingNativeDeath = true;
            }
            if (!dead)
            {
                float expectedRecordedGrowth = record.HealthStartGrowth;
                foreach (var entry in record.HealthHistory) expectedRecordedGrowth += entry.Growth;
                record.VirtualGrowth += survivedGrowth - expectedRecordedGrowth;
            }
            record.DiseaseRate = diseased ? 1d : 0d;
            record.HealthHistory.Clear();
            record.PendingGrowthEvent = false;
        }

        private static void SetDiseaseTag(AnimalRecord record, bool diseased)
        {
            if (diseased) EnableNativeTag(record, DiseaseTag);
            else record.Tags.Remove(DiseaseTag);
        }

        private static void EnableNativeTag(AnimalRecord record, string id)
        {
            if (!record.Tags.TryGetValue(id, out TagState tag))
            {
                tag = new TagState(id, id);
                record.Tags.Add(id, tag);
            }
            tag.Enable();
        }

        internal static void SetHungryCohort(AnimalRecord record, bool hungry)
        {
            if (!hungry) { record.Tags.Remove(HungryCohortTag); return; }
            if (!record.Tags.TryGetValue(HungryCohortTag, out TagState tag))
            {
                tag = new TagState(HungryCohortTag, HungryCohortTag);
                record.Tags.Add(HungryCohortTag, tag);
            }
            tag.Enable();
        }

        internal static bool HasHungryVirtual(GameItem item)
        {
            if (!TryRead(item, out List<AnimalRecord> records)) return false;
            foreach (AnimalRecord record in records)
                if (IsHungryCohort(record)) return true;
            return false;
        }

        internal static bool HasVirtualSingleton(GameItem item) => item != null
            && item.unitCount == 1 && TryRead(item, out List<AnimalRecord> records)
            && records.Count == 1 && !records[0].WasExtracted
            && KeepVirtualSingleton(records[0]);

        internal static bool HasNormalVirtual(GameItem item)
        {
            if (!TryRead(item, out List<AnimalRecord> records)) return false;
            foreach (AnimalRecord record in records)
                if (!record.WasExtracted && !record.IsDead && !IsHungryCohort(record))
                    return true;
            return false;
        }

        private static void ExpandSmall(List<AnimalRecord> records)
        {
            for (int i = records.Count - 1; i >= 0; i--)
            {
                AnimalRecord record = records[i];
                if (record.Quantity == 1) continue;
                records.RemoveAt(i);
                for (int j = 0; j < record.Quantity; j++)
                    records.Insert(i + j, Copy(record, 1));
            }
        }

        private static void SettleMean(AnimalRecord record)
        {
            TagState age = record.Tag("ANIMAL_AGE_TAG");
            if (age != null) age.SetInt((int)Math.Min(int.MaxValue,
                Math.Max(0L, (long)age.GetInt() + record.DeferredDays)));
            TagState weight = record.Tag("ANIMAL_WEIGHT_TAG");
            if (weight != null) weight.SetFloat((float)Math.Min(float.MaxValue,
                Math.Max(0d, (double)weight.GetFloat() + record.VirtualGrowth)));
            TagState hunger = record.Tag("ANIMAL_HUNGER_TAG");
            if (hunger != null) hunger.SetFloat(Math.Max(0f,
                hunger.GetFloat() + record.VirtualHungerDelta));
            record.DeferredDays = 0;
            record.VirtualGrowth = 0f;
            record.VirtualHungerDelta = 0f;
        }

        private static double Value(TagState tag, char field)
        {
            if (tag == null) return 0d;
            return field == 'F' ? tag.valueFloat : field == 'I' ? tag.valueInt
                : field == 'L' ? tag.valueLong : tag.valueDouble;
        }

        private static void SetValue(TagState tag, char field, double value)
        {
            if (double.IsNaN(value)) value = 0d;
            if (double.IsPositiveInfinity(value)) value = double.MaxValue;
            if (double.IsNegativeInfinity(value)) value = -double.MaxValue;
            if (field == 'F') tag.SetFloat((float)Math.Max(-float.MaxValue, Math.Min(float.MaxValue, value)));
            else if (field == 'I') tag.SetInt((int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Round(value))));
            else if (field == 'L') tag.valueLong = value >= long.MaxValue ? long.MaxValue
                : value <= long.MinValue ? long.MinValue : (long)Math.Round(value);
            else tag.valueDouble = value;
        }

        private static void MergeField(AnimalRecord left, AnimalRecord right,
            string id, char field, TagState oldLeft, TagState oldRight, TagState destination,
            int leftCount, int rightCount)
        {
            string key = field + ":" + id;
            double first = Value(oldLeft, field), second = Value(oldRight, field);
            double firstVariance = left.Variances.TryGetValue(key, out double a) ? a : 0d;
            double secondVariance = right.Variances.TryGetValue(key, out double b) ? b : 0d;
            string prionGene = field == 'F' ? PrionBonusGene(id) : null;
            if (prionGene != null)
            {
                string baselineKey = "B:" + id;
                bool firstAllele = left.Tag(prionGene + "_ALLELE_A")?.valueEnabled == true;
                bool secondAllele = right.Tag(prionGene + "_ALLELE_A")?.valueEnabled == true;
                if (left.Variances.TryGetValue(baselineKey, out double firstBaseline))
                    firstVariance = firstBaseline;
                else if (firstAllele)
                    firstVariance = Math.Max(0d, firstVariance - 0.04d
                        * left.AppliedPrionFraction * (1d - left.AppliedPrionFraction));
                if (right.Variances.TryGetValue(baselineKey, out double secondBaseline))
                    secondVariance = secondBaseline;
                else if (secondAllele)
                    secondVariance = Math.Max(0d, secondVariance - 0.04d
                        * right.AppliedPrionFraction * (1d - right.AppliedPrionFraction));
                first += firstAllele ? 0.2d * left.AppliedPrionFraction : 0d;
                second += secondAllele ? 0.2d * right.AppliedPrionFraction : 0d;
            }
            double count = (double)leftCount + rightCount;
            double delta = second - first;
            double mean = first + delta * rightCount / count;
            double variance = (firstVariance * leftCount + secondVariance * rightCount
                + delta * delta * leftCount * rightCount / count) / count;
            if (prionGene != null)
            {
                double applied = (left.AppliedPrionFraction * leftCount
                    + right.AppliedPrionFraction * rightCount) / count;
                bool destinationAllele = left.Tag(prionGene + "_ALLELE_A")?.valueEnabled == true
                    || right.Tag(prionGene + "_ALLELE_A")?.valueEnabled == true;
                SetValue(destination, field, mean - (destinationAllele ? 0.2d * applied : 0d));
                left.Variances.Remove(key);
                string baselineKey = "B:" + id;
                if (variance > 0d && !double.IsInfinity(variance))
                    left.Variances[baselineKey] = variance;
                else left.Variances.Remove(baselineKey);
            }
            else
            {
                SetValue(destination, field, mean);
                if (variance > 0d && !double.IsInfinity(variance)) left.Variances[key] = variance;
                else left.Variances.Remove(key);
            }
        }

        private static string PrionBonusGene(string id)
        {
            foreach (string gene in new[] { "ANIMAL_GROWTH_RATE_TAG",
                "ANIMAL_IMMUNITY_TAG", "ANIMAL_LONGEVITY_TAG" })
                if (id == gene + "_BONUS_PERCENT") return gene;
            return null;
        }

        /// <summary>Keeps discrete native attributes as choices during statistical compression.</summary>
        private static void MergeChoice(AnimalRecord left, AnimalRecord right,
            string key, string leftValue, string rightValue, int leftCount, int rightCount)
        {
            bool leftMixed = left.Choices.TryGetValue(key, out var leftOptions);
            bool rightMixed = right.Choices.TryGetValue(key, out var rightOptions);
            if (!leftMixed && !rightMixed
                && string.Equals(leftValue, rightValue, StringComparison.Ordinal)) return;
            double total = (double)leftCount + rightCount;
            var merged = new Dictionary<string, double>(StringComparer.Ordinal);
            void Add(string value, double weight)
            {
                merged[value] = (merged.TryGetValue(value, out double prior)
                    ? prior : 0d) + weight;
            }
            if (leftMixed)
                foreach (var option in leftOptions) Add(option.Key,
                    option.Value * leftCount / total);
            else Add(leftValue ?? string.Empty, leftCount / total);
            if (rightMixed)
                foreach (var option in rightOptions) Add(option.Key,
                    option.Value * rightCount / total);
            else Add(rightValue ?? string.Empty, rightCount / total);
            if (merged.Count > 1) left.Choices[key] = merged;
            else left.Choices.Remove(key);
        }

        private static void MergeHealthHistory(AnimalRecord left, AnimalRecord right,
            int leftCount, int rightCount)
        {
            int count = left.HealthHistory.Count;
            if (count == 0 && right.HealthHistory.Count == 0) return;
            double total = (double)leftCount + rightCount;
            if (count != right.HealthHistory.Count)
            {
                // Different-aged cohorts retain their current expected panel and disease rate.
                left.HealthHistory.Clear();
                RatButcherPatch.Observer?.RecordAnimal(
                    "health histories of different lengths merged at expected panel");
                return;
            }
            left.HealthStart = (float)(((double)left.HealthStart * leftCount
                + right.HealthStart * rightCount) / total);
            left.DiseaseStart = (left.DiseaseStart * leftCount
                + right.DiseaseStart * rightCount) / total;
            left.HealthStartGrowth = (float)(((double)left.HealthStartGrowth * leftCount
                + right.HealthStartGrowth * rightCount) / total);
            left.HealthStartDays = (int)Math.Round(((double)left.HealthStartDays * leftCount
                + right.HealthStartDays * rightCount) / total);
            for (int i = 0; i < count; i++)
            {
                HealthEvent first = left.HealthHistory[i], second = right.HealthHistory[i];
                first.InfectionChance = (first.InfectionChance * leftCount
                    + second.InfectionChance * rightCount) / total;
                first.Regen = (float)(((double)first.Regen * leftCount
                    + second.Regen * rightCount) / total);
                first.DiseaseDamage = (float)(((double)first.DiseaseDamage * leftCount
                    + second.DiseaseDamage * rightCount) / total);
                first.MaxHealth = (float)(((double)first.MaxHealth * leftCount
                    + second.MaxHealth * rightCount) / total);
                first.CureChance = (first.CureChance * leftCount
                    + second.CureChance * rightCount) / total;
                // Preserve the conditional daily distribution when records are compressed.
                double p1 = first.HasGrowthDistribution ? first.GrowthProbability : 0d;
                double p2 = second.HasGrowthDistribution ? second.GrowthProbability : 0d;
                double p = (p1 * leftCount + p2 * rightCount) / total;
                double alt = p > 0d
                    ? (first.AlternativeGrowth * p1 * leftCount
                        + second.AlternativeGrowth * p2 * rightCount) / (p * total) : 0d;
                first.HasGrowthDistribution = first.HasGrowthDistribution || second.HasGrowthDistribution;
                first.GrowthProbability = p;
                first.AlternativeGrowth = (float)alt;
                first.Growth = (float)(((double)first.Growth * leftCount
                    + second.Growth * rightCount) / total);
                first.Hunger = (float)(((double)first.Hunger * leftCount
                    + second.Hunger * rightCount) / total);
                first.PrionRate = (first.PrionRate * leftCount
                    + second.PrionRate * rightCount) / total;
            }
        }

        /// <summary>Returns native age in days, including deferred virtual days.</summary>
        internal static int CurrentAge(AnimalRecord record)
        {
            long age = (long)(record.Tag("ANIMAL_AGE_TAG")?.GetInt() ?? 0) + record.DeferredDays;
            return (int)Math.Min(int.MaxValue, Math.Max(0L, age));
        }

        /// <summary>Recognizes a pile only when both parent and age are known.</summary>
        internal static bool TryGetPileIdentity(AnimalRecord record, out string identity)
        {
            identity = null;
            if (record == null || record.Tag("ANIMAL_AGE_TAG") == null
                || record.Choices.ContainsKey("S:" + LineageTag)
                || record.Variances.TryGetValue("I:ANIMAL_AGE_TAG", out double ageVariance)
                    && ageVariance > 0d) return false;
            string parent = record.Tag(LineageTag)?.GetString() ?? string.Empty;
            identity = parent.Length.ToString(CultureInfo.InvariantCulture) + ":" + parent
                + ":" + CurrentAge(record).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        internal static string PileKey(AnimalRecord record, int index) =>
            TryGetPileIdentity(record, out string identity) ? "pile:" + identity
                : "opaque:" + index.ToString(CultureInfo.InvariantCulture);

        /// <summary>Keeps physically distinct feeding and starvation cohorts apart.</summary>
        internal static string ProcessingPileKey(AnimalRecord record, int index) =>
            PileKey(record, index) + (IsHungryCohort(record) ? ":unfed" : ":fed")
                + (IsNativeStarving(record) ? ":starving" : ":healthy");
        internal static bool SamePile(AnimalRecord left, AnimalRecord right) =>
            TryGetPileIdentity(left, out string first)
            && TryGetPileIdentity(right, out string second)
            && string.Equals(first, second, StringComparison.Ordinal);

        /// <summary>Combines panels only for matching parents and current age.</summary>
        private static bool MergeVirtual(AnimalRecord left, AnimalRecord right)
        {
            if (!SamePile(left, right)) return false;
            long sum = (long)left.Quantity + right.Quantity;
            if (sum > int.MaxValue) return false;
            float leftHunger = Expected(left, "ANIMAL_HUNGER_TAG");
            float rightHunger = Expected(right, "ANIMAL_HUNGER_TAG");
            double leftTotalHunger = left.TotalHungerRate;
            double rightTotalHunger = right.TotalHungerRate;
            double leftPrion = PrionProbability(left);
            double rightPrion = PrionProbability(right);
            SettleMean(left);
            SettleMean(right);
            int leftCount = left.Quantity, rightCount = right.Quantity;
            var keys = new HashSet<string>(left.Tags.Keys, StringComparer.Ordinal);
            keys.UnionWith(right.Tags.Keys);
            foreach (string id in keys)
            {
                if (id == ExtractedTag || id == IdentityTag) continue;
                TagState first = left.Tag(id), second = right.Tag(id);
                if (first == null && second == null) continue;
                MergeChoice(left, right, "S:" + id,
                    first?.valueString, second?.valueString, leftCount, rightCount);
                MergeChoice(left, right, "Q:" + id,
                    first?.valueBool == true ? "1" : "0",
                    second?.valueBool == true ? "1" : "0", leftCount, rightCount);
                TagState destination = first ?? second.Clone();
                foreach (char field in new[] { 'F', 'I', 'L', 'D' })
                    MergeField(left, right, id, field, first, second, destination,
                        leftCount, rightCount);
                double enabled = ((first?.valueEnabled == true ? leftCount : 0d)
                    + (second?.valueEnabled == true ? rightCount : 0d)) / sum;
                destination.valueEnabled = enabled >= 0.5d;
                if (enabled > 0d && enabled < 1d) left.Variances["P:" + id] = enabled;
                else left.Variances.Remove("P:" + id);
                if (first == null) left.Tags.Add(id, destination);
            }
            MergeChoice(left, right, "@sprite", left.SpritePath, right.SpritePath,
                leftCount, rightCount);
            MergeChoice(left, right, "@custom", left.CustomText, right.CustomText,
                leftCount, rightCount);
            MergeChoice(left, right, "@value",
                left.UnitValue.ToString(CultureInfo.InvariantCulture),
                right.UnitValue.ToString(CultureInfo.InvariantCulture),
                leftCount, rightCount);
            MergeHealthHistory(left, right, leftCount, rightCount);
            left.Tags.Remove(ExtractedTag);
            left.Tags.Remove(IdentityTag);
            left.Quantity = (int)sum;
            left.FoodShare = (float)(((double)left.FoodShare * leftCount
                + (double)right.FoodShare * rightCount) / sum);
            left.TotalHungerRate = (leftTotalHunger * leftCount
                + rightTotalHunger * rightCount) / sum;
            left.PrionRate = (leftPrion * leftCount + rightPrion * rightCount) / sum;
            left.DiseaseRate = (left.DiseaseRate * leftCount
                + right.DiseaseRate * rightCount) / sum;
            left.Variances.Remove("P:" + DiseaseTag);
            left.Tags.Remove(DiseaseTag);
            left.AppliedPrionFraction = (left.AppliedPrionFraction * leftCount
                + right.AppliedPrionFraction * rightCount) / sum;
            left.PendingPrionIncrement = (left.PendingPrionIncrement * leftCount
                + right.PendingPrionIncrement * rightCount) / sum;
            left.Variances.Remove("P:" + PrionTag);
            if (left.PrionRate < 1d) left.Tags.Remove(PrionTag);
            else SetPrionTag(left, true);
            if (left.HasHungerDistribution || right.HasHungerDistribution)
            {
                double hungryLeft = left.HasHungerDistribution
                    ? leftTotalHunger * leftCount : 0d;
                double hungryRight = right.HasHungerDistribution
                    ? rightTotalHunger * rightCount : 0d;
                double hungry = hungryLeft + hungryRight;
                double fed = sum - hungry;
                left.HungerRate = 0d;
                left.UnfedHunger = hungry > 0d
                    ? (float)((hungryLeft * left.UnfedHunger + hungryRight * right.UnfedHunger) / hungry)
                    : 0f;
                left.FedHunger = fed > 0d
                    ? (float)(((leftCount - hungryLeft) * (left.HasHungerDistribution
                        ? left.FedHunger : leftHunger)
                        + (rightCount - hungryRight) * (right.HasHungerDistribution
                            ? right.FedHunger : rightHunger)) / fed)
                    : 0f;
                left.HasHungerDistribution = true;
            }
            return true;
        }

        private static void CompactTo(List<AnimalRecord> records, int limit)
        {
            while (records.Count > limit)
            {
                int first = -1, second = -1;
                for (int pass = 0; pass < 2 && second < 0; pass++)
                {
                    for (int i = 0; i < records.Count && second < 0; i++)
                    {
                        for (int j = i + 1; j < records.Count; j++)
                        {
                            if (!SamePile(records[i], records[j])
                                || pass == 0 && IsHungryCohort(records[i])
                                    != IsHungryCohort(records[j])) continue;
                            first = i;
                            second = j;
                            break;
                        }
                    }
                }
                if (second < 0) break;
                if (!MergeVirtual(records[first], records[second])) break;
                records.RemoveAt(second);
            }
        }

        /// <summary>Reduces saved real panels while retaining each stack's total animal count.</summary>
        private static int CompactRealTo(List<AnimalRecord> records, int target)
        {
            int before = records.FindAll(record => record.WasExtracted).Count;
            target = Math.Max(0, target);
            while (before > target)
            {
                int first = records.FindIndex(record => record.WasExtracted);
                if (first < 0) break;
                int second = before - target >= 2
                    ? records.FindIndex(first + 1, record => record.WasExtracted
                        && SamePile(records[first], record)) : -1;
                if (second >= 0)
                {
                    if (!MergeVirtual(records[first], records[second])) break;
                    records.RemoveAt(second);
                    before -= 2;
                    continue;
                }
                int virtualIndex = records.FindIndex(record => !record.WasExtracted
                    && SamePile(record, records[first]));
                if (virtualIndex >= 0 && MergeVirtual(records[virtualIndex], records[first]))
                    records.RemoveAt(first);
                else
                {
                    SettleMean(records[first]);
                    records[first].Tags.Remove(ExtractedTag);
                    records[first].Tags.Remove(IdentityTag);
                }
                before--;
            }
            return records.FindAll(record => record.WasExtracted).Count;
        }

        /// <summary>Keeps at most 128 real panels in one container, starting with same-stack panels.</summary>
        internal static void CompressContainer(GameInventory inventory, bool allowNativeMerge = false)
        {
            if (inventory == null || compressingContainer) return;
            compressingContainer = true;
            try
            {
                long real = 0;
                foreach (GameItem item in inventory.childItems)
                {
                    if (!ItemStackPolicy.IsAnimal(item) || item.IsTag("ANIMAL_IS_DEAD_TAG")
                        || !TryRead(item, out List<AnimalRecord> records)) continue;
                    foreach (AnimalRecord record in records)
                        if (!AnimalPopulationPolicy.IsVirtual(item, record)) real += record.Quantity;
                }
                if (real <= MaxRealAnimalsPerContainer) return;
                long excess = real - MaxRealAnimalsPerContainer;
                foreach (GameItem item in inventory.childItems)
                {
                    if (excess == 0) break;
                    if (!ItemStackPolicy.IsAnimal(item) || item.IsTag("ANIMAL_IS_DEAD_TAG")
                        || item.unitCount < 2 || !TryRead(item, out List<AnimalRecord> records)) continue;
                    int before = records.FindAll(record => record.WasExtracted).Count;
                    if (before == 0) continue;
                    int after = CompactRealTo(records, (int)Math.Max(0L, before - excess));
                    if (after == before || !Write(item, records)) continue;
                    excess -= before - after;
                    RatButcherPatch.Observer?.RecordAnimal("container real panels compressed: item="
                        + item.uniqueId + ", real_before=" + before + ", real_after=" + after);
                }
                if (allowNativeMerge)
                {
                    while (excess > 0)
                    {
                        GameItem source = null, target = null;
                        foreach (GameItem candidate in inventory.childItems)
                        {
                            if (!ItemStackPolicy.IsAnimal(candidate) || candidate.unitCount != 1
                                || candidate.IsTag("ANIMAL_IS_DEAD_TAG")) continue;
                            foreach (GameItem destination in inventory.childItems)
                            {
                                if (destination.unitCount >= MaxAnimalsPerStack
                                    || !ItemStackPolicy.CanStack(candidate, destination)) continue;
                                source = candidate;
                                target = destination;
                                break;
                            }
                            if (target != null) break;
                        }
                        if (target == null) break;
                        int realBefore = CountReal(source) + CountReal(target);
                        try { SlotMarker.StackItemUnchecked(source, target, 1); }
                        catch (Exception error)
                        {
                            RatButcherPatch.Observer?.RecordAnimal("container native rat merge failed: "
                                + error);
                            break;
                        }
                        int realAfter = CountReal(target);
                        if (realBefore - realAfter > excess
                            && TryRead(target, out List<AnimalRecord> promoted))
                        {
                            int index = promoted.FindLastIndex(record => !record.WasExtracted
                                && record.Quantity == 1);
                            if (index >= 0)
                            {
                                Materialize(promoted[index]);
                                if (Write(target, promoted)) realAfter++;
                            }
                        }
                        if (realAfter >= realBefore && TryRead(target, out List<AnimalRecord> merged))
                        {
                            int remaining = CompactRealTo(merged, realAfter - 1);
                            if (remaining < realAfter && Write(target, merged)) realAfter = remaining;
                        }
                        if (realAfter >= realBefore) break;
                        excess -= realBefore - realAfter;
                    }
                }
                if (excess > 0)
                    RatButcherPatch.Observer?.RecordAnimal("container real panel limit pending: excess="
                        + excess + ", native-compatible stacks unavailable");
            }
            finally { compressingContainer = false; }
        }

        private static int CountReal(GameItem item)
        {
            if (item == null || item.unitCount < 1 || !TryRead(item, out List<AnimalRecord> records))
                return 0;
            int count = 0;
            foreach (AnimalRecord record in records)
                if (!AnimalPopulationPolicy.IsVirtual(item, record)) count += record.Quantity;
            return count;
        }

        private static void SamplePanel(AnimalRecord record)
        {
            foreach (var pair in record.Variances)
            {
                if (pair.Key.Length < 3 || pair.Key[1] != ':') continue;
                string id = pair.Key.Substring(2);
                if (id == PrionTag || id == DiseaseTag
                    || record.HealthHistory.Count > 0 && id == "ANIMAL_HEALTH_TAG"
                    || record.HasHungerDistribution
                    && id == "ANIMAL_HUNGER_TAG") continue;
                TagState tag = record.Tag(id);
                if (tag == null) continue;
                lock (Samples)
                {
                    if (pair.Key[0] == 'P') tag.valueEnabled = Samples.NextDouble() < pair.Value;
                    else
                    {
                        double first = Math.Max(double.Epsilon, Samples.NextDouble());
                        double normal = Math.Sqrt(-2d * Math.Log(first))
                            * Math.Cos(2d * Math.PI * Samples.NextDouble());
                        double sampled = Value(tag, pair.Key[0] == 'B' ? 'F' : pair.Key[0])
                            + Math.Sqrt(pair.Value) * normal;
                        if (id == "ANIMAL_WEIGHT_TAG" || id == "ANIMAL_AGE_TAG"
                            || id == "ANIMAL_HUNGER_TAG" || id == "ANIMAL_HEALTH_TAG")
                            sampled = Math.Max(0d, sampled);
                        SetValue(tag, pair.Key[0] == 'B' ? 'F' : pair.Key[0], sampled);
                    }
                }
            }
            record.Variances.Clear();
            foreach (var pair in record.Choices)
            {
                double draw;
                lock (Samples) draw = Samples.NextDouble();
                string chosen = null;
                foreach (var option in pair.Value)
                {
                    chosen = option.Key;
                    draw -= option.Value;
                    if (draw < 0d) break;
                }
                if (chosen == null) continue;
                if (pair.Key == "@sprite") record.SpritePath = chosen;
                else if (pair.Key == "@custom") record.CustomText = chosen;
                else if (pair.Key == "@value")
                {
                    if (long.TryParse(chosen, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long value)) record.UnitValue = value;
                }
                else if (pair.Key.Length > 2 && pair.Key[1] == ':')
                {
                    string id = pair.Key.Substring(2);
                    if (!record.Tags.TryGetValue(id, out TagState tag))
                    {
                        tag = new TagState(id, id);
                        record.Tags.Add(id, tag);
                    }
                    if (pair.Key[0] == 'S') tag.valueString = chosen;
                    else if (pair.Key[0] == 'Q') tag.valueBool = chosen == "1";
                }
            }
            record.Choices.Clear();
        }

        /// <summary>Writes all per-animal records to the saveable native tag state.</summary>
        internal static bool Write(GameItem item, List<AnimalRecord> records)
        {
            if (item == null || records == null || records.Count < 1
                || Count(records) != item.unitCount) return false;
            if (item.unitCount > 1 && !item.IsTag("ANIMAL_IS_DEAD_TAG"))
            {
                int before = records.FindAll(record => record.WasExtracted).Count;
                if (before > MaxRealAnimalsPerContainer)
                {
                    int after = CompactRealTo(records, MaxRealAnimalsPerContainer);
                    RatButcherPatch.Observer?.RecordAnimal("stack real panels compressed: item="
                        + item.uniqueId + ", real_before=" + before + ", real_after=" + after);
                }
            }
            if (records.Count > MaxIndividualRecords)
            {
                int before = records.Count;
                CompactTo(records, MaxIndividualRecords);
                RatButcherPatch.Observer?.RecordAnimal("animal panels compressed: item="
                    + item.uniqueId + ", records_before=" + before
                    + ", records_after=" + records.Count);
            }
            if (records.Count > MaxIndividualRecords) return false;
            NormalizeHealthRecords(item, records);
            if (records.Count == 1 && records[0].Quantity == 1
                && !records[0].HasHungerDistribution
                && !IsHungryCohort(records[0])
                && records[0].HealthHistory.Count == 0)
            {
                Apply(item, records[0]);
                if (records[0].PendingNativeDeath)
                {
                    records[0].PendingNativeDeath = false;
                    try
                    {
                        HusbandryHelper.OnAnimalDead(item, false);
                        CaptureIndividual(item, records[0]);
                    }
                    catch (Exception error)
                    {
                        RatButcherPatch.Observer?.RecordAnimal(
                            "deferred native death finalization failed: " + error);
                    }
                }
                Validated.Remove(item);
                item.Validate();
                return true;
            }
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write((byte)9);
                    writer.Write(item.unitCount);
                    writer.Write(records.Count);
                    foreach (AnimalRecord record in records)
                    {
                        writer.Write(record.Quantity);
                        writer.Write(record.SpritePath ?? string.Empty);
                        writer.Write(record.CustomText ?? string.Empty);
                        writer.Write(record.UnitValue);
                        writer.Write(record.DeferredDays);
                        writer.Write(record.VirtualGrowth);
                        writer.Write(record.VirtualHungerDelta);
                        writer.Write(record.FoodShare);
                        writer.Write(record.HasHungerDistribution);
                        writer.Write(record.UnfedHunger);
                        writer.Write(record.FedHunger);
                        writer.Write(record.TotalHungerRate);
                        writer.Write(record.PrionRate);
                        writer.Write(record.AppliedPrionFraction);
                        writer.Write(record.DiseaseRate);
                        writer.Write(record.HealthStart);
                        writer.Write(record.DiseaseStart);
                        writer.Write(record.HealthStartGrowth);
                        writer.Write(record.HealthStartDays);
                        writer.Write(record.HealthHistory.Count);
                        foreach (HealthEvent healthEvent in record.HealthHistory)
                        {
                            writer.Write(healthEvent.InfectionChance);
                            writer.Write(healthEvent.Regen);
                            writer.Write(healthEvent.DiseaseDamage);
                            writer.Write(healthEvent.MaxHealth);
                            writer.Write(healthEvent.CureChance);
                            writer.Write(healthEvent.Growth);
                            writer.Write(healthEvent.Hunger);
                            writer.Write(healthEvent.PrionRate);
                            writer.Write(healthEvent.HasGrowthDistribution);
                            writer.Write(healthEvent.AlternativeGrowth);
                            writer.Write(healthEvent.GrowthProbability);
                        }
                        writer.Write(record.Tags.Count);
                        foreach (var pair in record.Tags)
                        {
                            TagState tag = pair.Value;
                            writer.Write(pair.Key);
                            writer.Write(tag.identifierName ?? string.Empty);
                            writer.Write(tag.valueEnabled);
                            writer.Write(tag.valueString ?? string.Empty);
                            writer.Write(tag.valueInt);
                            writer.Write(tag.valueFloat);
                            writer.Write(tag.valueLong);
                            writer.Write(tag.valueDouble);
                            writer.Write(tag.valueBool);
                        }
                        writer.Write(record.Variances.Count);
                        foreach (var pair in record.Variances)
                        {
                            writer.Write(pair.Key);
                            writer.Write(pair.Value);
                        }
                        writer.Write(record.Choices.Count);
                        foreach (var pair in record.Choices)
                        {
                            writer.Write(pair.Key);
                            writer.Write(pair.Value.Count);
                            foreach (var option in pair.Value)
                            {
                                writer.Write(option.Key);
                                writer.Write(option.Value);
                            }
                        }
                    }
                }
                string payload = Convert.ToBase64String(stream.ToArray());
                if (payload.Length > MaxPayloadCharacters)
                {
                    if (records.Count < 2) return false;
                    int before = records.Count;
                    CompactTo(records, Math.Max(1, records.Count / 2));
                    RatButcherPatch.Observer?.RecordAnimal("animal payload compressed: item="
                        + item.uniqueId + ", records_before=" + before
                        + ", records_after=" + records.Count);
                    return records.Count < before && Write(item, records);
                }
                Apply(item, records[records.Count - 1]);
                item.ModifyTag(PayloadTag, tag => { tag.Enable(); tag.SetString(payload); }, false);
                string groupLineage = PrimaryLineage(records);
                item.ModifyTag(GroupLineageTag, tag => { tag.Enable(); tag.SetString(groupLineage); }, false);
                Validated.Remove(item);
                Validated.Add(item, new Validity { Count = item.unitCount, Payload = payload });
                item.Validate();
                return true;
            }
        }

        /// <summary>Corrects saved panels against each animal's native maximum.</summary>
        private static void NormalizeHealthRecords(GameItem source,
            List<AnimalRecord> records)
        {
            GameItem probe = NativeSplitPreparation.CreateCopy(source, 1);
            int corrected = 0;
            try
            {
                probe.SetUnitCount(1);
                foreach (AnimalRecord record in records)
                {
                    Apply(probe, record);
                    float maximum = GeneticHelper.GetCurrentStat(probe,
                        "ANIMAL_MAX_HEALTH_TAG");
                    TagState health = record.Tag("ANIMAL_HEALTH_TAG");
                    if (health != null)
                    {
                        float before = health.GetFloat();
                        float next = record.IsDead ? 0f : ClampHealth(before, maximum);
                        if (before != next) { health.SetFloat(next); corrected++; }
                    }
                    float startMaximum = record.HealthHistory.Count > 0
                        ? record.HealthHistory[0].MaxHealth : maximum;
                    float start = ClampHealth(record.HealthStart, startMaximum);
                    if (record.HealthStart != start)
                    {
                        record.HealthStart = start;
                        corrected++;
                    }
                }
            }
            finally { if (probe.parentInventory == null) probe.Destroy(); }
            if (corrected > 0)
                RatButcherPatch.Observer?.RecordAnimal("health bounds corrected: item="
                    + source.uniqueId + ", fields=" + corrected);
        }

        /// <summary>Restores a single animal's original tags and visible item fields.</summary>
        private static void Apply(GameItem item, AnimalRecord record)
        {
            item.modifiedState.dict.Clear();
            foreach (var pair in record.Tags) item.modifiedState.dict.Add(pair.Key, pair.Value.Clone());
            item.spritePath = record.SpritePath;
            item.customText = record.CustomText;
            item.unitValue = record.UnitValue;
        }

        private static List<AnimalRecord> TakeTop(List<AnimalRecord> records,
            int amount, bool remove)
        {
            if (amount < 1 || Count(records) < amount) return null;
            var picks = new int[records.Count];
            int remaining = amount;
            // Previously extracted real rats keep their established top priority.
            for (int i = records.Count - 1; i >= 0 && remaining > 0; i--)
            {
                if (!records[i].WasExtracted) continue;
                picks[i] = Math.Min(records[i].Quantity, remaining);
                remaining -= picks[i];
            }
            while (remaining > 0)
            {
                int latest = -1, sufficient = -1;
                for (int i = records.Count - 1; i >= 0; i--)
                {
                    if (records[i].WasExtracted || picks[i] == records[i].Quantity)
                        continue;
                    if (latest < 0) latest = i;
                    long available = 0;
                    for (int j = 0; j < records.Count; j++)
                        if (!records[j].WasExtracted
                            && (i == j || SamePile(records[i], records[j])))
                            available += records[j].Quantity - picks[j];
                    if (available >= remaining) { sufficient = i; break; }
                }
                int chosen = sufficient >= 0 ? sufficient : latest;
                if (chosen < 0) return null;
                for (int i = records.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    if (records[i].WasExtracted
                        || i != chosen && !SamePile(records[chosen], records[i]))
                        continue;
                    int taken = Math.Min(records[i].Quantity - picks[i], remaining);
                    picks[i] += taken;
                    remaining -= taken;
                }
            }
            var selected = new List<AnimalRecord>();
            for (int i = 0; i < records.Count; i++)
                if (picks[i] > 0) selected.Add(Copy(records[i], picks[i]));
            if (remove)
                for (int i = records.Count - 1; i >= 0; i--)
                    if (picks[i] > 0)
                    {
                        records[i].Quantity -= picks[i];
                        if (records[i].Quantity == 0) records.RemoveAt(i);
                    }
            return selected;
        }

        /// <summary>Removes virtual units first, then real units from the stack bottom.</summary>
        internal static long TrimToKeep(List<AnimalRecord> records, long keep)
        {
            long discard = Math.Max(0L, Count(records) - keep);
            long original = discard;
            for (int i = 0; i < records.Count && discard > 0;)
            {
                AnimalRecord record = records[i];
                if (record.WasExtracted) { i++; continue; }
                int removed = (int)Math.Min(discard, record.Quantity);
                record.Quantity -= removed;
                discard -= removed;
                if (record.Quantity == 0) records.RemoveAt(i);
                else i++;
            }
            while (records.Count > 0 && discard > 0)
            {
                AnimalRecord bottom = records[0];
                int removed = (int)Math.Min(discard, bottom.Quantity);
                bottom.Quantity -= removed;
                discard -= removed;
                if (bottom.Quantity == 0) records.RemoveAt(0);
            }
            return original - discard;
        }

        /// <summary>Handles an overflowing animal merge before native Int32 addition.</summary>
        internal static bool MergeOverflow(GameItem source, GameItem target, int requested)
        {
            if (source == null || target == null || requested < 1
                || requested > source.unitCount
                || !TryRead(source, out List<AnimalRecord> sourceRecords)
                || !TryRead(target, out List<AnimalRecord> targetRecords)) return false;
            int sourceLeft = source.unitCount - requested;
            List<AnimalRecord> moved = TakeTop(sourceRecords, requested, true);
            if (moved == null) return false;
            targetRecords.AddRange(moved);
            long discarded = TrimToKeep(targetRecords, StackLimitPolicy.OverflowKeep);
            if (Count(targetRecords) != StackLimitPolicy.OverflowKeep
                || Count(sourceRecords) != sourceLeft) return false;
            target.SetUnitCount(StackLimitPolicy.OverflowKeep);
            if (!Write(target, targetRecords)) return false;
            if (sourceLeft == 0) source.Destroy();
            else
            {
                source.SetUnitCount(sourceLeft);
                if (sourceLeft == 1 && sourceRecords.Count == 1
                    && !KeepVirtualSingleton(sourceRecords[0]))
                    Materialize(sourceRecords[0]);
                if (!Write(source, sourceRecords)) return false;
            }
            RatButcherPatch.Observer?.RecordAnimal("animal merge overflow: target=" + target.uniqueId
                + ", retained=" + target.unitCount + ", discarded=" + discarded);
            return true;
        }

        internal static bool CopyTop(GameItem source, GameItem destination, int amount)
        {
            if (!TryRead(source, out List<AnimalRecord> records) || amount < 1
                || amount > Count(records)) return false;
            destination.SetUnitCount(amount);
            var selected = TakeTop(records, amount, false);
            if (amount == 1) Materialize(selected[0]);
            return Write(destination, selected);
        }

        /// <summary>Settles the virtual age, hunger and weight when an animal leaves its group.</summary>
        internal static void Materialize(AnimalRecord record)
        {
            if (record.Quantity != 1) return;
            bool wasVirtual = !record.WasExtracted;
            bool alreadyDead = record.IsDead;
            if (record.Variances.Count > 0 || record.Choices.Count > 0)
                SamplePanel(record);
            record.Tags.Remove(AnimalFertilityCoordinator.VirtualFertilityTag);
            if (wasVirtual) MaterializeHealth(record);
            if (alreadyDead)
            {
                // Rebuild native corpse details after sampling this individual's panel.
                record.PendingNativeDeath = wasVirtual;
                record.Tag("ANIMAL_HEALTH_TAG")?.SetFloat(0f);
            }
            SettleMean(record);
            bool sampledHungry = false;
            if (record.HasHungerDistribution)
            {
                lock (Samples) sampledHungry = Samples.NextDouble()
                    < record.TotalHungerRate;
                TagState hunger = record.Tag("ANIMAL_HUNGER_TAG");
                if (hunger != null) hunger.SetFloat(sampledHungry
                    ? record.UnfedHunger : record.FedHunger);
                if (!record.IsDead && hunger != null
                    && UpdateStarvation(record, hunger.GetFloat()))
                {
                    EnableNativeTag(record, "ANIMAL_IS_DEAD_TAG");
                    record.Tag("ANIMAL_HEALTH_TAG")?.SetFloat(0f);
                    record.PendingNativeDeath = true;
                }
                record.HasHungerDistribution = false;
                record.HungerRate = 0f;
                record.TotalHungerRate = sampledHungry ? 1d : 0d;
            }
            if (wasVirtual)
            {
                bool infected;
                double chance = PrionProbability(record);
                lock (Samples) infected = Samples.NextDouble() < chance;
                ApplyPrionBonusChange(record,
                    (infected ? 1d : 0d) - record.AppliedPrionFraction);
                record.PrionRate = infected ? 1d : 0d;
                record.AppliedPrionFraction = record.PrionRate;
                SetPrionTag(record, infected);
            }
            SetHungryCohort(record, sampledHungry && !record.IsDead);
            if (!record.Tags.TryGetValue(ExtractedTag, out TagState extracted))
            {
                extracted = new TagState(ExtractedTag, ExtractedTag);
                record.Tags.Add(ExtractedTag, extracted);
            }
            extracted.Enable();
            EnsureIdentity(record);
        }

        /// <summary>Gives each real rat a stable key across stack moves and save loads.</summary>
        internal static string EnsureIdentity(AnimalRecord record)
        {
            TagState identity = record.Tag(IdentityTag);
            string value = identity?.GetString();
            if (!string.IsNullOrEmpty(value)) return value;
            value = Guid.NewGuid().ToString("N");
            if (identity == null)
            {
                identity = new TagState(IdentityTag, IdentityTag);
                record.Tags.Add(IdentityTag, identity);
            }
            identity.Enable();
            identity.SetString(value);
            return value;
        }

        /// <summary>Refreshes one individual record after its separate native growth calculation.</summary>
        internal static void CaptureIndividual(GameItem item, AnimalRecord record)
        {
            ClampNativeHealth(item);
            record.Tags.Clear();
            foreach (var pair in item.modifiedState.dict)
                if (pair.Key != PayloadTag && pair.Key != GroupLineageTag && pair.Value != null)
                    record.Tags.Add(pair.Key, pair.Value.Clone());
            record.SpritePath = item.spritePath;
            record.CustomText = item.customText;
            record.UnitValue = item.unitValue;
            record.PrionRate = item.IsTag(PrionTag) ? 1d : 0d;
            record.AppliedPrionFraction = record.PrionRate;
            record.PendingPrionIncrement = 0d;
            record.DiseaseRate = item.IsTag(DiseaseTag) ? 1d : 0d;
            record.HealthHistory.Clear();
            record.PendingNativeDeath = false;
        }

        /// <summary>Allows one visible stack while retaining separate parent records.</summary>
        internal static bool CanMergeLineage(GameItem source, GameItem target)
        {
            if (!TryRead(source, out List<AnimalRecord> sourceRecords)
                || !TryRead(target, out List<AnimalRecord> targetRecords)) return false;
            long payloadLength = (long)(source.GetTagReadonly(PayloadTag).GetString()?.Length ?? 0)
                + (target.GetTagReadonly(PayloadTag).GetString()?.Length ?? 0);
            if (payloadLength >= MaxPayloadCharacters - 4096) return false;
            if ((long)sourceRecords.Count + targetRecords.Count <= MaxIndividualRecords)
                return true;
            var groups = new HashSet<string>(StringComparer.Ordinal);
            int opaque = 0;
            foreach (AnimalRecord record in sourceRecords)
                if (TryGetPileIdentity(record, out string identity)) groups.Add(identity);
                else opaque++;
            foreach (AnimalRecord record in targetRecords)
                if (TryGetPileIdentity(record, out string identity)) groups.Add(identity);
                else opaque++;
            return groups.Count + opaque <= MaxIndividualRecords;
        }

        private static string PrimaryLineage(List<AnimalRecord> records)
        {
            string first = null;
            foreach (AnimalRecord record in records)
                if (!record.WasExtracted)
                {
                    if (!TryGetPileIdentity(record, out string group)) return "mixed";
                    if (first == null) first = group;
                    else if (!string.Equals(first, group, StringComparison.Ordinal))
                        return "mixed";
                }
            return first == null ? "extracted" : "group:" + first;
        }

        internal static float Expected(AnimalRecord record, string tag)
        {
            TagState current = record.Tag(tag);
            float value = tag == "ANIMAL_AGE_TAG" ? current?.GetInt() ?? 0 : current?.GetFloat() ?? 0f;
            if (tag == "ANIMAL_WEIGHT_TAG") value += record.VirtualGrowth;
            if (tag == "ANIMAL_HUNGER_TAG") value += record.VirtualHungerDelta;
            if (tag == "ANIMAL_AGE_TAG") value += record.DeferredDays;
            return value;
        }

        internal static bool ConsumeTop(GameItem source, int amount)
        {
            if (!TryRead(source, out List<AnimalRecord> records) || amount < 1
                || amount > Count(records)) return false;
            int left = source.unitCount - amount;
            if (left == 0) { source.Destroy(); return true; }
            source.SetUnitCount(left);
            TakeTop(records, amount, true);
            if (left == 1 && records.Count == 1
                && !KeepVirtualSingleton(records[0])) Materialize(records[0]);
            return Write(source, records);
        }

        internal static void RestoreNativeSplit(GameItem source, GameItem split,
            List<AnimalRecord> before, int amount)
        {
            if (source == null || split == null || before == null || amount < 1
                || amount > Count(before) || split.unitCount != amount) return;
            int left = (int)(Count(before) - amount);
            var taken = TakeTop(before, amount, true);
            if (amount == 1) Materialize(taken[0]);
            Write(split, taken);
            if (left > 0 && source.unitCount == left)
            {
                if (left == 1 && before.Count == 1
                    && !KeepVirtualSingleton(before[0])) Materialize(before[0]);
                Write(source, before);
            }
            RatButcherPatch.Observer?.RecordAnimal("native split: source=" + source.uniqueId
                + ", taken=" + split.uniqueId + ", units=" + amount + ", remaining=" + left);
        }

        internal static MergeSnapshot PrepareMerge(GameItem source, GameItem target, int amount)
        {
            var snapshot = new MergeSnapshot { TargetCount = target.unitCount };
            if (!ItemStackPolicy.IsAnimal(source)) return snapshot;
            if (!TryRead(source, out snapshot.Source) || !TryRead(target, out snapshot.Target)) return null;
            snapshot.Amount = amount == -1 ? source.unitCount : amount;
            if (snapshot.Amount < 1 || snapshot.Amount > source.unitCount
                || (long)target.unitCount + snapshot.Amount > MaxAnimalsPerStack) return null;
            return snapshot;
        }

        internal static void FinishMerge(GameItem source, GameItem target, MergeSnapshot snapshot)
        {
            if (snapshot.Target == null) return;
            int moved = target.unitCount - snapshot.TargetCount;
            if (moved < 1 || moved > snapshot.Amount || moved > Count(snapshot.Source)) return;
            snapshot.Target.AddRange(TakeTop(snapshot.Source, moved, true));
            // Keep insertion order inside both layers so the latest virtual pile stays on top.
            var virtualRecords = new List<AnimalRecord>(snapshot.Target.Count);
            var realRecords = new List<AnimalRecord>();
            foreach (AnimalRecord record in snapshot.Target)
                (record.WasExtracted ? realRecords : virtualRecords).Add(record);
            snapshot.Target.Clear();
            snapshot.Target.AddRange(virtualRecords);
            snapshot.Target.AddRange(realRecords);
            Write(target, snapshot.Target);
            if (source.unitCount > 0)
            {
                if (source.unitCount == 1 && snapshot.Source.Count == 1
                    && !KeepVirtualSingleton(snapshot.Source[0]))
                    Materialize(snapshot.Source[0]);
                Write(source, snapshot.Source);
            }
        }

        internal static int CountAnimals(GameInventory inventory, GameItem exclude = null)
        {
            if (inventory == null) return 0;
            long count = 0;
            foreach (GameItem item in inventory.childItems)
                if (item != null && !ReferenceEquals(item, exclude) && item.IsTag("ANIMAL_ITEM_TAG"))
                    count += Math.Max(1, item.unitCount);
            return count > int.MaxValue ? int.MaxValue : (int)count;
        }
    }
}
