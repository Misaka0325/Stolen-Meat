using System;

namespace StolenMeat
{
    /// <summary>Evaluates normal tails and quantiles without an external statistics library.</summary>
    internal static class NormalDistributionMath
    {
        private const int Order = 64;
        private const double DensityScale = 0.39894228040143267794d;
        private static readonly double[] Nodes = new double[Order / 2];
        private static readonly double[] Weights = new double[Order / 2];

        static NormalDistributionMath()
        {
            for (int i = 0; i < Nodes.Length; i++)
            {
                double root = Math.Cos(Math.PI * (i + 0.75d) / (Order + 0.5d));
                double derivative = 0d;
                for (int iteration = 0; iteration < 20; iteration++)
                {
                    double previous = 1d, current = root;
                    for (int degree = 2; degree <= Order; degree++)
                    {
                        double next = ((2d * degree - 1d) * root * current
                            - (degree - 1d) * previous) / degree;
                        previous = current;
                        current = next;
                    }
                    derivative = Order * (root * current - previous) / (root * root - 1d);
                    double adjustment = current / derivative;
                    root -= adjustment;
                    if (Math.Abs(adjustment) < 1e-16d) break;
                }
                Nodes[i] = root;
                Weights[i] = 2d / ((1d - root * root) * derivative * derivative);
            }
        }

        /// <summary>Computes P(Z &gt; x) for nonnegative x using Gaussian quadrature.</summary>
        internal static double UpperTail(double x)
        {
            if (double.IsNaN(x) || x < 0d) throw new ArgumentOutOfRangeException(nameof(x));
            if (x >= 12d) return 0d;
            double midpoint = (12d + x) / 2d;
            double halfWidth = (12d - x) / 2d;
            double sum = 0d;
            for (int i = 0; i < Nodes.Length; i++)
            {
                double displacement = halfWidth * Nodes[i];
                double left = midpoint - displacement, right = midpoint + displacement;
                sum += Weights[i] * (Math.Exp(-0.5d * left * left)
                    + Math.Exp(-0.5d * right * right));
            }
            return sum * halfWidth * DensityScale;
        }

        /// <summary>Inverts a standard-normal upper-tail probability at most one half.</summary>
        internal static double PositiveQuantileFromTail(double tail)
        {
            if (double.IsNaN(tail) || tail < 0d || tail > 0.5d)
                throw new ArgumentOutOfRangeException(nameof(tail));
            if (tail == 0d) return double.PositiveInfinity;
            if (tail == 0.5d) return 0d;
            double lower = 0d, upper = 12d;
            for (int i = 0; i < 56; i++)
            {
                double mid = (lower + upper) / 2d;
                if (UpperTail(mid) > tail) lower = mid;
                else upper = mid;
            }
            return (lower + upper) / 2d;
        }
        /// <summary>Returns the standard-normal inverse CDF with stable tail probabilities.</summary>
        internal static double Quantile(double probability)
        {
            if (double.IsNaN(probability) || probability < 0d || probability > 1d)
                throw new ArgumentOutOfRangeException(nameof(probability));
            if (probability == 0d) return double.NegativeInfinity;
            if (probability == 1d) return double.PositiveInfinity;
            if (probability == 0.5d) return 0d;
            bool negative = probability < 0.5d;
            double target = negative ? probability : 1d - probability;
            double lower = 0d, upper = 12d;
            for (int i = 0; i < 56; i++)
            {
                double mid = (lower + upper) / 2d;
                if (UpperTail(mid) > target) lower = mid;
                else upper = mid;
            }
            double result = (lower + upper) / 2d;
            return negative ? -result : result;
        }
    }
}