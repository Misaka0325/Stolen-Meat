using System;

namespace StolenMeat
{
    /// <summary>Expected pregnancies and offspring for one virtual pile and one night.</summary>
    internal readonly struct AnimalBirthExpectation
    {
        internal readonly double OnceChance, TwiceChance, ThreeTimesChance;
        internal readonly double Pregnancies, Offspring;

        internal AnimalBirthExpectation(double onceChance, double twiceChance, double threeTimesChance,
            double pregnancies, double offspring)
        {
            OnceChance = onceChance; TwiceChance = twiceChance; ThreeTimesChance = threeTimesChance;
            Pregnancies = pregnancies; Offspring = offspring;
        }
    }

    /// <summary>Calculates expectations without assigning individual pregnancies or creating animals.</summary>
    internal static class AnimalBirthCalculator
    {
        /// <summary>Uses the effective native per-mating fertility and the callers' average litter size.</summary>
        internal static AnimalBirthExpectation Calculate(AnimalMatingDistribution distribution,
            double effectiveFertilityPercent, double averageLitterSize)
        {
            if (!double.IsFinite(effectiveFertilityPercent) || !double.IsFinite(averageLitterSize)
                || averageLitterSize < 0d) throw new ArgumentOutOfRangeException();
            // Native TryMate rolls an integer percent after choosing the two parents' lower fertility.
            int percent = (int)Math.Max(0d, Math.Min(100d, effectiveFertilityPercent));
            double p = percent / 100d;
            double failure = 1d - p;
            double once = p;
            double twice = p * (1d + failure);
            double three = p * (1d + failure + failure * failure);
            double pregnancies = distribution.Once * once + distribution.Twice * twice
                + distribution.ThreeTimes * three;
            double offspring = pregnancies * averageLitterSize;
            if (!double.IsFinite(offspring)) throw new OverflowException("Expected offspring count is not finite.");
            return new AnimalBirthExpectation(once, twice, three, pregnancies, offspring);
        }
    }
}