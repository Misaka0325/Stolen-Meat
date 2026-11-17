using System;
using System.IO;

namespace StolenMeat
{
    /// <summary>Creates, mutates, splits and persists animal population data.</summary>
    internal static class AnimalPopulationOperations
    {
        /// <summary>Fixes newborn sexes once from the game's native 50 percent rule.</summary>
        internal static AnimalPopulationCounts Newborns(int count, Random random)
        {
            int females = AnimalSexSampler.DrawFemaleCount(count, random);
            return new AnimalPopulationCounts(count - females, 0, females, 0, 0, 0);
        }

        /// <summary>Marks intact, nonpregnant females after a successful native mating.</summary>
        internal static void MarkPregnant(AnimalPopulationCounts state, int count)
        {
            if (count < 0 || count > state.FemaleIntact) throw new ArgumentOutOfRangeException(nameof(count));
            state.FemaleIntact -= count;
            state.PregnantIntact += count;
        }

        /// <summary>Applies one native procedure to a randomly selected eligible animal.</summary>
        internal static AnimalPopulationCounts.Outcome Apply(AnimalPopulationCounts state, AnimalPopulationCounts.Procedure procedure, AnimalPopulationCounts.Target target, Random random)
        {
            if (random == null || !Enum.IsDefined(typeof(AnimalPopulationCounts.Procedure), procedure)
                || !Enum.IsDefined(typeof(AnimalPopulationCounts.Target), target)) throw new ArgumentOutOfRangeException();
            int eligibleMales = state.MaleIntact;
            int eligibleNonpregnant = state.FemaleIntact;
            int eligiblePregnant = procedure == AnimalPopulationCounts.Procedure.WireCutter ? state.PregnantIntact : state.Pregnant;
            bool female = target == AnimalPopulationCounts.Target.Female || target == AnimalPopulationCounts.Target.PregnantFemale;
            bool pregnant = target == AnimalPopulationCounts.Target.PregnantFemale;
            if (target == AnimalPopulationCounts.Target.Any)
            {
                long femaleTotal = (long)eligibleNonpregnant + eligiblePregnant;
                long available = eligibleMales + femaleTotal;
                if (available == 0) return AnimalPopulationCounts.Outcome.None;
                female = random.NextDouble() < (double)femaleTotal / available;
                // Choose pregnancy only after choosing sex, as with the requested menu.
                if (female && femaleTotal > 0)
                    pregnant = random.NextDouble() < (double)eligiblePregnant / femaleTotal;
            }
            if (!female)
            {
                if (eligibleMales == 0) return AnimalPopulationCounts.Outcome.None;
                state.MaleIntact--; state.MaleNeutered++;
                return AnimalPopulationCounts.Outcome.MaleNeutered;
            }
            if (!pregnant)
            {
                if (eligibleNonpregnant == 0) return AnimalPopulationCounts.Outcome.None;
                state.FemaleIntact--; state.FemaleNeutered++;
                return AnimalPopulationCounts.Outcome.FemaleNeutered;
            }
            if (eligiblePregnant == 0) return AnimalPopulationCounts.Outcome.None;
            if (procedure == AnimalPopulationCounts.Procedure.WireCutter)
            {
                state.PregnantIntact--; state.PregnantNeutered++;
                return AnimalPopulationCounts.Outcome.PregnantNeutered;
            }
            bool alreadyNeutered = state.PregnantNeutered > 0
                && random.NextDouble() < (double)state.PregnantNeutered / state.Pregnant;
            if (alreadyNeutered) { state.PregnantNeutered--; state.FemaleNeutered++; }
            else { state.PregnantIntact--; state.FemaleIntact++; }
            return AnimalPopulationCounts.Outcome.Miscarried;
        }
        /// <summary>Removes one animal by sex, pregnancy, then its corresponding neutering state.</summary>
        internal static AnimalPopulationCounts SplitOne(AnimalPopulationCounts state, Random random)
        {
            if (random == null || state.Total < 1) throw new ArgumentOutOfRangeException();
            bool female = random.NextDouble() < (double)state.Females / state.Total;
            if (!female)
            {
                bool neutered = random.NextDouble() < (double)state.MaleNeutered / state.Males;
                if (neutered)
                {
                    state.MaleNeutered--;
                    return new AnimalPopulationCounts(0, 1, 0, 0, 0, 0);
                }
                state.MaleIntact--;
                return new AnimalPopulationCounts(1, 0, 0, 0, 0, 0);
            }
            bool pregnant = random.NextDouble() < (double)state.Pregnant / state.Females;
            if (pregnant)
            {
                bool neutered = random.NextDouble() < (double)state.PregnantNeutered / state.Pregnant;
                if (neutered)
                {
                    state.PregnantNeutered--;
                    return new AnimalPopulationCounts(0, 0, 0, 0, 0, 1);
                }
                state.PregnantIntact--;
                return new AnimalPopulationCounts(0, 0, 0, 0, 1, 0);
            }
            int nonpregnant = state.FemaleIntact + state.FemaleNeutered;
            bool femaleNeutered = random.NextDouble() < (double)state.FemaleNeutered / nonpregnant;
            if (femaleNeutered)
            {
                state.FemaleNeutered--;
                return new AnimalPopulationCounts(0, 0, 0, 1, 0, 0);
            }
            state.FemaleIntact--;
            return new AnimalPopulationCounts(0, 0, 1, 0, 0, 0);
        }
        /// <summary>Splits virtual counts by floors, keeping totals and each marginal count.</summary>
        internal static bool TrySplitMultiple(AnimalPopulationCounts state, int amount, out AnimalPopulationCounts split)
        {
            split = null;
            int total = state.Total;
            if (amount < 2 || amount >= total) throw new ArgumentOutOfRangeException(nameof(amount));
            int femaleTotal = state.Females, maleTotal = state.Males;
            int female = (int)((long)femaleTotal * amount / total);
            int male = amount - female;
            int femaleRemaining = femaleTotal - female;
            int maleRemaining = maleTotal - male;
            int maleNeuteredTotal = state.MaleNeutered;
            int femaleNeuteredTotal = state.NeuteredFemales;
            int pregnantTotal = state.Pregnant;

            // Preserve per-sex totals first, then neutering and pregnancy marginals.
            int maleNeutered = ClampCount((long)maleNeuteredTotal * amount / total,
                Math.Max(0, maleNeuteredTotal - maleRemaining), Math.Min(male, maleNeuteredTotal));
            int femaleNeutered = ClampCount((long)femaleNeuteredTotal * amount / total,
                Math.Max(0, femaleNeuteredTotal - femaleRemaining), Math.Min(female, femaleNeuteredTotal));
            int pregnant = ClampCount((long)pregnantTotal * amount / total,
                Math.Max(0, pregnantTotal - femaleRemaining), Math.Min(female, pregnantTotal));

            int pregnantRemaining = pregnantTotal - pregnant;
            int neuteredRemaining = femaleNeuteredTotal - femaleNeutered;
            long lowTaken = Math.Max(0L, (long)pregnant + femaleNeutered - female);
            long highTaken = Math.Min(pregnant, femaleNeutered);
            long lowRemaining = Math.Max(0L, (long)pregnantRemaining + neuteredRemaining - femaleRemaining);
            long highRemaining = Math.Min(pregnantRemaining, neuteredRemaining);
            long preferred = (long)state.PregnantNeutered * amount / total;
            // Keep the original overlap when possible; only reassign virtual correlations if required.
            long commonLow = Math.Max(lowTaken, state.PregnantNeutered - highRemaining);
            long commonHigh = Math.Min(highTaken, state.PregnantNeutered - lowRemaining);
            long takenOverlap = commonLow <= commonHigh
                ? Math.Max(commonLow, Math.Min(commonHigh, preferred))
                : Math.Max(lowTaken, Math.Min(highTaken, preferred));
            long remainingOverlap = Math.Max(lowRemaining,
                Math.Min(highRemaining, state.PregnantNeutered - takenOverlap));

            split = new AnimalPopulationCounts(male - maleNeutered, maleNeutered,
                checked((int)(female - (long)pregnant - femaleNeutered + takenOverlap)),
                checked((int)(femaleNeutered - takenOverlap)),
                checked((int)(pregnant - takenOverlap)), checked((int)takenOverlap));
            state.MaleNeutered = maleNeuteredTotal - maleNeutered;
            state.MaleIntact = maleRemaining - state.MaleNeutered;
            state.PregnantNeutered = checked((int)remainingOverlap);
            state.PregnantIntact = checked((int)(pregnantRemaining - remainingOverlap));
            state.FemaleNeutered = checked((int)(neuteredRemaining - remainingOverlap));
            state.FemaleIntact = checked((int)(femaleRemaining - (long)pregnantRemaining
                - neuteredRemaining + remainingOverlap));
            return true;
        }

        private static int ClampCount(long desired, int minimum, int maximum) =>
            checked((int)Math.Max(minimum, Math.Min(maximum, desired)));

        internal static void WriteTo(AnimalPopulationCounts state, BinaryWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.Write(state.MaleIntact);
            writer.Write(state.MaleNeutered);
            writer.Write(state.FemaleIntact);
            writer.Write(state.FemaleNeutered);
            writer.Write(state.PregnantIntact);
            writer.Write(state.PregnantNeutered);
        }

        internal static AnimalPopulationCounts ReadFrom(BinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            try
            {
                return new AnimalPopulationCounts(reader.ReadInt32(), reader.ReadInt32(),
                    reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            }
            catch (ArgumentOutOfRangeException error) { throw new InvalidDataException("Invalid population counts", error); }
        }
    }
}
