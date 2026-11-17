using System;
using System.Collections.Generic;

namespace StolenMeat
{
    /// <summary>Owns native hook subscriptions and writes structured observations through ModLog.</summary>
    internal sealed class LootObserver
    {
        private readonly ModLog log;
        private bool enabled;
        private int rollCount;
        private int cutCount;

        internal LootObserver(ModLog log) { this.log = log; }

        /// <summary>Subscribes once to game events that expose item activity.</summary>
        internal void Enable()
        {
            if (enabled) return;
            ModHook.OnGameLoadedNormal += OnGameLoaded;
            ModHook.OnLootRolled += OnLootRolled;
            enabled = true;
            Write("hook subscribed: OnGameLoadedNormal, OnLootRolled; Harmony observers: OnButcher, CreateButcherCuts");
        }

        /// <summary>Unsubscribes from game events and reports final counts.</summary>
        internal void Disable()
        {
            if (!enabled) return;
            ModHook.OnGameLoadedNormal -= OnGameLoaded;
            ModHook.OnLootRolled -= OnLootRolled;
            Write($"hook unsubscribed: loot rolls={rollCount}, rat butcher batches={cutCount}");
            enabled = false;
        }

        private void OnGameLoaded()
        {
            rollCount = 0;
            cutCount = 0;
            Write("event OnGameLoadedNormal: counters reset");
        }

        private void OnLootRolled(LootRollContext context)
        {
            if (!enabled || context == null) return;
            rollCount++;
            Write($"event OnLootRolled #{rollCount}: table={ProductBatch.Safe(context.tableId)}, group={ProductBatch.Safe(context.groupId)}, item={ProductBatch.Safe(context.itemId)}; mod change=none");
        }

        internal void RecordButcherEntered(GameItem source)
        {
            if (!enabled) return;
            Write($"intercept OnButcher entered: source={ProductBatch.Safe(source == null ? null : source.identifier)}#{(source == null ? -1 : source.uniqueId)}; mod change=none");
        }

        internal void RecordButcherReturned()
        {
            if (enabled) Write("event OnButcher returned: original method completed; placement result not independently verified; mod change=none");
        }

        /// <summary>Logs the native cut list as item units, including stack sizes.</summary>
        internal void RecordButcherCuts(GameItem source, List<GameItem> cuts)
        {
            if (!enabled || cuts == null) return;
            cutCount++;
            ProductBatch batch = ProductBatch.Capture("HusbandryHelper.CreateButcherCuts", source, cuts);
            Write($"intercept {batch.Origin} succeeded #{cutCount}: source={batch.SourceId}#{batch.SourceUniqueId}; data captured at={batch.OccurredAtUtc:O}; produced={batch.Total}; counts=[{batch.Counts}]; products=[{batch.Items}]; change=see interception record");
        }

        internal void RecordStackInterception(GameItem source, int originalRawCount, int physicalObjects)
        {
            if (enabled) Write($"intercept CreateButcherCuts: source={ProductBatch.Safe(source == null ? null : source.identifier)}#{(source == null ? -1 : source.uniqueId)}; original raw_meat objects={originalRawCount}; resulting raw_meat stacks={(originalRawCount > 0 ? 1 : 0)}; returned objects={physicalObjects}; change=raw_meat packed before placement");
        }

        internal void RecordUnsafeWeight(GameItem source, float rawWeight)
        {
            if (enabled) Write($"blocked CreateButcherCuts: source={ProductBatch.Safe(source == null ? null : source.identifier)}#{(source == null ? -1 : source.uniqueId)}; weight={rawWeight}; reason=outside safe Int32 conversion range; products=0");
        }
        internal void RecordStackFailure(Exception error)
        {
            if (enabled) log.Error($"[{DateTimeOffset.UtcNow:O}] CreateButcherCuts stack interception failed; original path retained: {error}");
        }

        internal void RecordSplit(string detail)
        {
            if (enabled) Write("split: " + detail);
        }

        internal void RecordMerge(GameItem source, GameItem target, int amount)
        {
            if (enabled && amount > 0)
                Write($"item stack merge: source={source.identifier}#{source.uniqueId}, target={target.identifier}#{target.uniqueId}, moved={amount}, target total={target.unitCount}");
        }
        internal void RecordAnimal(string detail)
        {
            if (enabled) Write("animal stack: " + detail);
        }
        internal void RecordOverflow(string reason, string itemId, long amount, string outcome)
        {
            if (enabled) Write($"overflow: reason={reason}, item={itemId}, units={amount}, outcome={outcome}");
        }

        private void Write(string message) => log.Log($"[{DateTimeOffset.UtcNow:O}] {message}");
    }
}







