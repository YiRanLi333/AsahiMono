using System;
using System.Collections.Generic;
using AsahiMono.Core;
using Xunit;

namespace AsahiMono.Core.Tests
{
    public sealed class WorldClockTests
    {
        [Fact]
        public void NewClockStartsOnDayOne()
        {
            var clock = new WorldClock();

            Assert.Equal(1, clock.Day);
            Assert.Equal(0, clock.DaysElapsed);
            Assert.Equal(0, clock.TickOfDay);
            Assert.Equal(0, clock.TotalTicks);
        }

        [Fact]
        public void AdvancingOneFullDayRaisesNewDayExactlyOnce()
        {
            var clock = new WorldClock();
            var days = new List<int>();
            clock.NewDay += (_, e) => days.Add(e.Day);

            int crossed = clock.Advance(WorldClock.TicksPerDay);

            Assert.Equal(1, crossed);
            Assert.Equal(2, clock.Day);
            Assert.Equal(1, clock.DaysElapsed);
            Assert.Equal(0, clock.TickOfDay);
            Assert.Equal(new[] { 2 }, days);
        }

        [Fact]
        public void AdvancingAcrossSeveralDaysRaisesEveryDayInOrder()
        {
            var clock = new WorldClock();
            var days = new List<int>();
            clock.NewDay += (_, e) => days.Add(e.Day);

            int crossed = clock.Advance((WorldClock.TicksPerDay * 3) + 5);

            Assert.Equal(3, crossed);
            Assert.Equal(4, clock.Day);
            Assert.Equal(3, clock.DaysElapsed);
            Assert.Equal(5, clock.TickOfDay);
            Assert.Equal(new[] { 2, 3, 4 }, days);
        }

        [Fact]
        public void AdvancingInsideTheSameDayRaisesNoEvent()
        {
            var clock = new WorldClock();
            var days = new List<int>();
            clock.NewDay += (_, e) => days.Add(e.Day);

            int crossed = clock.Advance(WorldClock.TicksPerDay - 1);

            Assert.Equal(0, crossed);
            Assert.Equal(1, clock.Day);
            Assert.Empty(days);
        }

        [Fact]
        public void AdvanceToNextDayLandsOnTickZeroOfTheNextDay()
        {
            var clock = new WorldClock();
            clock.Advance(10);

            int day = clock.AdvanceToNextDay();

            Assert.Equal(2, day);
            Assert.Equal(0, clock.TickOfDay);
            Assert.Equal(WorldClock.TicksPerDay, clock.TotalTicks);
        }

        [Fact]
        public void NegativeAdvanceThrows()
        {
            var clock = new WorldClock();

            Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(-1));
            Assert.Equal(0, clock.TotalTicks);
        }
    }
}
