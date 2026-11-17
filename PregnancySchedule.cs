using System;
using System.IO;

namespace StolenMeat
{
    /// <summary>Counts pregnancies with the same parent identity by native remaining days.</summary>
    internal sealed class PregnancySchedule : TimedStateTable
    {
        internal string ParentGroup { get; }

        internal PregnancySchedule(int durationDays, string parentGroup) : base(durationDays)
        {
            if (string.IsNullOrEmpty(parentGroup))
                throw new ArgumentException("A parent group is required", nameof(parentGroup));
            ParentGroup = parentGroup;
        }

        /// <summary>Records existing native progress; a due pregnancy finishes on the next tick.</summary>
        internal void AddPregnancies(float progress, float gestationPeriod, long count, long mothers)
        {
            if (!float.IsFinite(progress) || !float.IsFinite(gestationPeriod)
                || progress < 0f || gestationPeriod <= 0f)
                throw new ArgumentOutOfRangeException();
            double remaining = Math.Max(1d, Math.Ceiling((double)gestationPeriod - progress));
            if (remaining > Duration) throw new ArgumentOutOfRangeException(nameof(gestationPeriod));
            AddStates((int)remaining, count, mothers);
        }

        internal void WriteTo(BinaryWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.Write(Duration);
            writer.Write(ParentGroup);
            for (int day = 1; day <= Duration; day++) writer.Write(CountAt(day));
        }

        internal static PregnancySchedule ReadFrom(BinaryReader reader, long mothers)
        {
            if (reader == null || mothers < 0) throw new InvalidDataException();
            int duration = reader.ReadInt32();
            string group = reader.ReadString();
            if (duration < 1 || duration > 10000 || string.IsNullOrEmpty(group)
                || group.Length > 4096) throw new InvalidDataException();
            var result = new PregnancySchedule(duration, group);
            for (int day = 1; day <= duration; day++)
            {
                long count = reader.ReadInt64();
                if (count < 0 || count > mothers - result.Active) throw new InvalidDataException();
                result.AddStates(day, count, mothers);
            }
            return result;
        }
    }
}