using System;

namespace AsahiMono.Core.World;

/// <summary>
/// 确定性伪随机数：SplitMix64。
///
/// 为什么不用 .NET 自带的随机数类：它的算法在 .NET 版本之间变过（.NET 6 换了实现），
/// 同一个 seed 在不同运行时/平台上会给出不同序列，世界就重放不出来了。
/// SplitMix64 只有加法、异或、乘法，全部是 64 位整数运算，任何平台上逐位一致。
///
/// 参考实现（Steele / Lea / Flood）：状态每步加黄金比例奇数
/// <c>0x9E3779B97F4A7C15</c>，再做两轮异或-乘混合。
/// </summary>
public sealed class Rng
{
    /// <summary>SplitMix64 的状态增量（2^64 / φ 取奇）。</summary>
    private const ulong Gamma = 0x9E3779B97F4A7C15UL;

    /// <summary>1 / 2^53，把 53 位整数摊到 [0,1) 且不丢精度。</summary>
    internal const double InvTwoPow53 = 1.0 / 9007199254740992.0;

    private ulong _state;

    /// <summary>用给定种子建一个随机流。种子相同 ⇒ 序列完全相同。</summary>
    public Rng(ulong seed)
    {
        _state = seed;
    }

    /// <summary>取一个 64 位无符号整数（全部位都可用）。</summary>
    public ulong NextULong()
    {
        unchecked
        {
            _state += Gamma;
        }

        return Mix64(_state);
    }

    /// <summary>取一个 <c>[0,1)</c> 的双精度数。</summary>
    public double NextDouble() => (NextULong() >> 11) * InvTwoPow53;

    /// <summary>
    /// 取一个 <c>[0, maxExclusive)</c> 的整数。
    /// 用拒绝采样丢掉尾部的偏斜区，保证每个值概率相同（不是简单的取模）。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExclusive"/> 不是正数。</exception>
    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "maxExclusive must be > 0.");
        }

        ulong bound = (ulong)maxExclusive;

        // 2^64 里落在 [0, 2^64 % bound) 的那一小段会让 0..bound-1 多出一次机会，丢掉它。
        ulong threshold = unchecked(0UL - bound) % bound;

        while (true)
        {
            ulong value = NextULong();
            if (value >= threshold)
            {
                return (int)(value % bound);
            }
        }
    }

    /// <summary>
    /// 以概率 <paramref name="p"/> 返回 <c>true</c>。
    /// <paramref name="p"/> &lt;= 0 恒为 false，&gt;= 1 恒为 true；两种情况都仍然消耗一次随机数，
    /// 这样调用点改了阈值也不会打乱后面的序列。
    /// </summary>
    public bool Chance(double p) => NextDouble() < p;

    /// <summary>
    /// SplitMix64 的输出混合函数（也叫 finalizer）。公开给 <see cref="ValueNoise"/> 复用，
    /// 程序集外不可见。
    /// </summary>
    internal static ulong Mix64(ulong z)
    {
        unchecked
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}

/// <summary>
/// 二维 value noise（值噪声）：格点上放白噪声，格内做平滑插值，多层叠加。
///
/// 它是<b>纯函数</b>：<c>(seed, x, y, octaves)</c> 一定给出同一个数，
/// 不持有状态、不依赖 .NET 自带的随机数类、也不用任何浮点函数库里可能变味的实现
/// （只用 <c>Floor</c>、加减乘除）。
///
/// 输出已经归一化到 <c>[0,1]</c>。
/// </summary>
public static class ValueNoise
{
    /// <summary>
    /// 取 <c>[0,1]</c> 的噪声值。
    /// </summary>
    /// <param name="seed">噪声种子；同一 seed 同一坐标恒定。</param>
    /// <param name="x">x 坐标，单位是"噪声格"（想按地图格缩放就自己除以一个尺度）。</param>
    /// <param name="y">y 坐标，单位同上。</param>
    /// <param name="octaves">叠加层数；第 n 层频率 ×2、振幅 ×0.5。默认 4 层。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="octaves"/> 小于 1。</exception>
    public static double Sample2D(ulong seed, double x, double y, int octaves = 4)
    {
        if (octaves < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(octaves), octaves, "octaves must be >= 1.");
        }

        double sum = 0.0;
        double amplitude = 1.0;
        double amplitudeTotal = 0.0;
        double frequency = 1.0;

        for (int octave = 0; octave < octaves; octave++)
        {
            // 每层用独立的种子，否则各层的格点会对齐成可见的方块。
            ulong octaveSeed = Rng.Mix64(unchecked(seed + ((ulong)octave * 0x9E3779B97F4A7C15UL)));
            sum += amplitude * Octave2D(octaveSeed, x * frequency, y * frequency);
            amplitudeTotal += amplitude;
            amplitude *= 0.5;
            frequency *= 2.0;
        }

        double value = sum / amplitudeTotal;
        if (value < 0.0)
        {
            return 0.0;
        }

        return value > 1.0 ? 1.0 : value;
    }

    /// <summary>单层 value noise：四个格点值做双线性插值，插值曲线用 smoothstep。</summary>
    private static double Octave2D(ulong seed, double x, double y)
    {
        double floorX = Math.Floor(x);
        double floorY = Math.Floor(y);
        long cellX = (long)floorX;
        long cellY = (long)floorY;

        double smoothX = Smoothstep(x - floorX);
        double smoothY = Smoothstep(y - floorY);

        double v00 = Lattice(seed, cellX, cellY);
        double v10 = Lattice(seed, cellX + 1, cellY);
        double v01 = Lattice(seed, cellX, cellY + 1);
        double v11 = Lattice(seed, cellX + 1, cellY + 1);

        double top = v00 + ((v10 - v00) * smoothX);
        double bottom = v01 + ((v11 - v01) * smoothX);
        return top + ((bottom - top) * smoothY);
    }

    /// <summary>格点上的白噪声，<c>[0,1)</c>。</summary>
    private static double Lattice(ulong seed, long x, long y)
    {
        ulong hash = seed;
        unchecked
        {
            hash = Rng.Mix64(hash + ((ulong)x * 0x9E3779B97F4A7C15UL));
            hash = Rng.Mix64(hash + ((ulong)y * 0xC2B2AE3D27D4EB4FUL));
        }

        return (hash >> 11) * Rng.InvTwoPow53;
    }

    /// <summary>3t²-2t³：一阶导在两端为 0，格线处不会出现折角。</summary>
    private static double Smoothstep(double t) => t * t * (3.0 - (2.0 * t));
}
