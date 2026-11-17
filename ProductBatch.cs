using System;
using System.Collections.Generic;

namespace StolenMeat
{
    /// <summary>An immutable snapshot of one native production result. Can also represent future birth output.</summary>
    internal sealed class ProductBatch
    {
        internal readonly string Origin;
        internal readonly string SourceId;
        internal readonly int SourceUniqueId;
        internal readonly DateTimeOffset OccurredAtUtc;
        internal readonly int Total;
        internal readonly string Items;
        internal readonly string Counts;

        private ProductBatch(string origin, string sourceId, int sourceUniqueId, DateTimeOffset time, int total, string items, string counts)
        {
            Origin = origin;
            SourceId = sourceId;
            SourceUniqueId = sourceUniqueId;
            OccurredAtUtc = time;
            Total = total;
            Items = items;
            Counts = counts;
        }

        /// <summary>Copies generated item identities and unit counts into a log snapshot.</summary>
        internal static ProductBatch Capture(string origin, GameItem source, IEnumerable<GameItem> products)
        {
            var identities = new List<string>();
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int total = 0;
            if (products != null)
            {
                foreach (GameItem item in products)
                {
                    string id = item == null ? "<null>" : Safe(item.identifier);
                    int uniqueId = item == null ? -1 : item.uniqueId;
                    int units = item == null ? 0 : item.unitCount;
                    identities.Add(id + "#" + uniqueId + " x" + units);
                    counts[id] = counts.TryGetValue(id, out int current) ? current + units : units;
                    total += units;
                }
            }
            var summary = new List<string>();
            foreach (var pair in counts) summary.Add(pair.Key + " x" + pair.Value);
            return new ProductBatch(origin, source == null ? "<null>" : Safe(source.identifier),
                source == null ? -1 : source.uniqueId, DateTimeOffset.UtcNow, total,
                string.Join(", ", identities), string.Join(", ", summary));
        }

        internal static string Safe(string value) => string.IsNullOrEmpty(value) ? "<none>" : value;
    }
}


