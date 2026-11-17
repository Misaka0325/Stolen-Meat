using System.Collections.Generic;

namespace StolenMeat
{
    /// <summary>Data for one virtual pile with one parent identity and current age.</summary>
    internal sealed class AnimalPilePanel
    {
        internal string Identity { get; }
        internal int Age { get; }
        internal IReadOnlyList<AnimalStackState.AnimalRecord> VirtualRecords { get; }
        internal AnimalPopulationCounts Population { get; }

        internal AnimalPilePanel(string identity, int age,
            IReadOnlyList<AnimalStackState.AnimalRecord> records, AnimalPopulationCounts population)
        {
            Identity = identity;
            Age = age;
            VirtualRecords = records;
            Population = population;
        }
    }
}