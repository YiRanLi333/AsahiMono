using System;
using System.Collections.Generic;
using AsahiMono.Core.Save;

namespace AsahiMono.Core.World;

/// <summary>
/// 地表格子类型。数值是存档里的稳定编码，<b>不要改动已有数值</b>。
/// </summary>
public enum TileKind
{
    /// <summary>虚空：地图外或尚未生成。不可站立。</summary>
    Void = 0,

    /// <summary>普通草地，可站立，是默认地表。</summary>
    Grass = 1,

    /// <summary>裸土（未耕作）。</summary>
    Soil = 2,

    /// <summary>已耕作的土地，可以播种。</summary>
    TilledSoil = 3,

    /// <summary>水。不可站立。</summary>
    Water = 4,

    /// <summary>沙地（水边的过渡带），可站立。</summary>
    Sand = 5,

    /// <summary>铺过的路，稍后走路会更快。</summary>
    Path = 6,
}

/// <summary>
/// 叠在格子上的物件。数值同样是存档编码。
/// </summary>
public enum PropKind
{
    /// <summary>没有物件。</summary>
    None = 0,

    /// <summary>树。挡住去路，可以砍。</summary>
    Tree = 1,

    /// <summary>石头。挡住去路，可以敲。</summary>
    Rock = 2,

    /// <summary>杂草。不挡路，可以割。</summary>
    Weed = 3,
}

/// <summary>
/// 一张格子地图，是世界的权威数据。
///
/// 坐标约定：<c>(0,0)</c> 在<b>左上角</b>，<c>x</c> 向右、<c>y</c> 向下（与表现层 TileMap 的约定一致），
/// 有效范围 <c>0 &lt;= x &lt; Width</c>、<c>0 &lt;= y &lt; Height</c>。
///
/// 越界访问一律"安全但无效果"：读返回 <see cref="TileKind.Void"/> / <see cref="PropKind.None"/>，
/// 写直接忽略，<b>不抛异常</b>——这样表现层在视野边缘可以放心地多查几格。
/// </summary>
public sealed class WorldMap
{
    private readonly TileKind[] _tiles;
    private readonly PropKind[] _props;

    /// <summary>
    /// 建一张全是 <see cref="TileKind.Void"/> 的空地图。地形由 <see cref="WorldGen"/> 填，
    /// 出生点先暂定在地图中心，生成结束时再改写。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> 或 <paramref name="height"/> 不是正数。</exception>
    public WorldMap(int width, int height, int seed)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "width must be > 0.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "height must be > 0.");
        }

        Width = width;
        Height = height;
        Seed = seed;
        _tiles = new TileKind[width * height];
        _props = new PropKind[width * height];
        Spawn = (width / 2, height / 2);
    }

    /// <summary>地图宽度（格）。</summary>
    public int Width { get; }

    /// <summary>地图高度（格）。</summary>
    public int Height { get; }

    /// <summary>生成这张图用的种子。重放同一张图需要它。</summary>
    public int Seed { get; }

    /// <summary>出生格。由 <see cref="WorldGen"/> 在生成结束时写入。</summary>
    public (int X, int Y) Spawn { get; internal set; }

    /// <summary>坐标是否落在地图内。</summary>
    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    /// <summary>取地表类型；越界返回 <see cref="TileKind.Void"/>。</summary>
    public TileKind GetTile(int x, int y)
    {
        int index = IndexOf(x, y);
        return index < 0 ? TileKind.Void : _tiles[index];
    }

    /// <summary>写地表类型；越界写入被忽略（不抛异常）。</summary>
    public void SetTile(int x, int y, TileKind kind)
    {
        int index = IndexOf(x, y);
        if (index < 0)
        {
            return;
        }

        _tiles[index] = kind;
    }

    /// <summary>取格子上的物件；越界返回 <see cref="PropKind.None"/>。</summary>
    public PropKind GetProp(int x, int y)
    {
        int index = IndexOf(x, y);
        return index < 0 ? PropKind.None : _props[index];
    }

    /// <summary>写格子上的物件；越界写入被忽略（不抛异常）。</summary>
    public void SetProp(int x, int y, PropKind prop)
    {
        int index = IndexOf(x, y);
        if (index < 0)
        {
            return;
        }

        _props[index] = prop;
    }

    /// <summary>可站立：非 Void/Water，且该格无 Tree/Rock。越界恒为 <c>false</c>。</summary>
    public bool IsWalkable(int x, int y)
    {
        int index = IndexOf(x, y);
        if (index < 0)
        {
            return false;
        }

        TileKind tile = _tiles[index];
        if (tile == TileKind.Void || tile == TileKind.Water)
        {
            return false;
        }

        PropKind prop = _props[index];
        return prop != PropKind.Tree && prop != PropKind.Rock;
    }

    /// <summary>
    /// 把存档里的改动盖回去（先生成再覆盖）。
    /// 逐条按顺序应用；越界条目静默跳过（存档可能来自尺寸不同的地图）。
    /// </summary>
    public void ApplyOverrides(IEnumerable<TileOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        foreach (TileOverride? item in overrides)
        {
            if (item is null || !InBounds(item.X, item.Y))
            {
                continue;
            }

            SetTile(item.X, item.Y, item.Tile);
            SetProp(item.X, item.Y, item.Prop);
        }
    }

    /// <summary>把二维坐标折成一维下标；越界返回 -1。</summary>
    private int IndexOf(int x, int y) => InBounds(x, y) ? (y * Width) + x : -1;
}
