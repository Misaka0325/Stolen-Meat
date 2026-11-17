using System;

namespace StolenMeat
{
    /// <summary>Draws bounded binomial counts independently of their game meaning.</summary>
    internal static class BinomialSampler
    {
        private static double LogFailure(double p) => p < 0.0001d
            ? -p * (1d + p * (0.5d + p * (1d / 3d + p * 0.25d)))
            : Math.Log(1d - p);

        internal static double InnerProbability(long trials, double p, double mean, double sd)
        {
            long lower = Math.Max(0L, (long)Math.Ceiling(mean - sd));
            long upper = Math.Min(trials, (long)Math.Floor(mean + sd));
            if (lower > upper) return 0d;
            double mass = Math.Exp(trials * LogFailure(p));
            double result = 0d;
            for (long k = 0; k <= upper; k++)
            {
                if (k >= lower) result += mass;
                if (k < upper) mass *= ((double)(trials - k) / (k + 1d)) * (p / (1d - p));
            }
            return Math.Max(0d, Math.Min(1d, result));
        }

        /// <summary>Draws the one-standard-deviation ring first, then ascending outer values.</summary>
        internal static long DrawExact(long trials, double p, double mean, double sd, double drawPercent)
        {
            if (trials < 0 || !double.IsFinite(p) || p <= 0d || p >= 1d
                || !double.IsFinite(drawPercent) || drawPercent < 0d || drawPercent >= 100d)
                throw new ArgumentOutOfRangeException();
            long lower = Math.Max(0L, (long)Math.Ceiling(mean - sd));
            long upper = Math.Min(trials, (long)Math.Floor(mean + sd));
            long limit = Math.Min(trials, Math.Max(upper,
                (long)Math.Ceiling(mean + 40d * sd + 100d)));
            var masses = new double[checked((int)limit + 1)];
            masses[0] = Math.Exp(trials * LogFailure(p));
            for (int k = 0; k < limit; k++)
                masses[k + 1] = masses[k] * ((double)(trials - k) / (k + 1d)) * (p / (1d - p));
            double inner = 0d;
            for (long k = lower; k <= upper; k++) inner += masses[(int)k];
            if (drawPercent == inner * 100d)
                return Math.Max(0L, Math.Min(trials, (long)Math.Floor(mean + sd)));
            double draw = drawPercent / 100d;
            bool inside = draw < inner;
            double target = inside ? draw : draw - inner;
            double accumulated = 0d;
            long lastRepresentable = 0;
            for (long k = 0; k <= limit; k++)
            {
                if ((k >= lower && k <= upper) != inside) continue;
                double next = accumulated + masses[(int)k];
                if (next > accumulated) lastRepresentable = k;
                accumulated = next;
                if (target < accumulated) return k;
            }
            return lastRepresentable;
        }

        /// <summary>Uses the established bounded Box-Muller approximation and floor rule.</summary>
        internal static long DrawNormal(decimal trials, decimal mean, double sd,
            double drawPercent, Random random)
        {
            if (random == null || !double.IsFinite(sd) || sd <= 0d
                || !double.IsFinite(drawPercent) || drawPercent < 0d || drawPercent >= 100d)
                throw new ArgumentOutOfRangeException();
            double first = drawPercent / 100d;
            for (int attempt = 0; attempt < 128; attempt++)
            {
                double radial = 1d - (attempt == 0 ? first : random.NextDouble());
                double angle = 2d * Math.PI * random.NextDouble();
                double standard = Math.Sqrt(-2d * Math.Log(radial)) * Math.Cos(angle);
                decimal value = mean + (decimal)(sd * standard);
                if (value < 0m || value > trials) continue;
                value = decimal.Floor(value);
                return value >= long.MaxValue ? long.MaxValue : (long)value;
            }
            throw new InvalidOperationException("Normal sampling failed to reach the permitted range.");
        }

        /// <summary>Draws a count for independent events with a caller-supplied probability.</summary>
        internal static int DrawCount(int trials, double probability, Random random)
        {
            if (trials < 0 || random == null || !double.IsFinite(probability)
                || probability < 0d || probability > 1d)
                throw new ArgumentOutOfRangeException();
            if (trials == 0 || probability == 0d) return 0;
            if (probability == 1d) return trials;
            // Sampling the rarer outcome avoids underflow and oversized exact tables.
            bool complement = probability > 0.5d;
            double p = complement ? 1d - probability : probability;
            double q = 1d - p;
            double mean = trials * p;
            double sd = Math.Sqrt(mean * q);
            double normalMinimum = 158.34d * (1d - 2d * p) * (1d - 2d * p) / (p * q);
            // Zero skew at p=0.5 is not by itself sufficient for tiny samples.
            bool approximate = trials >= 128 && trials >= normalMinimum;
            double draw = random.NextDouble() * 100d;
            if (approximate)
                return checked((int)DrawNormal(trials,
                    (decimal)trials * (decimal)probability, sd, draw, random));
            long value = DrawExact(trials, p, mean, sd, draw);
            return checked((int)(complement ? trials - value : value));
        }
    }
}