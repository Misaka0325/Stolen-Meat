namespace StolenMeat
{
    /// <summary>Chooses a reusable container-wide animal calculation mode.</summary>
    internal static class AnimalPopulationPolicy
    {
        internal const long BalancedMinimum = 32;
        internal const long ForcedUnifiedAbove = 128;

        internal static bool IsVirtual(GameItem item, AnimalStackState.AnimalRecord record) =>
            item != null && record != null && !record.WasExtracted
                && (item.unitCount > 1 || record.HasHungerDistribution
                    || AnimalStackState.IsHungryCohort(record));

        /// <summary>Uses one calculation above 128, or from 32 with a virtual majority.</summary>
        internal static bool UseUnified(long virtualCount, long realCount)
        {
            if (virtualCount < 0 || realCount < 0) return false;
            long total = virtualCount > long.MaxValue - realCount
                ? long.MaxValue : virtualCount + realCount;
            return total > ForcedUnifiedAbove
                || total >= BalancedMinimum && virtualCount >= realCount;
        }
    }
}
