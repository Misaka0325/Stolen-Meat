using System;
using System.Numerics;
using System.IO;

namespace StolenMeat
{
    /// <summary>Counts active and waiting doses for one drug in one parent pile.</summary>
    internal sealed class DoseSchedule : TimedStateTable
    {
        private readonly long[] pendingDays;

        internal DoseSchedule(int durationDays) : base(durationDays)
        {
            pendingDays = new long[durationDays + 1];
        }

        internal long PendingAt(int days) => pendingDays[days];
        internal long Pending => Sum(pendingDays);

        private static long Sum(long[] values)
        {
            long total = 0;
            foreach (long value in values) total = checked(total + value);
            return total;
        }

        internal DoseSchedule Copy()
        {
            var copy = new DoseSchedule(Duration);
            Array.Copy(remainingDays, copy.remainingDays, remainingDays.Length);
            Array.Copy(pendingDays, copy.pendingDays, pendingDays.Length);
            return copy;
        }

        /// <summary>Writes active and waiting counts together with their remaining days.</summary>
        internal void WriteTo(BinaryWriter writer)
        {
            writer.Write(Duration);
            for (int day = 1; day <= Duration; day++) writer.Write(remainingDays[day]);
            for (int day = 1; day <= Duration; day++) writer.Write(pendingDays[day]);
        }

        /// <summary>Restores a schedule while rejecting invalid or oversized counts.</summary>
        internal static DoseSchedule ReadFrom(BinaryReader reader, int animals)
        {
            int duration = reader.ReadInt32();
            if (duration < 1 || duration > 10000 || animals < 1) throw new InvalidDataException();
            var result = new DoseSchedule(duration);
            for (int day = 1; day <= duration; day++)
            {
                result.remainingDays[day] = reader.ReadInt64();
                if (result.remainingDays[day] < 0) throw new InvalidDataException();
            }
            for (int day = 1; day <= duration; day++)
            {
                result.pendingDays[day] = reader.ReadInt64();
                if (result.pendingDays[day] < 0) throw new InvalidDataException();
            }
            try
            {
                if (result.Active > animals || result.Pending > 0 && result.Active != animals)
                    throw new InvalidDataException();
            }
            catch (OverflowException) { throw new InvalidDataException(); }
            return result;
        }
        private void Commit(DoseSchedule source)
        {
            Array.Copy(source.remainingDays, remainingDays, remainingDays.Length);
            Array.Copy(source.pendingDays, pendingDays, pendingDays.Length);
        }

        internal void Inject(long animals)
        {
            if (animals < 1 || Active > animals) throw new ArgumentOutOfRangeException(nameof(animals));
            if (Active < animals) remainingDays[Duration] = checked(remainingDays[Duration] + 1);
            else
            {
                if (Pending == long.MaxValue) throw new OverflowException();
                pendingDays[Duration] = checked(pendingDays[Duration] + 1);
            }
        }

        /// <summary>Draws an active duration for an extracted rat, or zero for no dose.</summary>
        internal int SampleSingleDuration(long assignedDoses, Random random)
        {
            if (assignedDoses < 0 || random == null) throw new ArgumentOutOfRangeException();
            if (assignedDoses == 0) return 0;
            long active = Active;
            if (active <= 0) throw new InvalidOperationException("No active dose can supply a duration.");
            long selected = (long)(random.NextDouble() * active);
            for (int day = 1; day <= Duration; day++)
            {
                if (selected < remainingDays[day]) return day;
                selected -= remainingDays[day];
            }
            throw new InvalidOperationException("Dose duration distribution is invalid.");
        }

        /// <summary>Moves an exact count proportionally while preserving every duration bucket.</summary>
        private static void MoveProportional(long[] source, long[] destination,
            long count, long total, Random random)
        {
            if (count == 0) return;
            if (count < 0 || total < count) throw new ArgumentOutOfRangeException();
            const long scale = 9007199254740992L;
            long offset = (long)(random.NextDouble() * scale);
            BigInteger denominator = (BigInteger)total * scale;
            BigInteger cumulative = 0;
            long previous = 0;
            for (int day = 1; day < source.Length && day < destination.Length; day++)
            {
                cumulative += source[day];
                long rounded = (long)((cumulative * count * scale + (BigInteger)offset * total)
                    / denominator);
                long moved = rounded - previous;
                source[day] -= moved;
                destination[day] = checked(destination[day] + moved);
                previous = rounded;
            }
            if (previous != count) throw new InvalidOperationException("Dose transfer did not conserve counts.");
        }

        /// <summary>Splits counted doses, retaining active and waiting duration buckets.</summary>
        internal DoseSchedule SplitMultiple(int animals, int taken, Random random)
        {
            long active = Active, pending = Pending;
            if (animals < 3 || taken < 2 || taken >= animals || random == null
                || active > animals || pending > 0 && active != animals)
                throw new ArgumentOutOfRangeException();
            var source = Copy();
            var result = new DoseSchedule(Duration);
            long activeMove = (long)decimal.Floor((decimal)active * taken / animals
                + (decimal)random.NextDouble());
            long pendingMove = (long)decimal.Floor((decimal)pending * taken / animals
                + (decimal)random.NextDouble());
            MoveProportional(source.remainingDays, result.remainingDays, activeMove, active, random);
            MoveProportional(source.pendingDays, result.pendingDays, pendingMove, pending, random);
            Commit(source);
            return result;
        }

        /// <summary>Combines schedules and immediately admits waiting doses into free slots.</summary>
        internal DoseSchedule Combine(DoseSchedule other, long animals)
        {
            if (other == null || other.Duration != Duration || animals < 1)
                throw new ArgumentOutOfRangeException();
            var result = Copy();
            for (int day = 1; day <= Duration; day++)
            {
                result.remainingDays[day] = checked(result.remainingDays[day] + other.remainingDays[day]);
                result.pendingDays[day] = checked(result.pendingDays[day] + other.pendingDays[day]);
            }
            if (result.Active > animals) throw new InvalidOperationException("Active doses exceed pile size.");
            long available = animals - result.Active;
            for (int day = 1; day <= Duration && available > 0; day++)
            {
                long admitted = Math.Min(available, result.pendingDays[day]);
                result.pendingDays[day] -= admitted;
                result.remainingDays[day] = checked(result.remainingDays[day] + admitted);
                available -= admitted;
            }
            _ = result.Pending;
            return result;
        }
        /// <summary>Moves an assigned count into one rat without creating or losing doses.</summary>
        internal DoseSchedule SplitSingle(int animals, long assigned, Random random)
        {
            long active = Active, pending = Pending;
            if (animals < 2 || assigned < 0 || random == null || active > animals
                || pending > 0 && active != animals || (decimal)assigned > (decimal)active + pending)
                throw new ArgumentOutOfRangeException();
            var source = Copy();
            var result = new DoseSchedule(Duration);
            if (assigned > 0)
            {
                int day = source.SampleSingleDuration(assigned, random);
                source.remainingDays[day]--;
                result.remainingDays[day]++;
                long remaining = assigned - 1;
                long fromPending = Math.Min(remaining, source.Pending);
                MoveProportional(source.pendingDays, result.pendingDays, fromPending, source.Pending, random);
                remaining -= fromPending;
                MoveProportional(source.remainingDays, result.pendingDays, remaining, source.Active, random);
            }
            else if (source.Active > animals - 1)
            {
                if (source.Pending == long.MaxValue) throw new OverflowException();
                int day = source.SampleSingleDuration(1, random);
                source.remainingDays[day]--;
                source.pendingDays[day] = checked(source.pendingDays[day] + 1);
            }
            // Validate aggregate counts before committing, including near Int64 limits.
            _ = source.Pending;
            _ = result.Pending;
            if (source.Active > animals - 1 || source.Pending > 0 && source.Active != animals - 1
                || result.Active > 1 || result.Pending > 0 && result.Active != 1)
                throw new InvalidOperationException("Split produced an invalid dose state.");
            Commit(source);
            return result;
        }

        /// <summary>Expires active doses and activates waiting doses with their saved duration.</summary>
        internal void AdvanceNight(long animals)
        {
            long active = Active, pending = Pending;
            if (animals < 1 || active > animals || pending > 0 && active != animals)
                throw new ArgumentOutOfRangeException(nameof(animals));
            AdvanceStates();
            long available = animals - Active;
            // Prioritize the shortest remaining duration when activating queued doses.
            for (int day = 1; day <= Duration && available > 0; day++)
            {
                long admitted = Math.Min(available, pendingDays[day]);
                pendingDays[day] -= admitted;
                remainingDays[day] = checked(remainingDays[day] + admitted);
                available -= admitted;
            }
        }
    }
}