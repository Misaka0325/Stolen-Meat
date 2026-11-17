using System;
using System.Collections.Generic;

namespace StolenMeat
{
    /// <summary>Pages through virtual piles and already-materialized individual animals.</summary>
    internal sealed class AnimalInspectionNavigation
    {
        internal enum ViewMode { Piles, Individuals }
        private readonly List<List<AnimalStackState.AnimalRecord>> piles =
            new List<List<AnimalStackState.AnimalRecord>>();
        private readonly List<AnimalStackState.AnimalRecord> individuals =
            new List<AnimalStackState.AnimalRecord>();
        internal int PileCount => piles.Count;
        internal int IndividualCount { get; private set; }
        internal int Index { get; private set; }
        internal ViewMode Mode { get; private set; }
        internal int PageCount => Mode == ViewMode.Piles ? PileCount : IndividualCount;
        internal bool CanPrevious => Index > 0 && PageCount > 0;
        internal bool CanNext => PageCount > 0 && Index < PageCount - 1;

        internal AnimalInspectionNavigation(IReadOnlyList<AnimalStackState.AnimalRecord> records, bool nativeSingletonIsReal = false)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            var byIdentity = new Dictionary<string, List<AnimalStackState.AnimalRecord>>(StringComparer.Ordinal);
            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record == null || record.Quantity < 1) throw new ArgumentException("Invalid animal record.");
                if (record.WasExtracted || nativeSingletonIsReal && records.Count == 1
                    && record.Quantity == 1 && !AnimalStackState.KeepVirtualSingleton(record))
                {
                    individuals.Add(record);
                    IndividualCount = checked(IndividualCount + record.Quantity);
                    continue;
                }
                string key = string.IsNullOrEmpty(record.Tag(AnimalStackState.LineageTag)?.GetString())
                    ? "unknown:" + i : AnimalStackState.PileKey(record, i);
                if (!byIdentity.TryGetValue(key, out var pile))
                {
                    pile = new List<AnimalStackState.AnimalRecord>();
                    byIdentity.Add(key, pile);
                    piles.Add(pile);
                }
                pile.Add(record);
            }
            Mode = piles.Count > 0 ? ViewMode.Piles : ViewMode.Individuals;
        }

        internal void Select(ViewMode mode)
        {
            if (!Enum.IsDefined(typeof(ViewMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            Mode = mode;
            Index = 0;
        }

        internal void Previous() { if (CanPrevious) Index--; }
        internal void Next() { if (CanNext) Index++; }
        internal IReadOnlyList<AnimalStackState.AnimalRecord> CurrentPile =>
            Mode == ViewMode.Piles && PageCount > 0 ? piles[Index].AsReadOnly() : null;
        internal AnimalStackState.AnimalRecord CurrentIndividual
        {
            get
            {
                if (Mode != ViewMode.Individuals || PageCount == 0) return null;
                int position = Index;
                foreach (var record in individuals)
                {
                    if (position < record.Quantity) return record;
                    position -= record.Quantity;
                }
                return null;
            }
        }
    }
}