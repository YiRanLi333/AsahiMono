using System;
using AsahiMono.Core;

namespace AsahiMono.Tools.WorldPreview
{
    /// <summary>
    /// Headless playground for Game.Core: later this renders a seed as ASCII/PNG.
    /// For M0 it only proves that the engine-free library can be driven from a console
    /// (and therefore from CI on Linux/macOS/Windows).
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            var clock = new WorldClock();
            clock.Advance(WorldClock.TicksPerDay);

            Console.WriteLine("world preview ok");
            Console.WriteLine($"clock after one day: {clock}");
            return 0;
        }
    }
}
