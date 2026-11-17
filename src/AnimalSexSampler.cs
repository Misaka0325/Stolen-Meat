using System;

namespace StolenMeat
{
    /// <summary>Draws female counts only for animals whose sex is not already known.</summary>
    internal static class AnimalSexSampler
    {
        internal const double NativeFemaleProbability = 0.5d;

        internal static int DrawFemaleCount(int unknownAnimals, Random random) =>
            BinomialSampler.DrawCount(unknownAnimals, NativeFemaleProbability, random);
    }
}