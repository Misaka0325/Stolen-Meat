using System;
using System.Collections.Generic;

namespace StolenMeat
{
    /// <summary>Indexes one cell's current virtual piles and saved individual animals.</summary>
    internal sealed class AnimalCellIndex
    {
        private readonly GameItem cell;
        private readonly string payload;
        private readonly int count;
        private readonly int cellId;
        private readonly GameInventory parent;
        private readonly Dictionary<string, TagState> savedTags = new Dictionary<string, TagState>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<AnimalStackState.AnimalRecord>> piles =
            new Dictionary<string, List<AnimalStackState.AnimalRecord>>(StringComparer.Ordinal);
        private readonly Dictionary<string, AnimalStackState.AnimalRecord> individuals =
            new Dictionary<string, AnimalStackState.AnimalRecord>(StringComparer.Ordinal);
        internal IReadOnlyList<AnimalStackState.AnimalRecord> Records { get; }
        internal int CellId => cellId;
        internal int PileCount => piles.Count;
        internal int IndividualCount => individuals.Count;
        internal int TotalAnimals => count;

        private AnimalCellIndex(GameItem source, List<AnimalStackState.AnimalRecord> records)
        {
            cell = source;
            payload = source.GetTagReadonly(AnimalStackState.PayloadTag).GetString() ?? string.Empty;
            count = source.unitCount;
            cellId = source.uniqueId;
            parent = source.parentInventory;
            foreach (var pair in source.modifiedState.dict) savedTags.Add(pair.Key, pair.Value?.Clone());
            Records = records.AsReadOnly();
            for (int index = 0; index < records.Count; index++)
            {
                var record = records[index];
                if (!AnimalPopulationPolicy.IsVirtual(source, record))
                {
                    string identity = record.Tag(AnimalStackState.IdentityTag)?.GetString();
                    if (string.IsNullOrEmpty(identity)) identity = "legacy:" + index;
                    if (!individuals.TryAdd(identity, record))
                        throw new InvalidOperationException("Duplicate individual identities in a cell.");
                    continue;
                }
                string key = string.IsNullOrEmpty(record.Tag(AnimalStackState.LineageTag)?.GetString())
                    ? "unknown:" + index : AnimalStackState.PileKey(record, index);
                if (!piles.TryGetValue(key, out var group))
                    piles.Add(key, group = new List<AnimalStackState.AnimalRecord>());
                group.Add(record);
            }
        }

        internal static bool TryCreate(GameItem source, out AnimalCellIndex index)
        {
            index = null;
            if (source == null || !ItemStackPolicy.IsAnimal(source)
                || !AnimalStackState.TryRead(source, out var records)) return false;
            try { index = new AnimalCellIndex(source, records); return true; }
            catch (InvalidOperationException) { return false; }
        }

        /// <summary>Stale snapshots must not be reused after source quantity or saved data changes.</summary>
        internal bool IsCurrent
        {
            get
            {
                if (cell == null || cell.uniqueId != cellId || cell.unitCount != count
                    || !ReferenceEquals(parent, cell.parentInventory)
                    || !string.Equals(payload, cell.GetTagReadonly(AnimalStackState.PayloadTag).GetString() ?? string.Empty,
                        StringComparison.Ordinal) || cell.modifiedState.dict.Count != savedTags.Count) return false;
                foreach (var pair in savedTags)
                {
                    if (!cell.modifiedState.dict.TryGetValue(pair.Key, out var current)) return false;
                    var old = pair.Value;
                    if (old == null || current == null) { if (!ReferenceEquals(old, current)) return false; continue; }
                    if (old.valueEnabled != current.valueEnabled || old.valueInt != current.valueInt
                        || old.valueLong != current.valueLong || old.valueBool != current.valueBool
                        || !old.valueFloat.Equals(current.valueFloat) || !old.valueDouble.Equals(current.valueDouble)
                        || !string.Equals(old.valueString, current.valueString, StringComparison.Ordinal)) return false;
                }
                return true;
            }
        }
        internal bool TryGetPile(string id, out List<AnimalStackState.AnimalRecord> records) =>
            IsCurrent && piles.TryGetValue(id, out records) || ReturnNoPile(out records);
        internal bool TryGetIndividual(string id, out AnimalStackState.AnimalRecord record) =>
            IsCurrent && individuals.TryGetValue(id, out record) || ReturnNoAnimal(out record);
        private static bool ReturnNoPile(out List<AnimalStackState.AnimalRecord> records)
        { records = null; return false; }
        private static bool ReturnNoAnimal(out AnimalStackState.AnimalRecord record)
        { record = null; return false; }
    }
}