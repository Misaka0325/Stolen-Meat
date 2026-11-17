using System;
using System.Collections.Generic;

namespace StolenMeat
{
    /// <summary>Builds a panel only from one valid virtual pile.</summary>
    internal static class AnimalPilePanelFactory
    {
        internal static AnimalPilePanel Create(IReadOnlyList<AnimalStackState.AnimalRecord> records,
            AnimalPopulationCounts population)
        {
            if (records == null || records.Count == 0 || population == null)
                throw new ArgumentException("A pile requires records and population counts.");
            if (records[0] == null || string.IsNullOrEmpty(records[0].Tag(AnimalStackState.LineageTag)?.GetString())
                || !AnimalStackState.TryGetPileIdentity(records[0], out string identity))
                throw new ArgumentException("The pile identity is unknown.");
            var copied = new List<AnimalStackState.AnimalRecord>(records.Count);
            var seen = new HashSet<AnimalStackState.AnimalRecord>();
            long quantity = 0;
            foreach (var record in records)
            {
                if (record == null || record.WasExtracted || record.IsDead || record.Quantity < 1
                    || string.IsNullOrEmpty(record.Tag(AnimalStackState.LineageTag)?.GetString())
                    || !seen.Add(record)
                    || !AnimalStackState.TryGetPileIdentity(record, out string current)
                    || !string.Equals(identity, current, StringComparison.Ordinal))
                    throw new ArgumentException("A panel may contain one live virtual pile only.");
                quantity += record.Quantity;
                copied.Add(record);
            }
            if (quantity != population.Total)
                throw new ArgumentException("Population counts do not match the virtual pile.");
            return new AnimalPilePanel(identity, AnimalStackState.CurrentAge(records[0]),
                copied.AsReadOnly(), population);
        }
    }
}