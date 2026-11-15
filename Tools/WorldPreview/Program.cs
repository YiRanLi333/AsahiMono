using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AsahiMono.Core;
using AsahiMono.Core.World;

namespace AsahiMono.Tools.WorldPreview
{
    /// <summary>
    /// Headless playground for Game.Core: renders a seed as an ASCII map so world
    /// generation can be eyeballed on a machine without the engine.
    ///
    /// <code>
    /// dotnet run --project Tools/WorldPreview -- --seed 20261115 --ascii
    /// </code>
    ///
    /// Writes exactly <c>height</c> lines of <c>width</c> characters (no legend line, so
    /// <c>wc -l</c> equals the world height).
    /// </summary>
    public static class Program
    {
        private const int DefaultSeed = 20261115;
        private const int DefaultSize = 96;

        public static int Main(string[] args)
        {
            Settings settings;
            try
            {
                settings = Settings.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                PrintUsage();
                return 2;
            }

            if (settings.Help)
            {
                PrintUsage();
                return 0;
            }

            var clock = new WorldClock();
            clock.Advance(WorldClock.TicksPerDay);

            var world = WorldGen.Generate(new WorldGenOptions(settings.Seed, settings.Width, settings.Height));

            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"world preview ok -- seed {settings.Seed}, {settings.Width}x{settings.Height}, clock after one day: {clock}"));

            if (!settings.Ascii)
            {
                PrintStats(world);
                Console.WriteLine("use --ascii to dump the map to a text file");
                return 0;
            }

            string path = settings.OutPath ?? DefaultOutPath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string text = BuildAscii(world);
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            int lines = text.Count(c => c == '\n');
            int columns = text.IndexOf('\n');
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ascii written: {path} ({lines} lines x {columns} columns)"));
            Console.WriteLine("legend: '~' water  ',' grass  '.' soil  ':' tilled  '_' sand  '=' path  'T' tree  'O' rock  '\"' weed  'S' spawn");
            PrintStats(world);
            return 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("usage: WorldPreview [--seed N] [--size N | --width N --height N] [--ascii] [--out PATH]");
            Console.WriteLine("default --ascii target: E:\\AsahiMono\\tmp\\world-ascii.txt (fallback: <temp>/world-ascii.txt)");
        }

        private static string DefaultOutPath()
        {
            const string ProjectTmp = @"E:\AsahiMono\tmp";
            if (OperatingSystem.IsWindows() && Directory.Exists(ProjectTmp))
            {
                return Path.Combine(ProjectTmp, "world-ascii.txt");
            }

            return Path.Combine(Path.GetTempPath(), "world-ascii.txt");
        }

        private static string BuildAscii(WorldMap world)
        {
            var builder = new StringBuilder((world.Width + 1) * world.Height);

            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    builder.Append(CharOf(world, x, y));
                }

                builder.Append('\n');
            }

            return builder.ToString();
        }

        private static char CharOf(WorldMap world, int x, int y)
        {
            if (world.Spawn == (x, y))
            {
                return 'S';
            }

            char tile = world.GetTile(x, y) switch
            {
                TileKind.Void => ' ',
                TileKind.Grass => ',',
                TileKind.Soil => '.',
                TileKind.TilledSoil => ':',
                TileKind.Water => '~',
                TileKind.Sand => '_',
                TileKind.Path => '=',
                _ => '?',
            };

            return world.GetProp(x, y) switch
            {
                PropKind.Tree => 'T',
                PropKind.Rock => 'O',
                PropKind.Weed => '"',
                _ => tile,
            };
        }

        private static void PrintStats(WorldMap world)
        {
            int total = world.Width * world.Height;
            int water = 0;
            int grass = 0;
            int sand = 0;
            int soil = 0;
            int trees = 0;
            int rocks = 0;
            int weeds = 0;
            int walkable = 0;

            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    switch (world.GetTile(x, y))
                    {
                        case TileKind.Water:
                            water++;
                            break;
                        case TileKind.Grass:
                            grass++;
                            break;
                        case TileKind.Sand:
                            sand++;
                            break;
                        case TileKind.Soil:
                            soil++;
                            break;
                    }

                    switch (world.GetProp(x, y))
                    {
                        case PropKind.Tree:
                            trees++;
                            break;
                        case PropKind.Rock:
                            rocks++;
                            break;
                        case PropKind.Weed:
                            weeds++;
                            break;
                    }

                    if (world.IsWalkable(x, y))
                    {
                        walkable++;
                    }
                }
            }

            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"stats: {total} cells | water {water} ({Percent(water, total):0.0}%) grass {grass} ({Percent(grass, total):0.0}%) sand {sand} soil {soil} | trees {trees} rocks {rocks} weeds {weeds} | walkable {walkable} | spawn {world.Spawn.X},{world.Spawn.Y}"));
        }

        private static double Percent(int part, int total) => total == 0 ? 0 : part * 100.0 / total;

        /// <summary>Command line of the tool, parsed and validated once.</summary>
        private sealed class Settings
        {
            public int Seed { get; private init; } = DefaultSeed;

            public int Width { get; private init; } = DefaultSize;

            public int Height { get; private init; } = DefaultSize;

            public bool Ascii { get; private init; }

            public bool Help { get; private init; }

            public string? OutPath { get; private init; }

            public static Settings Parse(string[] args)
            {
                int seed = DefaultSeed;
                int width = DefaultSize;
                int height = DefaultSize;
                bool ascii = false;
                bool help = false;
                string? outPath = null;

                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];

                    switch (arg)
                    {
                        case "--seed":
                            seed = ParseInt(args, ref i, arg);
                            break;

                        case "--size":
                            width = ParseInt(args, ref i, arg);
                            height = width;
                            break;

                        case "--width":
                            width = ParseInt(args, ref i, arg);
                            break;

                        case "--height":
                            height = ParseInt(args, ref i, arg);
                            break;

                        case "--out":
                            if (i + 1 >= args.Length)
                            {
                                throw new ArgumentException("--out needs a path");
                            }

                            outPath = args[++i];
                            break;

                        case "--ascii":
                            ascii = true;
                            break;

                        case "-h":
                        case "--help":
                            help = true;
                            break;

                        default:
                            if (arg.StartsWith("--seed=", StringComparison.Ordinal)
                                && int.TryParse(arg["--seed=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int inlineSeed))
                            {
                                seed = inlineSeed;
                                break;
                            }

                            throw new ArgumentException($"unknown argument: {arg}");
                    }
                }

                if (width <= 0 || height <= 0)
                {
                    throw new ArgumentException("world size must be positive");
                }

                return new Settings
                {
                    Seed = seed,
                    Width = width,
                    Height = height,
                    Ascii = ascii,
                    Help = help,
                    OutPath = outPath,
                };
            }

            private static int ParseInt(string[] args, ref int index, string name)
            {
                if (index + 1 >= args.Length
                    || !int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    throw new ArgumentException($"{name} needs an integer");
                }

                index++;
                return value;
            }
        }
    }
}
