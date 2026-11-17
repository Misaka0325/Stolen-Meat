using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace StolenMeat
{
    /// <summary>Connects native animal lifecycle operations to per-animal stack records.</summary>
    internal static class AnimalStackHooks
    {
        private sealed class DayStamp { internal long Epoch; }
        private static readonly ConditionalWeakTable<GameItem, DayStamp> AdvancedToday =
            new ConditionalWeakTable<GameItem, DayStamp>();

        internal static void MarkAdvancedToday(GameItem item)
        {
            AdvancedToday.Remove(item);
            AdvancedToday.Add(item, new DayStamp { Epoch = AnimalWelfareCoordinator.DayEpoch });
        }

        internal static bool AlreadyAdvancedToday(GameItem item) => item != null
            && AdvancedToday.TryGetValue(item, out DayStamp stamp)
            && stamp.Epoch == AnimalWelfareCoordinator.DayEpoch;

        internal static bool AllAnimalsEnabled
        {
            get => ItemStackPolicy.AllAnimalsEnabled;
            set => ItemStackPolicy.AllAnimalsEnabled = value;
        }

        /// <summary>Calculates a group's one-animal growth and shares its food and gain.</summary>
        internal static bool AdvanceGroup(GameItem item, GameInventory inventory)
        {
            AnimalWelfareCoordinator.BeginDayIfNeeded(inventory);
            if (!AnimalStackState.TryRead(item, out List<AnimalStackState.AnimalRecord> records)) return false;
            if (item.unitCount < 2 && !AnimalStackState.HasVirtualSingleton(item)
                || item.IsTag("ANIMAL_IS_DEAD_TAG")) return false;
            var feedable = records.FindAll(record => !record.WasExtracted && !record.IsDead
                && (!AnimalStackState.IsHungryCohort(record)
                    || AnimalStackState.IsNativeStarving(record)));
            var hungry = records.FindAll(record => !record.IsDead
                && AnimalStackState.IsHungryCohort(record)
                && !AnimalStackState.IsNativeStarving(record));
            long virtualCount = AnimalStackState.Count(feedable);
            float foodPerAnimal = 0f;
            float growthPerAnimal = 0f;
            double foodTotal = 0d;
            foreach (var group in GroupVirtualPiles(feedable))
            {
                long count = AnimalStackState.Count(group);
                GameItem proxy = CreateProxy(item, group[0]);
                try
                {
                    float before = Average(group, "ANIMAL_HUNGER_TAG");
                    proxy.ModifyTag("ANIMAL_HUNGER_TAG", tag => tag.SetFloat(before), false);
                    HusbandryHelper.HandleHunger(proxy);
                    float afterDecay = proxy.GetTagReadonly("ANIMAL_HUNGER_TAG").GetFloat();
                    AnimalGroupFeeding.Result feeding = AnimalGroupFeeding.Feed(proxy, inventory, count);
                    float groupFood = (float)feeding.HungerGainPerAnimal;
                    foodTotal += (double)groupFood * count;
                    foreach (AnimalStackState.AnimalRecord record in group)
                    {
                        float currentHunger = AnimalStackState.Expected(record, "ANIMAL_HUNGER_TAG");
                        float loss = Math.Max(0f, before - afterDecay);
                        float unfed = Math.Max(0f, currentHunger - loss);
                        float max = record.Tag("ANIMAL_MAX_HUNGER_TAG")?.GetFloat() ?? 0f;
                        if (max <= 0f) max = 1000f;
                        float nextHunger = (float)Math.Min(max, (double)unfed + groupFood);
                        record.VirtualHungerDelta += nextHunger - currentHunger;
                        record.FoodShare += nextHunger - unfed;
                        record.HasHungerDistribution = true;
                        AnimalStackState.AccumulateHungerRate(record, feeding.HungerRate);
                        record.HungerRate = feeding.HungerRate;
                        record.UnfedHunger = unfed;
                        record.FedHunger = nextHunger;
                        record.PendingPrionIncrement = feeding.PrionIncrement;
                        record.DeferredDays++;
                    }
                }
                finally { if (proxy.parentInventory == null) proxy.Destroy(); }
            }
            if (virtualCount > 0) foodPerAnimal = (float)(foodTotal / virtualCount);
            foreach (var group in GroupVirtualPiles(hungry))
            {
                GameItem proxy = CreateProxy(item, group[0]);
                try
                {
                    float before = Average(group, "ANIMAL_HUNGER_TAG");
                    proxy.ModifyTag("ANIMAL_HUNGER_TAG", tag => tag.SetFloat(before), false);
                    HusbandryHelper.HandleHunger(proxy);
                    float loss = Math.Max(0f, before
                        - proxy.GetTagReadonly("ANIMAL_HUNGER_TAG").GetFloat());
                    foreach (AnimalStackState.AnimalRecord record in group)
                    {
                        float current = AnimalStackState.Expected(record, "ANIMAL_HUNGER_TAG");
                        float next = Math.Max(0f, current - loss);
                        record.VirtualHungerDelta += next - current;
                        record.HasHungerDistribution = true;
                        AnimalStackState.AccumulateHungerRate(record, 1d);
                        record.HungerRate = 1f;
                        record.UnfedHunger = next;
                        record.FedHunger = next;
                        record.DeferredDays++;
                    }
                }
                finally { if (proxy.parentInventory == null) proxy.Destroy(); }
            }
            foreach (AnimalStackState.AnimalRecord record in records)
                if (record.WasExtracted && !record.IsDead) FeedIndividual(item, inventory, record);
            AnimalHungerSplitter.ResolvePriorStarvation(item, records);
            AnimalWelfareCoordinator.CheckHygieneAndHealth(item, inventory, records);
            if (item.IsTag("ANIMAL_IS_DEAD_TAG")) return true;
            if (records.Count > 0 && item.parentInventory != null)
                AnimalWelfareCoordinator.CheckOvercrowding(item, inventory, records);
            if (records.Count == 0) return true;
            AnimalFertilityCoordinator.Update(item, inventory, records);
            feedable = records.FindAll(record => !record.WasExtracted && !record.IsDead
                && (!AnimalStackState.IsHungryCohort(record)
                    || AnimalStackState.IsNativeStarving(record)));
            hungry = records.FindAll(record => !record.IsDead
                && AnimalStackState.IsHungryCohort(record)
                && !AnimalStackState.IsNativeStarving(record));
            virtualCount = AnimalStackState.Count(feedable);
            growthPerAnimal = GrowVirtual(item, feedable);
            GrowVirtual(item, hungry);
            foreach (AnimalStackState.AnimalRecord record in records)
                if (record.WasExtracted && !record.IsDead) GrowIndividual(item, record);
            bool saved = AnimalHungerSplitter.SplitOrStore(item, inventory, records);
            if (saved)
            {
                long liveVirtual = 0, liveIndividual = 0, deadUnits = 0;
                foreach (AnimalStackState.AnimalRecord record in records)
                {
                    if (record.IsDead) deadUnits += record.Quantity;
                    else if (record.WasExtracted) liveIndividual += record.Quantity;
                    else liveVirtual += record.Quantity;
                }
                RatButcherPatch.Observer?.RecordAnimal("day advanced: item=" + item.uniqueId
                    + ", grouped=" + liveVirtual + ", individual=" + liveIndividual
                    + ", dead=" + deadUnits
                    + ", group_food_each=" + foodPerAnimal.ToString("G9", CultureInfo.InvariantCulture)
                    + ", group_growth_each=" + growthPerAnimal.ToString("G9", CultureInfo.InvariantCulture));
            }
            return saved;
        }

        /// <summary>Groups virtual records by their parent, age and feeding state.</summary>
        private static IEnumerable<List<AnimalStackState.AnimalRecord>> GroupVirtualPiles(
            List<AnimalStackState.AnimalRecord> records)
        {
            var groups = new Dictionary<string, List<AnimalStackState.AnimalRecord>>(StringComparer.Ordinal);
            for (int index = 0; index < records.Count; index++)
            {
                var record = records[index];
                string key = AnimalStackState.ProcessingPileKey(record, index);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new List<AnimalStackState.AnimalRecord>();
                    groups.Add(key, group);
                }
                group.Add(record);
            }
            return groups.Values;
        }
        private static float GrowVirtual(GameItem item,
            List<AnimalStackState.AnimalRecord> records)
        {
            long total = AnimalStackState.Count(records);
            if (total == 0) return 0f;
            var groups = new Dictionary<string, List<AnimalStackState.AnimalRecord>>();
            int index = 0;
            foreach (var record in records)
            {
                // Unknown legacy parent or age histories remain isolated.
                string key = AnimalStackState.ProcessingPileKey(record, index);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new List<AnimalStackState.AnimalRecord>();
                    groups.Add(key, group);
                }
                group.Add(record);
                index++;
            }
            double growthTotal = 0d;
            foreach (var group in groups.Values)
            {
                long count = AnimalStackState.Count(group);
                growthTotal += GrowOneVirtual(item, group, count) * count;
            }
            return (float)(growthTotal / total);
        }

        /// <summary>Runs one shared growth calculation and native feces event per parent-and-age pile.</summary>
        private static float GrowOneVirtual(GameItem item,
            List<AnimalStackState.AnimalRecord> records, long count)
        {
            GameItem proxy = CreateProxy(item, records[0]);
            try
            {
                proxy.ModifyTag("ANIMAL_HUNGER_TAG", tag => tag.SetFloat(
                    Average(records, "ANIMAL_HUNGER_TAG")), false);
                proxy.ModifyTag("ANIMAL_AGE_TAG", tag => tag.SetInt(
                    AnimalStackState.CurrentAge(records[0])), false);
                proxy.ModifyTag("ANIMAL_WEIGHT_TAG", tag => tag.SetFloat(
                    Average(records, "ANIMAL_WEIGHT_TAG")), false);
                float hunger = Average(records, "ANIMAL_HUNGER_TAG");
                proxy.ModifyTag(AnimalStackState.StarvingTag, tag => tag.valueEnabled = false, false);
                int normal = HusbandryHelper.GetAnimalCurrentWeightChange(proxy);
                double totalExpected = 0d;
                foreach (var record in records)
                {
                    proxy.ModifyTag("ANIMAL_HUNGER_TAG", tag => tag.SetFloat(hunger), false);
                    if (hunger < 400f)
                        proxy.EnableTag(AnimalStackState.StarvingTag, false);
                    else
                        proxy.ModifyTag("ANIMAL_HUNGER_TAG", tag => tag.SetFloat(
                            Math.Max(0f, Math.Min(399f, record.UnfedHunger))), false);
                    int alternative = HusbandryHelper.GetAnimalCurrentWeightChange(proxy);
                    proxy.ModifyTag(AnimalStackState.StarvingTag, tag => tag.valueEnabled = false, false);
                    double probability = record.HasHungerDistribution ? record.TotalHungerRate : 0d;
                    AnimalStackState.AddVirtualGrowthDistribution(record,
                        (float)normal / count, (float)alternative / count, probability);
                    totalExpected += (normal * (1d - probability) + alternative * probability)
                        * record.Quantity / count;
                }
                int fecesGrowth = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue,
                    Math.Round(totalExpected, MidpointRounding.ToEven)));
                if (item.identifier == "rat")
                    FecesProduction.TryGenerate(item, item.parentInventory, fecesGrowth,
                        Average(records, "ANIMAL_WEIGHT_TAG"));
                return (float)(totalExpected / count);
            }
            finally { if (proxy.parentInventory == null) proxy.Destroy(); }
        }
        internal static GameItem CreateProxy(GameItem source, AnimalStackState.AnimalRecord record)
        {
            GameItem proxy = NativeSplitPreparation.CreateCopy(source, 1);
            proxy.SetUnitCount(1);
            if (!AnimalStackState.Write(proxy, new List<AnimalStackState.AnimalRecord>
                { AnimalStackState.Representative(record) }))
            {
                proxy.Destroy();
                throw new InvalidOperationException("animal proxy could not be restored");
            }
            return proxy;
        }

        /// <summary>Feeds a previously extracted rat before welfare and growth checks.</summary>
        private static void FeedIndividual(GameItem source, GameInventory inventory,
            AnimalStackState.AnimalRecord record)
        {
            GameItem individual = CreateProxy(source, record);
            try
            {
                HusbandryHelper.HandleHunger(individual);
                float afterDecay = individual.GetTagReadonly("ANIMAL_HUNGER_TAG").GetFloat();
                if (inventory != null) HusbandryHelper.TryFeed(individual, inventory);
                float afterFeed = individual.GetTagReadonly("ANIMAL_HUNGER_TAG").GetFloat();
                int oldAge = individual.GetTagReadonly("ANIMAL_AGE_TAG").GetInt();
                individual.ModifyTag("ANIMAL_AGE_TAG", tag => tag.SetInt(
                    oldAge == int.MaxValue ? int.MaxValue : oldAge + 1), false);
                AnimalStackState.CaptureIndividual(individual, record);
                record.FoodShare += Math.Max(0f, afterFeed - afterDecay);
            }
            finally { if (individual.parentInventory == null) individual.Destroy(); }
        }

        private static void GrowIndividual(GameItem source, AnimalStackState.AnimalRecord record)
        {
            GameItem individual = CreateProxy(source, record);
            try
            {
                int growth = HusbandryHelper.GetAnimalCurrentWeightChange(individual);
                float oldWeight = individual.GetTagReadonly("ANIMAL_WEIGHT_TAG").GetFloat();
                float newWeight = (float)Math.Min(float.MaxValue, Math.Max(0d, (double)oldWeight + growth));
                individual.ModifyTag("ANIMAL_WEIGHT_TAG", tag => tag.SetFloat(newWeight), false);
                HusbandryHelper.TryChangeSprite(individual);
                if (source.identifier == "rat")
                    FecesProduction.TryGenerate(individual, source.parentInventory, growth, newWeight);
                AnimalStackState.CaptureIndividual(individual, record);
            }
            finally { if (individual.parentInventory == null) individual.Destroy(); }
        }

        internal static float Average(List<AnimalStackState.AnimalRecord> records, string tag)
        {
            double sum = 0d;
            foreach (AnimalStackState.AnimalRecord record in records)
                sum += AnimalStackState.Expected(record, tag) * record.Quantity;
            return (float)(sum / AnimalStackState.Count(records));
        }
    }

    /// <summary>Defers individual daily updates while a live group remains stacked.</summary>
    [HarmonyPatch(typeof(HusbandryHelper), nameof(HusbandryHelper.OnAnimalItemDayEnd))]
    internal static class AnimalGroupDayPatch
    {
        private static bool Prefix(GameItem __0, GameInventory __1)
        {
            if (AnimalStackHooks.AlreadyAdvancedToday(__0)) return false;
            try { AnimalWelfareCoordinator.BeginDayIfNeeded(__1); }
            catch (Exception error)
            {
                RatButcherPatch.Observer?.RecordAnimal("crowding day snapshot failed: " + error);
            }
            if (!ItemStackPolicy.IsAnimal(__0) || __0.unitCount < 2
                && !AnimalStackState.HasVirtualSingleton(__0)
                || __0.IsTag("ANIMAL_IS_DEAD_TAG")) return true;
            try
            {
                if (!AnimalStackHooks.AdvanceGroup(__0, __1))
                    RatButcherPatch.Observer?.RecordAnimal("day skipped: invalid animal stack state=" + __0.uniqueId);
                return false;
            }
            catch (Exception error)
            {
                RatButcherPatch.Observer?.RecordAnimal("day failed; stack state retained: " + error);
                return false;
            }
        }
    }

    /// <summary>Retains individual animal state when the game splits a native stack.</summary>
    [HarmonyPatch(typeof(GameItem), nameof(GameItem.GetSplitItem))]
    internal static class AnimalNativeSplitPatch
    {
        [ThreadStatic] internal static GameItem PreparedSplitSource;
        private static void Prefix(GameItem __instance, int __0,
            out List<AnimalStackState.AnimalRecord> __state)
        {
            __state = !ReferenceEquals(__instance, PreparedSplitSource) && ItemStackPolicy.IsAnimal(__instance)
                && AnimalStackState.TryRead(__instance, out var records) ? records : null;
        }

        private static void Postfix(GameItem __instance, int __0, GameItem __result,
            List<AnimalStackState.AnimalRecord> __state) =>
            AnimalStackState.RestoreNativeSplit(__instance, __result, __state, __0);
    }

    /// <summary>Butchering one corpse consumes one record, not the whole stack.</summary>
    [HarmonyPatch(typeof(HusbandryHelper), nameof(HusbandryHelper.CompleteButcher))]
    internal static class AnimalButcherConsumePatch
    {
        private static bool Prefix(GameItem __0)
        {
            if (!ItemStackPolicy.IsAnimal(__0) || !__0.IsTag("ANIMAL_IS_DEAD_TAG")
                || __0.unitCount < 2) return true;
            if (!AnimalStackState.ConsumeTop(__0, 1)) return true;
            PlayerStore.Instance.animalsButcheredTotal++;
            RatButcherPatch.Observer?.RecordAnimal("butcher consumed one corpse: stack=" + __0.uniqueId
                + ", remaining=" + __0.unitCount);
            return false;
        }
    }

    /// <summary>Moves one killed rat out of a live stack before native death handling.</summary>
    [HarmonyPatch(typeof(HusbandryHelper), nameof(HusbandryHelper.OnAnimalDead))]
    internal static class AnimalSingleDeathPatch
    {
        private static bool handling;

        private static bool Prefix(GameItem __0, bool __1)
        {
            if (!handling && ItemStackPolicy.IsAnimal(__0) && __0.unitCount == 1
                && AnimalStackState.HasVirtualSingleton(__0)
                && AnimalStackState.TryRead(__0, out var single))
            {
                AnimalStackState.Materialize(single[0]);
                AnimalStackState.Write(__0, single);
            }
            if (handling || !ItemStackPolicy.IsAnimal(__0) || __0.unitCount < 2
                || __0.IsTag("ANIMAL_IS_DEAD_TAG")) return true;
            GameItem individual = NativeSplitPreparation.Create(__0, 1);
            if (!AnimalStackState.CopyTop(__0, individual, 1))
            {
                individual.Destroy();
                return true;
            }
            try
            {
                handling = true;
                HusbandryHelper.OnAnimalDead(individual, __1);
            }
            finally { handling = false; }
            bool placed = false;
            GameInventory inventory = __0.parentInventory;
            if (inventory != null)
            {
                bool prior = ItemStackPolicy.SuppressStackingForSplit;
                ItemStackPolicy.SuppressStackingForSplit = true;
                try
                {
                    SlotMarker slot = inventory.TryFindOneValidInventorySlot(individual);
                    placed = slot?.targetItem == null && slot?.itemGridShape != null
                        && slot.numTransfer >= 1 && slot.TryAcceptOnce(1) == 1;
                }
                finally { ItemStackPolicy.SuppressStackingForSplit = prior; }
            }
            if (!placed && OverflowPatch.Coordinator != null)
                placed = OverflowPatch.Coordinator.RouteSplit(individual, out int consumed, out _) && consumed == 1;
            if (!placed)
            {
                if (individual.parentInventory == null) individual.Destroy();
                RatButcherPatch.Observer?.RecordAnimal("death: no corpse space; live group unchanged");
                return false;
            }
            AnimalStackState.ConsumeTop(__0, 1);
            RatButcherPatch.Observer?.RecordAnimal("death separated corpse=" + individual.uniqueId
                + " from live stack=" + __0.uniqueId + ", remaining=" + __0.unitCount);
            return false;
        }
    }

    /// <summary>Displays group expectations instead of one top animal's tooltip.</summary>
    [HarmonyPatch(typeof(HusbandryHelper), nameof(HusbandryHelper.CreateItemTooltip))]
    internal static class AnimalGroupTooltipPatch
    {
        private static bool Prefix(RichTextBuilder __0, GameItem __1)
        {
            if (!ItemStackPolicy.IsAnimal(__1) || __1.unitCount < 2
                && !AnimalStackState.HasVirtualSingleton(__1)
                || !AnimalStackState.TryRead(__1, out var records)) return true;
            __0.AddLine((__1.identifier == "rat" ? "鼠群数量：" : "动物数量：")
                + AnimalStackState.Count(records));
            __0.AddLine("平均年龄（预期）：" + AnimalStackHooks.Average(records, "ANIMAL_AGE_TAG").ToString("0.##", CultureInfo.InvariantCulture));
            __0.AddLine("平均重量（预期）：" + AnimalStackHooks.Average(records, "ANIMAL_WEIGHT_TAG").ToString("0.##", CultureInfo.InvariantCulture));
            if (!__1.IsTag("ANIMAL_IS_DEAD_TAG"))
                __0.AddLine("平均饱食（预期）：" + AnimalStackHooks.Average(records, "ANIMAL_HUNGER_TAG").ToString("0.##", CultureInfo.InvariantCulture));
            __0.AddLine("生命值（群体面板）：" + AnimalStackHooks.Average(records, "ANIMAL_HEALTH_TAG").ToString("0.##", CultureInfo.InvariantCulture));
            __0.AddLine("平均基础生长率（预期）：" + AnimalStackHooks.Average(records, "ANIMAL_GROWTH_RATE_TAG").ToString("0.##", CultureInfo.InvariantCulture));
            __0.AddLine("平均基础繁殖力（预期）：" + AnimalStackHooks.Average(records, "ANIMAL_FERTILITY_TAG").ToString("0.##", CultureInfo.InvariantCulture));
            __0.AddLine("平均当前生育率（预期）：" + AnimalFertilityCoordinator.AverageForDisplay(records).ToString("0.##", CultureInfo.InvariantCulture));
            double female = 0d, male = 0d, pregnant = 0d;
            long diseased = 0;
            double diseaseRisk = 0d;
            long virtualDiseaseUnits = 0;
            foreach (var record in records)
            {
                if (record.Choices.TryGetValue("S:ANIMAL_SEXE_TAG", out var sexes))
                {
                    if (sexes.TryGetValue("FEMALE", out double femaleRate))
                        female += femaleRate * record.Quantity;
                    if (sexes.TryGetValue("MALE", out double maleRate))
                        male += maleRate * record.Quantity;
                }
                else
                {
                    string sex = record.Tag("ANIMAL_SEXE_TAG")?.GetString();
                    if (sex == "FEMALE") female += record.Quantity;
                    else if (sex == "MALE") male += record.Quantity;
                }
                if (record.WasExtracted)
                {
                    if (record.Tag("ANIMAL_DISEASED_TAG")?.valueEnabled == true)
                        diseased += record.Quantity;
                }
                else
                {
                    virtualDiseaseUnits += record.Quantity;
                    diseaseRisk += record.DiseaseRate * record.Quantity;
                }
                if (record.Variances.TryGetValue("P:ANIMAL_PREGNANT_TAG",
                    out double pregnancyRate)) pregnant += pregnancyRate * record.Quantity;
                else if (record.Tag("ANIMAL_PREGNANT_TAG")?.valueEnabled == true)
                    pregnant += record.Quantity;
            }
            __0.AddLine("雌性／雄性（预期）："
                + female.ToString("0.##", CultureInfo.InvariantCulture) + "／"
                + male.ToString("0.##", CultureInfo.InvariantCulture));
            if (virtualDiseaseUnits > 0)
                __0.AddLine("生病率：" + (100d * diseaseRisk / virtualDiseaseUnits)
                    .ToString("0.##", CultureInfo.InvariantCulture) + "%");
            if (diseased > 0) __0.AddLine("已患病：" + diseased);
            if (pregnant > 0) __0.AddLine("怀孕（预期）："
                + pregnant.ToString("0.##", CultureInfo.InvariantCulture));
            double food = 0d;
            foreach (var record in records) food += record.FoodShare * record.Quantity;
            __0.AddLine("平均已分配食物：" + (food / AnimalStackState.Count(records))
                .ToString("0.##", CultureInfo.InvariantCulture));
            double hungry = 0d;
            double prion = 0d;
            long virtualUnits = 0;
            foreach (var record in records)
                if (!record.WasExtracted)
                {
                    virtualUnits += record.Quantity;
                    hungry += (record.HasHungerDistribution
                        || AnimalStackState.IsHungryCohort(record)
                            ? record.TotalHungerRate : 0d)
                        * record.Quantity;
                    prion += AnimalStackState.PrionProbability(record) * record.Quantity;
                }
            __0.AddLine("总饥饿率：" + (virtualUnits > 0 ? 100d * hungry / virtualUnits : 0d)
                .ToString("0.##", CultureInfo.InvariantCulture) + "%");
            __0.AddLine("朊病毒率：" + (virtualUnits > 0 ? 100d * prion / virtualUnits : 0d)
                .ToString("0.##", CultureInfo.InvariantCulture) + "%");
            return false;
        }
    }

    /// <summary>Counts every animal in a stack for the native overcrowding thresholds.</summary>
    [HarmonyPatch(typeof(HusbandryHelper), nameof(HusbandryHelper.GetAnimalOvercrowdingLevel))]
    internal static class AnimalCrowdingPatch
    {
        private static void Postfix(GameInventory __0, ref int __result)
        {
            if (__0 is not GameGridInventory grid) return;
            double area = (double)grid.inventoryShape.width * grid.inventoryShape.height;
            if (area <= 0) return;
            double occupied = 0;
            bool hasStack = false;
            foreach (GameItem item in __0.childItems)
            {
                if (item == null || !item.IsTag("ANIMAL_ITEM_TAG")) continue;
                occupied += (double)item.GetItemSize() * Math.Max(1, item.unitCount);
                if (item.unitCount > 1) hasStack = true;
            }
            if (!hasStack) return;
            double ratio = (double)occupied / area;
            __result = ratio > 0.9d ? 3 : ratio > 0.7d ? 2 : ratio > 0.55d ? 1 : 0;
        }
    }

    /// <summary>Stores the parent's identities with each pregnancy and newborn litter.</summary>
    [HarmonyPatch(typeof(HusbandryHelper), nameof(HusbandryHelper.OnPregnant))]
    internal static class AnimalParentPatch
    {
        private static void Postfix(GameItem __0, GameItem __1)
        {
            if (!ItemStackPolicy.IsAnimal(__0) || !ItemStackPolicy.IsAnimal(__1)) return;
            string group = __0.uniqueId + ":" + __1.uniqueId + ":" + Guid.NewGuid().ToString("N");
            __0.ModifyTag(AnimalStackState.PregnancyLineageTag, tag => { tag.Enable(); tag.SetString(group); }, false);
            RatButcherPatch.Observer?.RecordAnimal("pregnancy group created: mother=" + __0.uniqueId
                + ", father=" + __1.uniqueId + ", group=" + group);
        }
    }

    [HarmonyPatch(typeof(HusbandryHelper), nameof(HusbandryHelper.GiveBirth))]
    internal static class AnimalBirthContextPatch
    {
        internal static string CurrentGroup;

        private static void Prefix(GameItem __0, out string __state)
        {
            __state = CurrentGroup;
            CurrentGroup = __0?.GetTagReadonly(AnimalStackState.PregnancyLineageTag).GetString();
            if (__0 != null && string.IsNullOrEmpty(CurrentGroup))
            {
                CurrentGroup = __0.uniqueId + ":unknown:" + Guid.NewGuid().ToString("N");
                string group = CurrentGroup;
                __0.ModifyTag(AnimalStackState.PregnancyLineageTag, tag => { tag.Enable(); tag.SetString(group); }, false);
            }
        }

        private static Exception Finalizer(Exception __exception, string __state)
        {
            CurrentGroup = __state;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ItemSpawner), nameof(ItemSpawner.Spawn))]
    internal static class AnimalNewbornTagPatch
    {
        private static void Postfix(string __0, GameItem __result)
        {
            string group = AnimalBirthContextPatch.CurrentGroup;
            if (__0 != "rat" || __result == null || string.IsNullOrEmpty(group)) return;
            __result.ModifyTag(AnimalStackState.LineageTag, tag => { tag.Enable(); tag.SetString(group); }, false);
            RatButcherPatch.Observer?.RecordAnimal("newborn rat=" + __result.uniqueId + ", parent group=" + group);
        }
    }

    /// <summary>Scopes native feeding so one food unit is consumed from a meat stack.</summary>
    [HarmonyPatch(typeof(HusbandryHelper), nameof(HusbandryHelper.TryFeed))]
    internal static class AnimalFeedContextPatch
    {
        internal static int Depth;

        private static void Prefix() => Depth++;

        private static Exception Finalizer(Exception __exception)
        {
            Depth--;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(GameItem), nameof(GameItem.Destroy))]
    internal static class AnimalFeedConsumePatch
    {
        private static bool Prefix(GameItem __instance)
        {
            if (AnimalFeedContextPatch.Depth < 1 || !ItemStackPolicy.IsMeat(__instance)
                || __instance.unitCount < 2 || !__instance.IsTag("CALORIE_VALUE_TAG")) return true;
            __instance.SetUnitCount(__instance.unitCount - 1);
            RatButcherPatch.Observer?.RecordSplit("native feed consumed one meat unit: " + __instance.identifier);
            return false;
        }
    }
}
