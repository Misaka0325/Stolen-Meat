using System;
using System.IO;

namespace StolenMeat
{
    /// <summary>Exact sex, pregnancy and neutering counts for one animal population.</summary>
    internal sealed class AnimalPopulationCounts
    {
        internal int MaleIntact { get; set; }
        internal int MaleNeutered { get; set; }
        internal int FemaleIntact { get; set; }
        internal int FemaleNeutered { get; set; }
        internal int PregnantIntact { get; set; }
        internal int PregnantNeutered { get; set; }
        internal int Males => checked(MaleIntact + MaleNeutered);
        internal int Females => checked(FemaleIntact + FemaleNeutered + PregnantIntact + PregnantNeutered);
        internal int Pregnant => checked(PregnantIntact + PregnantNeutered);
        internal int NeuteredMales => MaleNeutered;
        internal int NeuteredFemales => checked(FemaleNeutered + PregnantNeutered);
        internal int Total => checked(Males + Females);

        internal AnimalPopulationCounts(int maleIntact, int maleNeutered,
            int femaleIntact, int femaleNeutered, int pregnantIntact, int pregnantNeutered)
        {
            if (maleIntact < 0 || maleNeutered < 0 || femaleIntact < 0
                || femaleNeutered < 0 || pregnantIntact < 0 || pregnantNeutered < 0)
                throw new ArgumentOutOfRangeException();
            long total = (long)maleIntact + maleNeutered + femaleIntact + femaleNeutered
                + pregnantIntact + pregnantNeutered;
            if (total > int.MaxValue) throw new ArgumentOutOfRangeException();
            MaleIntact = maleIntact;
            MaleNeutered = maleNeutered;
            FemaleIntact = femaleIntact;
            FemaleNeutered = femaleNeutered;
            PregnantIntact = pregnantIntact;
            PregnantNeutered = pregnantNeutered;
        }

        internal enum Target { Male, Female, PregnantFemale, Any }
        internal enum Procedure { WireCutter, Surgery }
        internal enum Outcome { None, MaleNeutered, FemaleNeutered, PregnantNeutered, Miscarried }

    }
}
