using System.Collections.Generic;
using System.Text.Json;
using AsahiMono.Core.World;
using Godot;

namespace AsahiMono;

/// <summary>
/// 把"手工摆好的参考岛"（<c>Assets/maps/refblock.json</c>，由 <c>tools/art/place_deco.py</c> 产出、
/// 编辑器里的 <c>Maps/Refblock.tscn</c> 用的也是同一份数据）读成引擎的 <see cref="WorldMap"/>。
///
/// 为什么要这么一条路：在此之前，"你看到的图"（Refblock 场景，GDScript 把它逐格写进 TileMapLayer）
/// 和"游戏里跑的图"（<c>Main.tscn</c> → 随机生成世界 → IsoWorldRenderer）是两套东西，
/// 于是素材的观感在两边完全不一样。走这里之后，游戏渲染器读同一份 json，
/// 游戏里看到的就是编辑器里那张地皮 + 水。
///
/// json 结构（version 1）：
/// <code>
/// { "width": 13, "height": 13,
///   "cells": [[1,1,5,...], ...],   // cells[y][x]，值 = TileKind 的整数值（0 Void / 1 Grass / 4 Water / 5 Sand …）
///   "walls": [[false,...], ...],   // 侧壁标记，本类暂不消费（侧壁由 IsoWorldRenderer 按"邻居是虚空"自己算）
///   "deco":  [{"name":"grass_tuft_a","x":3.743,"y":3.389,"variant":2}, ...] }
/// </code>
/// 装饰（deco）不在这里进 <see cref="WorldMap"/>（<c>Core</c> 层不认识精灵），
/// 由 <see cref="TryLoadDeco"/> 单独读出、交给 <see cref="IsoWorldRenderer"/> 画。
/// </summary>
public static class RefblockMapLoader
{
    /// <summary>
    /// 一条装饰摆放。<see cref="Stem"/> 是图集里的精灵名（<c>deco_tileset.tres</c> 的
    /// <c>metadata/stem</c>），<see cref="X"/>/<see cref="Y"/> 是**浮点**格坐标（可以落在格内任意位置，
    /// 这是散点摆放的关键，不要取整）。<see cref="Variant"/> 只是原生成器的抖动种子，渲染不消费。
    /// </summary>
    public readonly record struct DecoPlacement(string Stem, float X, float Y, int Variant);

    /// <summary>
    /// 读 <c>deco[]</c> 并追加进 <paramref name="into"/>。返回是否读到了至少一条。
    /// 缺 name / 缺坐标的条目直接跳过（地图是手改的，宁可少画一个也不要整张图不显示）。
    /// </summary>
    public static bool TryLoadDeco(string resourcePath, List<DecoPlacement> into)
    {
        if (!Godot.FileAccess.FileExists(resourcePath))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(resourcePath));
            if (!document.RootElement.TryGetProperty("deco", out JsonElement deco)
                || deco.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            int read = 0;
            foreach (JsonElement item in deco.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("name", out JsonElement nameValue)
                    || nameValue.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("x", out JsonElement xValue)
                    || !item.TryGetProperty("y", out JsonElement yValue)
                    || !xValue.TryGetDouble(out double x)
                    || !yValue.TryGetDouble(out double y))
                {
                    continue;
                }

                string? stem = nameValue.GetString();
                if (string.IsNullOrEmpty(stem))
                {
                    continue;
                }

                int variant = item.TryGetProperty("variant", out JsonElement variantValue)
                              && variantValue.TryGetInt32(out int v) ? v : 0;
                into.Add(new DecoPlacement(stem!, (float)x, (float)y, variant));
                read++;
            }

            return read > 0;
        }
        catch (JsonException ex)
        {
            GD.PushWarning($"[map] {resourcePath} deco is not valid JSON ({ex.Message})");
            return false;
        }
    }

    /// <summary>
    /// 读 <paramref name="resourcePath"/>（形如 <c>res://Assets/maps/refblock.json</c>）并填充
    /// <paramref name="map"/>。成功返回 true 并打印一行日志；失败打印告警、返回 false，
    /// 调用方应回退到随机世界（绝不能因为缺图就黑屏）。
    /// </summary>
    public static bool TryLoad(string resourcePath, WorldMap map)
    {
        if (!Godot.FileAccess.FileExists(resourcePath))
        {
            GD.PushWarning($"[map] {resourcePath} not found; falling back to generated world");
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(resourcePath));
            JsonElement root = document.RootElement;

            int width = root.TryGetProperty("width", out JsonElement w) ? w.GetInt32() : 0;
            int height = root.TryGetProperty("height", out JsonElement h) ? h.GetInt32() : 0;
            if (width != map.Width || height != map.Height)
            {
                GD.PushWarning($"[map] {resourcePath} is {width}x{height} but the world is {map.Width}x{map.Height}; skipped");
                return false;
            }

            if (!root.TryGetProperty("cells", out JsonElement cells) || cells.ValueKind != JsonValueKind.Array)
            {
                GD.PushWarning($"[map] {resourcePath} has no 'cells' array; skipped");
                return false;
            }

            int water = 0;
            int y = 0;
            foreach (JsonElement row in cells.EnumerateArray())
            {
                int x = 0;
                foreach (JsonElement cell in row.EnumerateArray())
                {
                    if (x < map.Width && y < map.Height)
                    {
                        var kind = (TileKind)Mathf.Clamp(cell.GetInt32(), 0, (int)TileKind.StoneFloor);
                        map.SetTile(x, y, kind);
                        if (kind == TileKind.Water)
                        {
                            water++;
                        }
                    }

                    x++;
                }

                y++;
            }

            GD.Print($"[map] refblock island loaded: {width}x{height}, {water} water cell(s) from {resourcePath}");
            return true;
        }
        catch (JsonException ex)
        {
            GD.PushWarning($"[map] {resourcePath} is not valid JSON ({ex.Message}); skipped");
            return false;
        }
    }

    /// <summary>
    /// 只读 json 头部的 <c>width</c>/<c>height</c>，供调用方先建出尺寸正确的 <see cref="WorldMap"/>。
    /// </summary>
    public static bool TryReadSize(string resourcePath, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (!Godot.FileAccess.FileExists(resourcePath))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(resourcePath));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("width", out JsonElement w) || !root.TryGetProperty("height", out JsonElement h))
            {
                return false;
            }

            width = w.GetInt32();
            height = h.GetInt32();
            return width > 0 && height > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 从地图中心向外螺旋找第一个可站的格子，作为出生点（参考岛中间通常是草地）。
    /// </summary>
    public static (int X, int Y) FindSpawn(WorldMap map)
    {
        int cx = map.Width / 2;
        int cy = map.Height / 2;
        int maxRing = Mathf.Max(map.Width, map.Height);

        for (int ring = 0; ring < maxRing; ring++)
        {
            for (int dy = -ring; dy <= ring; dy++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (Mathf.Abs(dx) != ring && Mathf.Abs(dy) != ring)
                    {
                        continue;
                    }

                    int x = cx + dx;
                    int y2 = cy + dy;
                    if (map.IsWalkable(x, y2))
                    {
                        return (x, y2);
                    }
                }
            }
        }

        return (cx, cy);
    }
}
