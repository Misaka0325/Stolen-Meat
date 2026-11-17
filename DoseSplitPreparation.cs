using System;

namespace StolenMeat
{
    /// <summary>Prepares the statistics for resolving one rat's drug allocation.</summary>
    internal static class DoseSplitPreparation
    {
        internal sealed class Statistics
        {
            internal decimal TotalDoses;
            internal long NormalMinimum;
            internal double Mean;
            internal double StandardDeviation;
            internal double DrawPercent;
        }

        internal static long NormalApproximationMinimum(int animalCount)
        {
            if (animalCount < 2) throw new ArgumentOutOfRangeException(nameof(animalCount));
            decimal delta = (decimal)animalCount - 2m;
            decimal numerator = 7917m * delta * delta;
            decimal denominator = 50m * ((decimal)animalCount - 1m);
            return checked((long)decimal.Ceiling(numerator / denominator));
        }

        /// <summary>Draws one percentile and computes binomial statistics for all doses.</summary>
        internal static Statistics Prepare(int animalCount, long active, long pending, Random random)
        {
            if (animalCount < 2 || active < 0 || active > animalCount || pending < 0 || random == null)
                throw new ArgumentOutOfRangeException();
            decimal total = (decimal)active + pending;
            double probability = 1d / animalCount;
            double mean = (double)total * probability;
            return new Statistics {
                TotalDoses = total,
                NormalMinimum = NormalApproximationMinimum(animalCount),
                Mean = mean,
                StandardDeviation = Math.Sqrt(mean * (1d - probability)),
                DrawPercent = random.NextDouble() * 100d
            };
        }

        /// <summary>Selects the approximation only at or above the specified threshold.</summary>
        internal static bool UsesNormalApproximation(Statistics statistics, int animalCount)
        {
            if (statistics == null || animalCount < 2) throw new ArgumentOutOfRangeException();
            return animalCount > 2 && statistics.TotalDoses >= statistics.NormalMinimum;
        }

        /// <summary>Uses the shared binomial sampler with the dose-specific probability.</summary>
        internal static double BinomialInnerProbability(Statistics statistics, int animalCount)
        {
            if (statistics == null || animalCount < 3 || UsesNormalApproximation(statistics, animalCount))
                throw new ArgumentOutOfRangeException();
            return BinomialSampler.InnerProbability(checked((long)statistics.TotalDoses),
                1d / animalCount, statistics.Mean, statistics.StandardDeviation);
        }

        internal static long ResolveBinomial(Statistics statistics, int animalCount)
        {
            if (statistics == null || animalCount < 3 || UsesNormalApproximation(statistics, animalCount))
                throw new ArgumentOutOfRangeException();
            return BinomialSampler.DrawExact(checked((long)statistics.TotalDoses),
                1d / animalCount, statistics.Mean, statistics.StandardDeviation, statistics.DrawPercent);
        }

        internal static long ResolveNormal(Statistics statistics, int animalCount, Random random)
        {
            if (statistics == null || animalCount < 3 || !UsesNormalApproximation(statistics, animalCount))
                throw new ArgumentOutOfRangeException();
            return BinomialSampler.DrawNormal(statistics.TotalDoses,
                statistics.TotalDoses / animalCount, statistics.StandardDeviation, statistics.DrawPercent, random);
        }
        /// <summary>Resolves the two-rat special case from the already drawn percentile.</summary>
        internal static long TwoRatDoseCount(Statistics statistics)
        {
            if (statistics == null || statistics.TotalDoses < 0m)
                throw new ArgumentOutOfRangeException(nameof(statistics));
            decimal half = decimal.Floor(statistics.TotalDoses / 2m);
            if (statistics.TotalDoses % 2m != 0m && statistics.DrawPercent < 50d) half++;
            return checked((long)half);
        }
    }
}