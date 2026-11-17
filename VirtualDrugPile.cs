using System;
using System.IO;
using System.Text;

namespace StolenMeat
{
    /// <summary>Stores counted drug schedules independently from the ordinary animal panel.</summary>
    internal sealed class VirtualDrugPile
    {
        internal int Animals { get; private set; }
        internal DoseSchedule Steroid { get; private set; }
        internal DoseSchedule Immunity { get; private set; }

        internal VirtualDrugPile(int animals, int steroidDays, int immunityDays)
        {
            if (animals < 1) throw new ArgumentOutOfRangeException(nameof(animals));
            Animals = animals;
            Steroid = new DoseSchedule(steroidDays);
            Immunity = new DoseSchedule(immunityDays);
        }

        private VirtualDrugPile(int animals, DoseSchedule steroid, DoseSchedule immunity)
        {
            Animals = animals;
            Steroid = steroid;
            Immunity = immunity;
        }

        /// <summary>Immediately changes the effective panel while retaining the ordinary value.</summary>
        internal void Inject(string drug)
        {
            if (drug == AnimalDrugEffects.Steroid) Steroid.Inject(Animals);
            else if (drug == AnimalDrugEffects.Immunity) Immunity.Inject(Animals);
            else throw new ArgumentException("Unsupported drug", nameof(drug));
        }

        internal double Effective(double ordinary, string attribute) =>
            AnimalDrugEffects.ApplyCombined(ordinary, attribute, Animals, Steroid.Active, Immunity.Active);

        internal void AdvanceNight()
        {
            DoseSchedule steroid = Steroid.Copy(), immunity = Immunity.Copy();
            steroid.AdvanceNight(Animals);
            immunity.AdvanceNight(Animals);
            Steroid = steroid;
            Immunity = immunity;
        }

        private static long DrawSingle(DoseSchedule schedule, int animals, Random random)
        {
            var statistics = DoseSplitPreparation.Prepare(animals, schedule.Active, schedule.Pending, random);
            if (animals == 2) return DoseSplitPreparation.TwoRatDoseCount(statistics);
            return DoseSplitPreparation.UsesNormalApproximation(statistics, animals)
                ? DoseSplitPreparation.ResolveNormal(statistics, animals, random)
                : DoseSplitPreparation.ResolveBinomial(statistics, animals);
        }

        /// <summary>Moves both drugs atomically into a new pile, conserving every duration.</summary>
        internal VirtualDrugPile Split(int taken, Random random)
        {
            if (random == null || taken < 1 || taken >= Animals) throw new ArgumentOutOfRangeException();
            DoseSchedule steroid = Steroid.Copy(), immunity = Immunity.Copy();
            DoseSchedule selectedSteroid, selectedImmunity;
            if (taken == 1)
            {
                selectedSteroid = steroid.SplitSingle(Animals, DrawSingle(steroid, Animals, random), random);
                selectedImmunity = immunity.SplitSingle(Animals, DrawSingle(immunity, Animals, random), random);
            }
            else
            {
                selectedSteroid = steroid.SplitMultiple(Animals, taken, random);
                selectedImmunity = immunity.SplitMultiple(Animals, taken, random);
            }
            Animals -= taken;
            Steroid = steroid;
            Immunity = immunity;
            return new VirtualDrugPile(taken, selectedSteroid, selectedImmunity);
        }

        /// <summary>Returns a merged pile without modifying either source pile.</summary>
        internal VirtualDrugPile Merge(VirtualDrugPile other)
        {
            if (other == null || (long)Animals + other.Animals > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(other));
            int combined = Animals + other.Animals;
            return new VirtualDrugPile(combined, Steroid.Combine(other.Steroid, combined),
                Immunity.Combine(other.Immunity, combined));
        }
        internal string Serialize()
        {
            using (var buffer = new MemoryStream())
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
            {
                writer.Write((byte)1);
                writer.Write(Animals);
                Steroid.WriteTo(writer);
                Immunity.WriteTo(writer);
                writer.Flush();
                return Convert.ToBase64String(buffer.ToArray());
            }
        }

        internal static bool TryDeserialize(string encoded, out VirtualDrugPile result)
        {
            result = null;
            if (string.IsNullOrEmpty(encoded) || encoded.Length > 500000) return false;
            try
            {
                using (var buffer = new MemoryStream(Convert.FromBase64String(encoded), false))
                using (var reader = new BinaryReader(buffer, Encoding.UTF8))
                {
                    if (reader.ReadByte() != 1) return false;
                    int animals = reader.ReadInt32();
                    if (animals < 1) return false;
                    var steroid = DoseSchedule.ReadFrom(reader, animals);
                    var immunity = DoseSchedule.ReadFrom(reader, animals);
                    if (buffer.Position != buffer.Length) return false;
                    result = new VirtualDrugPile(animals, steroid, immunity);
                    return true;
                }
            }
            catch (Exception error) when (error is FormatException || error is IOException
                || error is OverflowException || error is ArgumentException) { return false; }
        }
    }
}