using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;

namespace StolenMeat
{
    /// <summary>Runs native welfare probabilities for every rat represented by a stack.</summary>
    internal static class AnimalWelfareCoordinator
    {
        private const double EulerGamma = 0.5772156649015329d;
        private static readonly ConditionalWeakTable<GameInventory, ContainerState> Containers =
            new ConditionalWeakTable<GameInventory, ContainerState>();
        private static readonly Random VictimRandom = new Random();
        private static long dayEpoch;
        internal static long DayEpoch => dayEpoch;

        private sealed class ContainerState
        {
            internal long Epoch;
            internal long CompressedEpoch = -1;
            internal bool Initialized;
            internal bool Large;
            internal readonly Dictionary<GameItem, int> VirtualDeaths = new Dictionary<GameItem, int>();
            internal readonly Dictionary<GameItem, List<string>> RealDeaths =
                new Dictionary<GameItem, List<string>>();
            internal readonly HashSet<GameItem> Processed = new HashSet<GameItem>();
        }

        private sealed class PileShare
        {
            internal GameItem Item;
            internal int VirtualCount;
            internal int Deaths;
            internal decimal Remainder;
        }

        private sealed class RealRat
        {
            internal GameItem Item;
            internal string Identity;
            internal float Weight;
            internal long Sequence;
        }

        private sealed class RealRatComparer : IComparer<RealRat>
        {
            public int Compare(RealRat left, RealRat right)
            {
                int byWeight = left.Weight.CompareTo(right.Weight);
                return byWeight != 0 ? byWeight : left.Sequence.CompareTo(right.Sequence);
            }
        }

        internal static void ResetDay() => dayEpoch++;

        /// <summary>Snapshots container counts before the first native day-end mutation.</summary>
        internal static void BeginDayIfNeeded(GameInventory inventory)
        {
            if (inventory == null) return;
            ContainerState state = Containers.GetValue(inventory, _ => new ContainerState());
            if (state.CompressedEpoch != dayEpoch)
            {
                AnimalStackState.CompressContainer(inventory);
                state.CompressedEpoch = dayEpoch;
            }
            if (!HasStackedAnimal(inventory)) return;
            if (!state.Initialized || state.Epoch != dayEpoch)
                InitializeContainer(inventory, state);
        }
        /// <summary>Preserves every decision while limiting synchronous log writes.</summary>
        private sealed class DecisionLog
        {
            private readonly StringBuilder pending = new StringBuilder(8192);

            internal void Add(string detail)
            {
                if (pending.Length + detail.Length > 8192) Flush();
                pending.Append(detail).Append('\n');
            }

            internal void Flush()
            {
                if (pending.Length == 0) return;
                RatButcherPatch.Observer?.RecordAnimal(pending.ToString());
                pending.Clear();
            }
        }

        private sealed class Candidate
        {
            internal GameItem Item;
            internal List<AnimalStackState.AnimalRecord> Records;
            internal AnimalStackState.AnimalRecord Record;
            internal int Index;
            internal float Weight;
        }

        internal static int CrowdingLevel(GameInventory inventory)
        {
            if (inventory is not GameGridInventory grid) return 0;
            double area = (double)grid.inventoryShape.width * grid.inventoryShape.height;
            if (area <= 0) return 0;
            double occupied = 0;
            foreach (GameItem item in inventory.childItems)
                if (item != null && item.IsTag("ANIMAL_ITEM_TAG"))
                    occupied += (double)item.GetItemSize() * Math.Max(1, item.unitCount);
            double ratio = occupied / area;
            return ratio > 0.9d ? 3 : ratio > 0.7d ? 2 : ratio > 0.55d ? 1 : 0;
        }

        private static double ContaminationRatio(GameInventory inventory)
        {
            if (inventory is not GameGridInventory grid) return 0d;
            double area = (double)grid.inventoryShape.width * grid.inventoryShape.height;
            if (area <= 0) return 0d;
            double contaminated = 0;
            foreach (GameItem item in inventory.childItems)
            {
                if (item == null) continue;
                double footprint = (double)item.GetItemSize() * Math.Max(1, item.unitCount);
                if (item.IsTag("ANIMAL_FECES_TAG")) contaminated += footprint;
                if (item.IsTag("ANIMAL_IS_DEAD_TAG")) contaminated += footprint;
            }
            return contaminated / area;
        }

        internal static long CountLiveAnimals(GameInventory inventory)
        {
            if (inventory == null) return 0L;
            long count = 0L;
            foreach (GameItem item in inventory.childItems)
            {
                if (item == null || !item.IsTag("ANIMAL_ITEM_TAG")
                    || item.IsTag("ANIMAL_IS_DEAD_TAG")) continue;
                long units = Math.Max(1, item.unitCount);
                count = count > long.MaxValue - units ? long.MaxValue : count + units;
            }
            return count;
        }

        /// <summary>Computes a(n-H_n) for the aggregate death budget.</summary>
        /// <remarks>Large counts use Euler-Maclaurin to avoid an O(n) summation.</remarks>
        internal static double HarmonicExpectedDeaths(long n, double attackProbability)
        {
            if (n <= 1 || attackProbability <= 0d) return 0d;
            double harmonic;
            if (n <= 100000L)
            {
                harmonic = 0d;
                for (long i = 1; i <= n; i++) harmonic += 1d / i;
            }
            else
            {
                double size = n;
                harmonic = Math.Log(size) + EulerGamma + 1d / (2d * size)
                    - 1d / (12d * size * size);
            }
            return Math.Max(0d, attackProbability * ((double)n - harmonic));
        }

        /// <summary>Rounds a proportional share without multiplying two large counts.</summary>
        internal static long ShareDeaths(long deaths, long groupCount, long total)
        {
            if (deaths <= 0 || groupCount <= 0 || total <= 0) return 0;
            decimal fraction = (decimal)groupCount / total;
            long share = (long)decimal.Round((decimal)deaths * fraction,
                0, MidpointRounding.AwayFromZero);
            return Math.Min(groupCount, share);
        }

        /// <summary>Matches the game's two hygiene rolls and overcrowding bonus.</summary>
        internal static bool CheckHygiene(GameItem animal, GameInventory inventory,
            out double ratio, out int chance, out int crowding, out bool firstRoll)
        {
            ratio = ContaminationRatio(inventory);
            chance = 0;
            crowding = CrowdingLevel(inventory);
            firstRoll = false;
            if (inventory is not GameGridInventory || ratio < 0.25d) return false;
            float baseChance = 0f;
            if (ratio < 0.35d)
            {
                firstRoll = RNG.Roll(15);
                if (firstRoll) baseChance = 0.15f;
            }
            else
            {
                firstRoll = RNG.Roll(35);
                if (firstRoll) baseChance = 0.35f;
            }
            if (crowding > 0) baseChance += 0.2f * crowding;
            chance = (int)(baseChance * 100f);
            bool diseased = chance > 0 && RNG.Roll(chance);
            if (diseased) animal.EnableTag("ANIMAL_DISEASED_TAG", false);
            return diseased;
        }

        /// <summary>Computes the native two-roll hygiene risk without making either roll.</summary>
        private static double HygieneProbability(GameInventory inventory,
            out double ratio, out int crowding)
        {
            ratio = ContaminationRatio(inventory);
            crowding = CrowdingLevel(inventory);
            if (inventory is not GameGridInventory || ratio < 0.25d) return 0d;
            float firstChance = ratio < 0.35d ? 0.15f : 0.35f;
            float crowdBonus = 0.2f * crowding;
            int ifFirst = (int)((firstChance + crowdBonus) * 100f);
            int otherwise = (int)(crowdBonus * 100f);
            return firstChance * Math.Max(0d, Math.Min(1d, ifFirst / 100d))
                + (1d - firstChance) * Math.Max(0d, Math.Min(1d, otherwise / 100d));
        }

        private static double ImmunityDiseaseProbability(
            AnimalSpecies species, float immunity)
        {
            if (species == null || immunity >= species.immunityThreshold) return 0d;
            float ratio = species.immunityThreshold == 0f ? 0f
                : immunity / species.immunityThreshold;
            ratio = Math.Max(0f, Math.Min(1f, ratio));
            float native = (0.25f + (0.05f - 0.25f) * ratio) * 100f;
            return Math.Max(0d, Math.Min(1d, (int)native / 100d));
        }

        private static float NativeRegen(AnimalSpecies species, float immunity,
            float maxHealth)
        {
            if (species == null || maxHealth <= 0f) return 0f;
            float threshold = species.immunityRegenThreshold;
            float delta = (immunity - threshold) / threshold * 0.3f * maxHealth;
            return Math.Min(delta, maxHealth * 0.3f);
        }

        private static GameItem CreateVirtualHealthProxy(GameItem stack,
            AnimalStackState.AnimalRecord record)
        {
            var expected = AnimalStackState.Representative(record);
            double fedShare = AnimalStackState.IsHungryCohort(record) ? 0d
                : record.HasHungerDistribution ? 1d - record.HungerRate : 1d;
            AnimalStackState.AddPrionProbability(expected,
                expected.PendingPrionIncrement * fedShare);
            return AnimalStackHooks.CreateProxy(stack, expected);
        }

        /// <summary>Defers virtual disease draws; real rats keep native daily checks.</summary>
        internal static void CheckHygieneAndHealth(GameItem stack, GameInventory inventory,
            List<AnimalStackState.AnimalRecord> records)
        {
            if (inventory == null) return;
            var decisions = new DecisionLog();
            try
            {
                var groups = new Dictionary<string, List<AnimalStackState.AnimalRecord>>(StringComparer.Ordinal);
                for (int index = 0; index < records.Count; index++)
                {
                    var record = records[index];
                    if (record.WasExtracted || record.IsDead) continue;
                    string key = AnimalStackState.ProcessingPileKey(record, index);
                    if (!groups.TryGetValue(key, out var group))
                        groups.Add(key, group = new List<AnimalStackState.AnimalRecord>());
                    group.Add(record);
                }
                foreach (var virtualRecords in groups.Values)
                {
                    double hygiene = HygieneProbability(inventory,
                        out double ratio, out int crowding);
                    double immunityTotal = 0d, healthTotal = 0d;
                    double maxHealthTotal = 0d, beforeRate = 0d;
                    long virtualCount = AnimalStackState.Count(virtualRecords);
                    AnimalSpecies species = null;
                    foreach (AnimalStackState.AnimalRecord record in virtualRecords)
                    {
                        GameItem proxy = CreateVirtualHealthProxy(stack, record);
                        try
                        {
                            float immunity = GeneticHelper.GetCurrentStat(proxy,
                                "ANIMAL_IMMUNITY_TAG");
                            if (species == null) species = HusbandryHelper.GetSpecies(proxy);
                            immunityTotal += immunity * record.Quantity / virtualCount;
                            healthTotal += (record.Tag("ANIMAL_HEALTH_TAG")?.GetFloat() ?? 0f)
                                * record.Quantity / virtualCount;
                            maxHealthTotal += GeneticHelper.GetCurrentStat(proxy,
                                "ANIMAL_MAX_HEALTH_TAG") * record.Quantity / virtualCount;
                            beforeRate += record.DiseaseRate * record.Quantity / virtualCount;
                        }
                        finally { if (proxy.parentInventory == null) proxy.Destroy(); }
                    }
                    float sharedImmunity = (float)immunityTotal;
                    double immunityRisk = ImmunityDiseaseProbability(species,
                        sharedImmunity);
                    double cure = Math.Max(0d, Math.Min(1d,
                        (int)sharedImmunity / 100d));
                    double infection = hygiene + (1d - hygiene) * immunityRisk;
                    float sharedMaxHealth = (float)maxHealthTotal;
                    float sharedRegen = NativeRegen(species,
                        sharedImmunity, sharedMaxHealth);
                    float sharedDamage = sharedMaxHealth * 0.25f;
                    double diseasedBeforeCure = beforeRate
                        + (1d - beforeRate) * infection;
                    float sharedHealth = AnimalStackState.GroupHealthAfterDisease(
                        (float)healthTotal, sharedRegen, sharedDamage,
                        sharedMaxHealth, diseasedBeforeCure);
                    foreach (AnimalStackState.AnimalRecord record in virtualRecords)
                    {
                        record.DiseaseRate = beforeRate;
                        AnimalStackState.DeferHealth(record, infection, sharedRegen,
                            sharedDamage, sharedMaxHealth, cure);
                        record.Tag("ANIMAL_HEALTH_TAG")?.SetFloat(sharedHealth);
                        decisions.Add("hygiene deferred: stack=" + stack.uniqueId
                            + ", units=" + record.Quantity
                            + ", contamination=" + ratio.ToString("G9", CultureInfo.InvariantCulture)
                            + ", crowding=" + crowding
                            + ", hygiene_rate=" + hygiene.ToString("G9", CultureInfo.InvariantCulture)
                            + ", shared_immunity=" + sharedImmunity.ToString("G9", CultureInfo.InvariantCulture)
                            + ", immunity_rate=" + immunityRisk.ToString("G9", CultureInfo.InvariantCulture)
                            + ", disease_before=" + beforeRate.ToString("G9", CultureInfo.InvariantCulture)
                            + ", disease_rate=" + record.DiseaseRate.ToString("G9", CultureInfo.InvariantCulture)
                            + ", health_before=" + healthTotal.ToString("G9", CultureInfo.InvariantCulture)
                            + ", health_group=" + sharedHealth.ToString("G9", CultureInfo.InvariantCulture));
                    }
                    if (sharedHealth <= 0f)
                    {
                        long deaths = 0;
                        foreach (AnimalStackState.AnimalRecord record in virtualRecords)
                        {
                            if (record.IsDead) continue;
                            AnimalStackState.FinalizeVirtualCorpse(stack, record, false);
                            deaths += record.Quantity;
                        }
                        // A returned real rat belongs to the sole virtual pile only when
                        // the visible cell contains no other candidate virtual pile.
                        if (groups.Count == 1)
                            foreach (var record in records)
                                if (record.WasExtracted && !record.IsDead)
                                {
                                    AnimalStackState.FinalizeVirtualCorpse(stack, record, false);
                                    deaths += record.Quantity;
                                }
                        decisions.Add("group health death: stack=" + stack.uniqueId
                            + ", units=" + deaths
                            + ", disease_rate=" + diseasedBeforeCure.ToString("G9", CultureInfo.InvariantCulture));
                    }
                }
                for (int index = records.Count - 1; index >= 0; index--)
                {
                    AnimalStackState.AnimalRecord record = records[index];
                    if (!record.WasExtracted || record.IsDead) continue;
                    GameItem proxy = AnimalStackHooks.CreateProxy(stack, record);
                    try
                    {
                        float beforeHealth = proxy.GetTagReadonly("ANIMAL_HEALTH_TAG").GetFloat();
                        bool beforeDisease = proxy.IsTag("ANIMAL_DISEASED_TAG");
                        bool hygieneDisease = CheckHygiene(proxy, inventory,
                            out double ratio, out int chance, out int level, out bool firstRoll);
                        HusbandryHelper.CheckImmunityDisease(proxy);
                        HusbandryHelper.HandleRegen(proxy);
                        HusbandryHelper.HandleDisease(proxy);
                        float afterHealth = AnimalStackState.ClampNativeHealth(proxy);
                        CopyTag(proxy, record, "ANIMAL_HEALTH_TAG");
                        CopyTag(proxy, record, "ANIMAL_DISEASED_TAG");
                        decisions.Add("hygiene check: stack=" + stack.uniqueId
                            + ", member=" + index + ", animals=" + AnimalStackState.CountAnimals(inventory)
                            + ", contamination=" + ratio.ToString("G9", CultureInfo.InvariantCulture)
                            + ", crowding=" + level
                            + ", first_chance=" + (ratio < 0.25d ? 0 : ratio < 0.35d ? 15 : 35)
                            + ", first_roll=" + firstRoll
                            + ", disease_chance=" + chance + ", hygiene_disease=" + hygieneDisease
                            + ", diseased_before=" + beforeDisease + ", diseased_after=" + proxy.IsTag("ANIMAL_DISEASED_TAG")
                            + ", health_before=" + beforeHealth.ToString("G9", CultureInfo.InvariantCulture)
                            + ", health_after=" + afterHealth.ToString("G9", CultureInfo.InvariantCulture));
                        if (afterHealth <= 0f)
                            KillAt(stack, inventory, records, index, "disease or health");
                    }
                    finally { if (proxy.parentInventory == null) proxy.Destroy(); }
                }
            }
            finally { decisions.Flush(); }
        }

        private static void CopyTag(GameItem item, AnimalStackState.AnimalRecord record, string id)
        {
            TagState tag = item.GetTagReadonly(id);
            if (tag != null) record.Tags[id] = tag.Clone();
        }

        /// <summary>Scans live rats only after a native attack roll succeeds.</summary>
        private static Candidate FindNativeVictim(GameInventory inventory, GameItem source,
            List<AnimalStackState.AnimalRecord> sourceRecords,
            AnimalStackState.AnimalRecord attacker, out long comparisons,
            out long unknownComparisons)
        {
            comparisons = 0;
            unknownComparisons = 0;
            float attackerWeight = AnimalStackState.Expected(attacker, "ANIMAL_WEIGHT_TAG");
            Candidate lightest = null;
            foreach (GameItem item in inventory.childItems)
            {
                if (item == null || !item.IsTag("ANIMAL_ITEM_TAG") || item.IsTag("ANIMAL_IS_DEAD_TAG")) continue;
                List<AnimalStackState.AnimalRecord> members = ReferenceEquals(item, source)
                    ? sourceRecords : AnimalStackState.TryRead(item, out var loaded) ? loaded : null;
                if (members == null) continue;
                for (int i = 0; i < members.Count; i++)
                {
                    if (members[i].IsDead || ReferenceEquals(members[i], attacker)) continue;
                    comparisons++;
                    float weight = AnimalStackState.Expected(members[i], "ANIMAL_WEIGHT_TAG");
                    bool samePile = ReferenceEquals(item, source)
                        && AnimalStackState.SamePile(attacker, members[i]);
                    bool lighter;
                    if (samePile)
                    {
                        unknownComparisons++;
                        lighter = RNG.Roll(50);
                    }
                    else lighter = !float.IsNaN(weight) && !float.IsNaN(attackerWeight)
                        && weight < attackerWeight;
                    if (!lighter) continue;
                    if (lightest != null)
                    {
                        if (samePile && ReferenceEquals(lightest.Item, source)
                            && AnimalStackState.SamePile(attacker, lightest.Record))
                        {
                            unknownComparisons++;
                            if (!RNG.Roll(50)) continue;
                        }
                        else if (float.IsNaN(weight) || weight >= lightest.Weight) continue;
                    }
                    lightest = new Candidate { Item = item, Records = members,
                        Record = members[i], Index = i, Weight = weight };
                }
            }
            return lightest;
        }

        /// <summary>Captures one container-wide death budget for the current day.</summary>
        private static void InitializeContainer(GameInventory inventory, ContainerState state)
        {
            state.Epoch = dayEpoch;
            state.Initialized = true;
            state.VirtualDeaths.Clear();
            state.RealDeaths.Clear();
            state.Processed.Clear();
            var piles = new List<PileShare>();
            long virtualCount = 0, realCount = 0;
            foreach (GameItem item in inventory.childItems)
            {
                if (!ItemStackPolicy.IsAnimal(item) || item.IsTag("ANIMAL_IS_DEAD_TAG")) continue;
                if (!AnimalStackState.TryRead(item, out List<AnimalStackState.AnimalRecord> members))
                {
                    state.Large = false;
                    RatButcherPatch.Observer?.RecordAnimal("crowding fallback: invalid animal stack=" + item.uniqueId);
                    return;
                }
                int count = 0, virtualInPile = 0;
                foreach (AnimalStackState.AnimalRecord member in members)
                {
                    if (member.IsDead) continue;
                    count += member.Quantity;
                    if (AnimalPopulationPolicy.IsVirtual(item, member))
                    {
                        virtualCount += member.Quantity;
                        virtualInPile += member.Quantity;
                    }
                    else realCount += member.Quantity;
                }
                if (count > 0) piles.Add(new PileShare
                    { Item = item, VirtualCount = virtualInPile });
            }
            long total = virtualCount + realCount;
            state.Large = AnimalPopulationPolicy.UseUnified(virtualCount, realCount);
            if (!state.Large) return;
            int level = CrowdingLevel(inventory);
            double probability = level == 2 ? 0.05d : level >= 3 ? 0.15d : 0d;
            double expected = HarmonicExpectedDeaths(total, probability);
            long guaranteed = (long)Math.Floor(expected);
            int remainderPercent = Math.Clamp((int)Math.Round((expected - guaranteed) * 100d), 0, 100);
            long deaths = Math.Min(total - 1, guaranteed + (remainderPercent > 0 && RNG.Roll(remainderPercent) ? 1 : 0));
            long virtualDeaths = ShareDeaths(deaths, virtualCount, total);
            long realDeaths = deaths - virtualDeaths;
            long assigned = 0;
            foreach (PileShare pile in piles)
            {
                if (pile.VirtualCount == 0) continue;
                decimal share = (decimal)virtualDeaths * pile.VirtualCount / virtualCount;
                pile.Deaths = (int)decimal.Floor(share);
                pile.Remainder = share - pile.Deaths;
                assigned += pile.Deaths;
            }
            piles.Sort((left, right) => right.Remainder.CompareTo(left.Remainder));
            for (long i = 0; i < virtualDeaths - assigned; i++)
                if (piles[(int)i].Deaths < piles[(int)i].VirtualCount) piles[(int)i].Deaths++;
            foreach (PileShare pile in piles) state.VirtualDeaths[pile.Item] = pile.Deaths;

            // The ordered index is rebuilt from saved real panels and inserted by weight.
            var realOrder = new SortedSet<RealRat>(new RealRatComparer());
            long sequence = 0;
            foreach (PileShare pile in piles)
            {
                if (!AnimalStackState.TryRead(pile.Item, out List<AnimalStackState.AnimalRecord> members)) continue;
                bool changed = false;
                foreach (AnimalStackState.AnimalRecord member in members)
                {
                    if (member.IsDead || AnimalPopulationPolicy.IsVirtual(pile.Item, member)) continue;
                    bool needsIdentity = string.IsNullOrEmpty(member.Tag(AnimalStackState.IdentityTag)?.GetString());
                    string identity = AnimalStackState.EnsureIdentity(member);
                    changed |= needsIdentity;
                    float weight = AnimalStackState.Expected(member, "ANIMAL_WEIGHT_TAG");
                    if (float.IsNaN(weight)) weight = float.MaxValue;
                    if (realDeaths > 0)
                    {
                        realOrder.Add(new RealRat { Item = pile.Item, Identity = identity,
                            Weight = weight, Sequence = sequence });
                        if (realOrder.Count > realDeaths) realOrder.Remove(realOrder.Max);
                    }
                    sequence++;
                }
                if (changed && !AnimalStackState.Write(pile.Item, members))
                {
                    state.Large = false;
                    state.VirtualDeaths.Clear();
                    RatButcherPatch.Observer?.RecordAnimal("crowding fallback: real identity could not be saved="
                        + pile.Item.uniqueId);
                    return;
                }
            }
            long selectedReal = 0;
            foreach (RealRat rat in realOrder)
            {
                if (selectedReal >= realDeaths) break;
                if (!state.RealDeaths.TryGetValue(rat.Item, out List<string> identities))
                    state.RealDeaths.Add(rat.Item, identities = new List<string>());
                identities.Add(rat.Identity);
                selectedReal++;
            }
            RatButcherPatch.Observer?.RecordAnimal("crowding container: animals=" + total
                + ", virtual=" + virtualCount + ", real=" + realCount + ", level=" + level
                + ", native_attack_chance=" + (int)(probability * 100d)
                + ", harmonic_expected=" + expected.ToString("G17", CultureInfo.InvariantCulture)
                + ", fractional_roll=" + remainderPercent + ", deaths=" + deaths
                + ", virtual_deaths=" + virtualDeaths + ", real_deaths=" + selectedReal
                + ", real_indexed=" + sequence + ", real_queue=" + realOrder.Count
                + ", piles=" + piles.Count);
        }

        /// <summary>Uses random virtual victims and the globally lightest real panels.</summary>
        private static void ProcessLargePile(GameItem source, GameInventory inventory,
            List<AnimalStackState.AnimalRecord> records, ContainerState state)
        {
            int assignedVirtual = state.VirtualDeaths.TryGetValue(source, out int count) ? count : 0;
            var assignedReal = state.RealDeaths.TryGetValue(source, out List<string> identities)
                ? new HashSet<string>(identities, StringComparer.Ordinal) : null;
            var virtualMembers = new List<int>();
            var selected = new List<int>();
            long virtualUnits = 0;
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].IsDead) continue;
                if (AnimalPopulationPolicy.IsVirtual(source, records[i]))
                {
                    virtualMembers.Add(i);
                    virtualUnits += records[i].Quantity;
                }
                else if (assignedReal != null
                    && assignedReal.Contains(records[i].Tag(AnimalStackState.IdentityTag)?.GetString()))
                    selected.Add(i);
            }
            int realSelected = selected.Count;
            int virtualSelected = (int)Math.Min(assignedVirtual, virtualUnits);
            bool compactVirtual = virtualUnits > virtualMembers.Count;
            if (!compactVirtual)
            {
                for (int i = 0; i < virtualSelected; i++)
                {
                    int pick;
                    lock (VictimRandom) pick = VictimRandom.Next(virtualMembers.Count);
                    selected.Add(virtualMembers[pick]);
                    virtualMembers.RemoveAt(pick);
                }
            }
            selected.Sort((left, right) => right.CompareTo(left));
            KillBatch(source, inventory, records, selected);
            if (compactVirtual && virtualSelected > 0 && !source.IsTag("ANIMAL_IS_DEAD_TAG"))
                KillVirtualByPile(source, inventory, records, virtualSelected);
            if (!source.IsTag("ANIMAL_IS_DEAD_TAG") && source.unitCount == 1
                && records.Count == 1 && !AnimalStackState.KeepVirtualSingleton(records[0])
                && !records[0].WasExtracted)
            {
                AnimalStackState.Materialize(records[0]);
                AnimalStackState.Write(source, records);
            }
            RatButcherPatch.Observer?.RecordAnimal("crowding pile: stack=" + source.uniqueId
                + ", virtual_allocated=" + assignedVirtual + ", virtual_deaths=" + virtualSelected
                + ", real_allocated=" + (identities?.Count ?? 0) + ", real_deaths=" + realSelected
                + ", actual_deaths=" + (realSelected + virtualSelected) + ", real_order=lightest_first"
                + ", remaining=" + AnimalStackState.Count(records));
        }

        private sealed class VirtualDeathShare
        {
            internal readonly List<AnimalStackState.AnimalRecord> Records =
                new List<AnimalStackState.AnimalRecord>();
            internal long Count;
            internal int Deaths;
            internal decimal Remainder;
        }

        /// <summary>Distributes the cell's virtual deaths over its distinct rat piles.</summary>
        private static void KillVirtualByPile(GameItem source, GameInventory inventory,
            List<AnimalStackState.AnimalRecord> records, int deaths)
        {
            var groups = new Dictionary<string, VirtualDeathShare>(StringComparer.Ordinal);
            long total = 0;
            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record.WasExtracted || record.IsDead) continue;
                string key = AnimalStackState.ProcessingPileKey(record, i);
                if (!groups.TryGetValue(key, out var share))
                    groups.Add(key, share = new VirtualDeathShare());
                share.Records.Add(record);
                share.Count += record.Quantity;
                total += record.Quantity;
            }
            if (total == 0) return;
            var shares = new List<VirtualDeathShare>(groups.Values);
            int assigned = 0;
            foreach (var share in shares)
            {
                decimal expected = (decimal)deaths * share.Count / total;
                share.Deaths = (int)decimal.Floor(expected);
                share.Remainder = expected - share.Deaths;
                assigned += share.Deaths;
            }
            shares.Sort((a, b) => b.Remainder.CompareTo(a.Remainder));
            for (int i = 0; i < deaths - assigned; i++) shares[i].Deaths++;
            foreach (var share in shares)
            {
                int remaining = share.Deaths;
                foreach (var record in share.Records)
                {
                    if (remaining <= 0 || source.IsTag("ANIMAL_IS_DEAD_TAG")) break;
                    int take = Math.Min(remaining, record.Quantity);
                    KillVirtualCompressed(source, inventory, records, take, true, record);
                    remaining -= take;
                }
            }
        }
        /// <summary>Settles victims individually, then writes survivors and corpses once.</summary>
        private static void KillBatch(GameItem source, GameInventory inventory,
            List<AnimalStackState.AnimalRecord> records, List<int> descendingIndices)
        {
            if (descendingIndices.Count == 0) return;
            var deadRecords = new List<AnimalStackState.AnimalRecord>(descendingIndices.Count);
            GameItem corpseStack = null;
            foreach (int index in descendingIndices)
            {
                AnimalStackState.AnimalRecord record = records[index];
                AnimalStackState.Materialize(record);
                GameItem corpse = AnimalStackHooks.CreateProxy(source, record);
                corpse.EnableTag("ANIMAL_SCRACH_MARK_TAG", false);
                HusbandryHelper.OnAnimalDead(corpse, true);
                AnimalStackState.CaptureIndividual(corpse, record);
                deadRecords.Add(record);
                records.RemoveAt(index);
                if (corpseStack == null) corpseStack = corpse;
                else corpse.Destroy();
            }
            if (records.Count == 0)
            {
                source.SetUnitCount(deadRecords.Count);
                AnimalStackState.Write(source, deadRecords);
                corpseStack.Destroy();
                RatButcherPatch.Observer?.RecordAnimal("crowding batch: source=" + source.uniqueId
                    + ", deaths=" + deadRecords.Count + ", all_dead=True");
                return;
            }
            source.SetUnitCount((int)AnimalStackState.Count(records));
            AnimalStackState.Write(source, records);
            corpseStack.SetUnitCount(deadRecords.Count);
            AnimalStackState.Write(corpseStack, deadRecords);
            bool placed = false;
            try
            {
                placed = GraphUtils.TryAcceptAll(inventory, corpseStack, -1) == 0;
                if (!placed && OverflowPatch.Coordinator != null)
                    placed = OverflowPatch.Coordinator.TryRescue(corpseStack, "animal crowding full");
            }
            catch (Exception error)
            {
                RatButcherPatch.Observer?.RecordAnimal("crowding batch placement error: " + error);
            }
            if (!placed && corpseStack.parentInventory == null) corpseStack.Destroy();
            RatButcherPatch.Observer?.RecordAnimal("crowding batch: source=" + source.uniqueId
                + ", deaths=" + deadRecords.Count + ", corpse=" + corpseStack.uniqueId
                + ", outcome=" + (placed ? "placed" : "discarded"));
        }

        /// <summary>Turns a counted virtual group into corpse records without creating one item per rat.</summary>
        private static void KillVirtualCompressed(GameItem source, GameInventory inventory,
            List<AnimalStackState.AnimalRecord> records, int deaths,
            bool attack = true, AnimalStackState.AnimalRecord only = null)
        {
            var deadRecords = new List<AnimalStackState.AnimalRecord>();
            GameItem corpseStack = null;
            int remaining = deaths;
            for (int i = 0; i < records.Count && remaining > 0;)
            {
                AnimalStackState.AnimalRecord record = records[i];
                if (record.WasExtracted || record.IsDead
                    || only != null && !ReferenceEquals(record, only))
                {
                    i++;
                    continue;
                }
                int taken = Math.Min(record.Quantity, remaining);
                AnimalStackState.AnimalRecord dead = AnimalStackState.CopyUnits(record, taken);
                GameItem proxy = AnimalStackHooks.CreateProxy(source, dead);
                if (attack) proxy.EnableTag("ANIMAL_SCRACH_MARK_TAG", false);
                HusbandryHelper.OnAnimalDead(proxy, true);
                AnimalStackState.CaptureIndividual(proxy, dead);
                deadRecords.Add(dead);
                if (corpseStack == null) corpseStack = proxy;
                else proxy.Destroy();
                record.Quantity -= taken;
                remaining -= taken;
                if (record.Quantity == 0) records.RemoveAt(i);
                else i++;
            }
            if (deadRecords.Count == 0) return;
            int deadUnits = deaths - remaining;
            long survivors = AnimalStackState.Count(records);
            if (survivors == 0)
            {
                source.SetUnitCount(deadUnits);
                AnimalStackState.Write(source, deadRecords);
                corpseStack.Destroy();
                return;
            }
            source.SetUnitCount((int)survivors);
            if (survivors == 1 && records.Count == 1
                && !AnimalStackState.KeepVirtualSingleton(records[0]))
                AnimalStackState.Materialize(records[0]);
            AnimalStackState.Write(source, records);
            corpseStack.SetUnitCount(deadUnits);
            AnimalStackState.Write(corpseStack, deadRecords);
            bool placed = false;
            try
            {
                placed = GraphUtils.TryAcceptAll(inventory, corpseStack, -1) == 0;
                if (!placed && OverflowPatch.Coordinator != null)
                    placed = OverflowPatch.Coordinator.TryRescue(corpseStack, "animal crowding full");
            }
            catch (Exception error)
            {
                RatButcherPatch.Observer?.RecordAnimal("virtual corpse placement error: " + error);
            }
            if (!placed && corpseStack.parentInventory == null) corpseStack.Destroy();
            RatButcherPatch.Observer?.RecordAnimal("virtual deaths: source=" + source.uniqueId
                + ", units=" + deadUnits + ", corpse=" + corpseStack.uniqueId
                + ", outcome=" + (placed ? "placed" : "discarded"));
        }

        /// <summary>Uses the container budget for large groups and native rolls for small ones.</summary>
        internal static void CheckOvercrowding(GameItem source, GameInventory inventory,
            List<AnimalStackState.AnimalRecord> records)
        {
            if (inventory == null || records == null) return;
            ContainerState state = Containers.GetValue(inventory, _ => new ContainerState());
            if (!state.Initialized || state.Epoch != dayEpoch)
                InitializeContainer(inventory, state);
            if (!state.Processed.Add(source)) return;
            if (state.Large)
            {
                ProcessLargePile(source, inventory, records, state);
                return;
            }
            var attackers = new List<AnimalStackState.AnimalRecord>(records);
            var active = new HashSet<AnimalStackState.AnimalRecord>(records);
            var decisions = new DecisionLog();
            long checks = 0, deaths = 0;
            try
            {
                foreach (AnimalStackState.AnimalRecord attacker in attackers)
                {
                    if (!active.Contains(attacker) || attacker.IsDead || source.IsTag("ANIMAL_IS_DEAD_TAG")) continue;
                    long live = CountLiveAnimals(inventory);
                    int level = CrowdingLevel(inventory);
                    int chance = level == 2 ? 5 : level >= 3 ? 15 : 0;
                    bool triggered = chance > 0 && RNG.Roll(chance);
                    long comparisons = 0, unknownComparisons = 0;
                    Candidate victim = triggered
                        ? FindNativeVictim(inventory, source, records, attacker,
                            out comparisons, out unknownComparisons) : null;
                    decisions.Add("crowding check: stack=" + source.uniqueId
                        + ", animals=" + live + ", level=" + level
                        + ", native_attack_chance=" + chance + ", attack_roll=" + triggered
                        + ", weight_comparisons=" + comparisons
                        + ", unknown_50pct=" + unknownComparisons
                        + ", victim=" + (victim == null ? "none" : victim.Item.uniqueId + ":" + victim.Index));
                    checks++;
                    if (victim == null) continue;
                    if (ReferenceEquals(victim.Item, source)) active.Remove(victim.Record);
                    KillAt(victim.Item, inventory, victim.Records, victim.Index, "crowding attack");
                    deaths++;
                }
            }
            finally
            {
                decisions.Flush();
                RatButcherPatch.Observer?.RecordAnimal("crowding summary: stack=" + source.uniqueId
                    + ", checks=" + checks + ", deaths=" + deaths
                    + ", mode=native_per_rat");
            }
        }

        private static void KillAt(GameItem stack, GameInventory inventory,
            List<AnimalStackState.AnimalRecord> records, int index, string reason)
        {
            if (index < 0 || index >= records.Count) return;
            AnimalStackState.AnimalRecord record = records[index];
            AnimalStackState.Materialize(record);
            if (records.Count == 1)
            {
                AnimalStackState.Write(stack, records);
                if (reason == "crowding attack") stack.EnableTag("ANIMAL_SCRACH_MARK_TAG", false);
                HusbandryHelper.OnAnimalDead(stack, true);
                AnimalStackState.CaptureIndividual(stack, record);
                RatButcherPatch.Observer?.RecordAnimal(reason + ": single corpse=" + stack.uniqueId);
                return;
            }
            GameItem corpse = AnimalStackHooks.CreateProxy(stack, record);
            if (reason == "crowding attack") corpse.EnableTag("ANIMAL_SCRACH_MARK_TAG", false);
            HusbandryHelper.OnAnimalDead(corpse, true);
            bool placed = false;
            try
            {
                placed = inventory != null && GraphUtils.TryAcceptAll(inventory, corpse, -1) == 0;
                if (!placed && OverflowPatch.Coordinator != null)
                    placed = OverflowPatch.Coordinator.TryRescue(corpse, "animal crowding full");
            }
            catch (Exception error)
            {
                RatButcherPatch.Observer?.RecordAnimal(reason + ": corpse placement error: " + error);
            }
            if (!placed && corpse.parentInventory == null) corpse.Destroy();
            records.RemoveAt(index);
            stack.SetUnitCount(records.Count);
            if (records.Count == 1 && !AnimalStackState.KeepVirtualSingleton(records[0]))
                AnimalStackState.Materialize(records[0]);
            AnimalStackState.Write(stack, records);
            RatButcherPatch.Observer?.RecordAnimal(reason + ": corpse=" + corpse.uniqueId
                + ", source=" + stack.uniqueId + ", remaining=" + records.Count
                + ", outcome=" + (placed ? "placed" : "discarded"));
        }

        internal static bool HasStackedHygieneSource(GameInventory inventory)
        {
            if (inventory == null) return false;
            foreach (GameItem item in inventory.childItems)
                if (item != null && item.unitCount > 1
                    && (item.IsTag("ANIMAL_IS_DEAD_TAG") || item.IsTag("ANIMAL_FECES_TAG"))) return true;
            return false;
        }

        internal static bool HasStackedAnimal(GameInventory inventory)
        {
            if (inventory == null) return false;
            foreach (GameItem item in inventory.childItems)
                if (item != null && ItemStackPolicy.IsAnimal(item)
                    && (item.unitCount > 1 || AnimalStackState.HasVirtualSingleton(item))
                    && !item.IsTag("ANIMAL_IS_DEAD_TAG")) return true;
            return false;
        }
    }

    /// <summary>Counts every corpse or waste unit in native hygiene checks.</summary>
    [HarmonyPatch(typeof(HusbandryHelper), nameof(HusbandryHelper.CheckHygiene))]
    internal static class AnimalHygienePatch
    {
        private static bool Prefix(GameItem __0, GameInventory __1)
        {
            if (!AnimalWelfareCoordinator.HasStackedHygieneSource(__1)) return true;
            AnimalWelfareCoordinator.CheckHygiene(__0, __1, out _, out _, out _, out _);
            return false;
        }
    }

    /// <summary>Uses native attack odds for each unit represented by a stack.</summary>
    [HarmonyPatch(typeof(HusbandryHelper), nameof(HusbandryHelper.HandleOvercrowding))]
    internal static class AnimalOvercrowdingPatch
    {
        private static bool Prefix(GameItem __0, GameInventory __1)
        {
            if (!ItemStackPolicy.IsAnimal(__0) || !AnimalWelfareCoordinator.HasStackedAnimal(__1)) return true;
            if (!AnimalStackState.TryRead(__0, out var records)) return true;
            try { AnimalWelfareCoordinator.CheckOvercrowding(__0, __1, records); }
            catch (Exception error)
            {
                RatButcherPatch.Observer?.RecordAnimal("crowding check failed: " + error);
            }
            return false;
        }
    }
}
