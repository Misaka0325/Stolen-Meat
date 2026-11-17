using System;

namespace StolenMeat
{
    /// <summary>Holds the per-pile fraction for each nonzero mating count.</summary>
    internal readonly struct AnimalMatingRatios
    {
        internal readonly double Once, Twice, ThreeTimes;
        internal AnimalMatingRatios(double once, double twice, double threeTimes)
        {
            if (!double.IsFinite(once) || !double.IsFinite(twice) || !double.IsFinite(threeTimes)
                || once < 0d || twice < 0d || threeTimes < 0d || once + twice + threeTimes > 1d)
                throw new ArgumentOutOfRangeException();
            Once = once; Twice = twice; ThreeTimes = threeTimes;
        }
    }

    /// <summary>Counts distinct females by their number of matings on one night.</summary>
    internal readonly struct AnimalMatingDistribution
    {
        internal readonly int Once, Twice, ThreeTimes, Unpaired;
        internal readonly AnimalMatingRatios Ratios;
        internal int MatedFemales => checked(Once + Twice + ThreeTimes);
        internal long TotalPairings => (long)Once + 2L * Twice + 3L * ThreeTimes;

        internal AnimalMatingDistribution(int females, int once, int twice, int threeTimes,
            AnimalMatingRatios ratios)
        {
            if (females < 0 || once < 0 || twice < 0 || threeTimes < 0
                || (long)once + twice + threeTimes > females) throw new ArgumentOutOfRangeException();
            Once = once; Twice = twice; ThreeTimes = threeTimes;
            Unpaired = females - checked(once + twice + threeTimes); Ratios = ratios;
        }
    }

    /// <summary>Computes a nightly mating distribution from one virtual pile's population.</summary>
    /// <remarks>The production algorithm remains unspecified.</remarks>
    internal interface IAnimalMatingCalculator
    {
        AnimalMatingDistribution CalculateTonight(AnimalPopulationCounts population);
    }
}