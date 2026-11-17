using System;

namespace StolenMeat
{
    /// <summary>Applies one native drug as a direct multiplier to an unchanged normal panel.</summary>
    internal static class AnimalDrugEffects
    {
        internal const string Steroid = "livestock_steroid";
        internal const string Immunity = "livestock_immunity";

        internal static double Multiplier(string drug, string attribute, long active, long animals)
        {
            if (animals < 1 || active < 0 || active > animals) throw new ArgumentOutOfRangeException();
            double effect = 0d;
            if (drug == Steroid)
            {
                if (attribute == "ANIMAL_GROWTH_RATE_TAG") effect = 0.2d;
                else if (attribute == "ANIMAL_IMMUNITY_TAG") effect = -0.4d;
                else if (attribute == "ANIMAL_HUNGER_RATE_TAG") effect = 0.25d;
            }
            else if (drug == Immunity)
            {
                if (attribute == "ANIMAL_IMMUNITY_TAG") effect = 0.75d;
                else if (attribute == "ANIMAL_HUNGER_RATE_TAG") effect = 0.25d;
            }
            else throw new ArgumentException("Unsupported drug", nameof(drug));
            return 1d + effect * ((double)active / animals);
        }

        /// <summary>Adds native drug bonuses before multiplying the ordinary panel.</summary>
        internal static double ApplyCombined(double ordinary, string attribute, long animals,
            long steroidActive, long immunityActive)
        {
            if (!double.IsFinite(ordinary)) throw new ArgumentOutOfRangeException(nameof(ordinary));
            double multiplier = Multiplier(Steroid, attribute, steroidActive, animals)
                + Multiplier(Immunity, attribute, immunityActive, animals) - 1d;
            double value = ordinary * multiplier;
            if (!double.IsFinite(value)) throw new OverflowException();
            return value;
        }
        /// <summary>Computes the effective panel without changing the ordinary value.</summary>
        internal static double Apply(double ordinary, string drug, string attribute, long active, long animals)
        {
            if (!double.IsFinite(ordinary)) throw new ArgumentOutOfRangeException(nameof(ordinary));
            double value = ordinary * Multiplier(drug, attribute, active, animals);
            if (!double.IsFinite(value)) throw new OverflowException();
            return value;
        }
    }
}