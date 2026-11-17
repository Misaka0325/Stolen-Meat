using System;

namespace StolenMeat
{
    /// <summary>Counts independent timed states by remaining day without expanding individuals.</summary>
    internal class TimedStateTable
    {
        protected readonly long[] remainingDays;
        internal int Duration => remainingDays.Length - 2;
        internal long CountAt(int days) => remainingDays[days];

        internal long Active
        {
            get
            {
                long total = 0;
                foreach (long value in remainingDays) total = checked(total + value);
                return total;
            }
        }

        internal TimedStateTable(int durationDays)
        {
            if (durationDays < 1 || durationDays > 10000)
                throw new ArgumentOutOfRangeException(nameof(durationDays));
            remainingDays = new long[durationDays + 2];
        }

        /// <summary>Adds states with the specified remaining time.</summary>
        internal void AddStates(int days, long count, long capacity)
        {
            if (days < 1 || days > Duration || count < 0 || capacity < 0
                || count > capacity || Active > capacity - count)
                throw new ArgumentOutOfRangeException();
            remainingDays[days] = checked(remainingDays[days] + count);
        }

        /// <summary>Advances one night and returns the number that expired.</summary>
        internal long AdvanceStates()
        {
            long expired = remainingDays[1];
            for (int day = 1; day <= Duration; day++) remainingDays[day] = remainingDays[day + 1];
            remainingDays[Duration + 1] = 0;
            return expired;
        }
    }
}