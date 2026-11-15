using System;

namespace AsahiMono.Core
{
    /// <summary>
    /// The single source of time for the whole simulation.
    ///
    /// Pure C# on purpose: no engine types, no screen coordinates, no file system.
    /// Everything else asks this class what day it is instead of computing it.
    ///
    /// Unit: one tick is one in-game minute. <see cref="TicksPerDay"/> ticks = one full day.
    /// Days are 1-based (day 1 is the first day of the game); <see cref="DaysElapsed"/> is 0-based.
    /// </summary>
    public sealed class WorldClock
    {
        /// <summary>Ticks in one in-game day (24 h * 60 min).</summary>
        public const int TicksPerDay = 1440;

        /// <summary>Ticks in one in-game hour.</summary>
        public const int TicksPerHour = 60;

        /// <summary>Total ticks advanced since the clock was created or reset.</summary>
        public int TotalTicks { get; private set; }

        /// <summary>Current day number, 1-based.</summary>
        public int Day => (TotalTicks / TicksPerDay) + 1;

        /// <summary>Ticks already elapsed inside the current day (0 .. <see cref="TicksPerDay"/> - 1).</summary>
        public int TickOfDay => TotalTicks % TicksPerDay;

        /// <summary>Number of whole days that have passed (0 on day 1).</summary>
        public int DaysElapsed => TotalTicks / TicksPerDay;

        /// <summary>
        /// Raised once for every day boundary crossed by <see cref="Advance"/>,
        /// with the new day number as the payload.
        /// </summary>
        public event EventHandler<NewDayEventArgs>? NewDay;

        /// <summary>
        /// Advance the clock by <paramref name="ticks"/> ticks.
        /// Raises <see cref="NewDay"/> once per crossed day boundary, in order.
        /// </summary>
        /// <returns>The number of day boundaries crossed.</returns>
        /// <exception cref="ArgumentOutOfRangeException">When <paramref name="ticks"/> is negative.</exception>
        public int Advance(int ticks)
        {
            if (ticks < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "ticks must be >= 0; the clock never runs backwards.");
            }

            if (ticks == 0)
            {
                return 0;
            }

            int dayBefore = TotalTicks / TicksPerDay;
            TotalTicks += ticks;
            int dayAfter = TotalTicks / TicksPerDay;
            int crossed = dayAfter - dayBefore;

            EventHandler<NewDayEventArgs>? handler = NewDay;
            if (handler is not null)
            {
                for (int i = 1; i <= crossed; i++)
                {
                    handler(this, new NewDayEventArgs(dayBefore + i + 1));
                }
            }

            return crossed;
        }

        /// <summary>Advance by an amount of in-game minutes (alias kept for readability).</summary>
        public int AdvanceMinutes(int minutes) => Advance(minutes);

        /// <summary>Advance to the very start of the next day and return the new day number.</summary>
        public int AdvanceToNextDay()
        {
            int ticksToBoundary = TicksPerDay - TickOfDay;
            Advance(ticksToBoundary);
            return Day;
        }

        /// <summary>Put the clock back to day 1, tick 0. Does not raise events.</summary>
        public void Reset()
        {
            TotalTicks = 0;
        }

        public override string ToString() => $"Day {Day} +{TickOfDay}t";
    }

    /// <summary>Payload of <see cref="WorldClock.NewDay"/>.</summary>
    public sealed class NewDayEventArgs : EventArgs
    {
        public NewDayEventArgs(int day)
        {
            Day = day;
        }

        /// <summary>The day number that has just started (1-based).</summary>
        public int Day { get; }
    }
}
