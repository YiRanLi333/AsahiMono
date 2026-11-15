using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using AsahiMono.Core.World;

namespace AsahiMono.Core.Save;

/// <summary>
/// 一份存档的全部内容。<see cref="Seed"/> + <see cref="Overrides"/> 就足以还原世界：
/// 先生成同 seed 的地图，再把玩家动过/建过/种过的格子盖回去。
/// </summary>
public sealed class GameState
{
    /// <summary>存档格式版本。以后加字段时靠它做迁移。</summary>
    public int Version { get; set; } = 1;

    /// <summary>世界种子（对应 <see cref="WorldGenOptions.Seed"/>）。</summary>
    public int Seed { get; set; }

    /// <summary>第几天，从 1 开始。</summary>
    public int Day { get; set; } = 1;

    /// <summary>当天已经过去的 tick（<c>WorldClock.TotalTicks</c>）。</summary>
    public long Ticks { get; set; }

    /// <summary>玩家所在格 x。</summary>
    public int PlayerX { get; set; }

    /// <summary>玩家所在格 y。</summary>
    public int PlayerY { get; set; }

    /// <summary>相对生成结果的改动列表（只存改过的格子）。</summary>
    public List<TileOverride> Overrides { get; set; } = new();
}

/// <summary>
/// 一格改动：地表和物件一起写（存档不区分"只改了物件"还是"只改了地表"）。
/// </summary>
/// <param name="X">格 x。</param>
/// <param name="Y">格 y。</param>
/// <param name="Tile">地表类型。</param>
/// <param name="Prop">物件。</param>
public sealed record TileOverride(int X, int Y, TileKind Tile, PropKind Prop);

/// <summary>
/// 存档读写。<c>System.Text.Json</c>，缩进可读，枚举写成名字（方便人眼和手改）。
///
/// 刻意<b>不</b>依赖真实时间或 <c>Guid</c>：同一份 <see cref="GameState"/> 序列化出来的字符串永远一样，
/// 方便 diff、做回归测试和"存档指纹"。
/// </summary>
public static class SaveSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>把存档变成可读的 JSON 文本。</summary>
    public static string Serialize(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return JsonSerializer.Serialize(state, Options);
    }

    /// <summary>
    /// 把 JSON 文本读回存档对象。
    /// 内容坏掉就抛 <see cref="JsonException"/>（不吞、不返回半成品）。
    /// </summary>
    /// <exception cref="JsonException">文本不是合法 JSON，或结构对不上。</exception>
    public static GameState Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException("save json is empty.");
        }

        GameState? state = JsonSerializer.Deserialize<GameState>(json, Options);
        if (state is null)
        {
            throw new JsonException("save json is 'null'.");
        }

        // JSON 里显式写了 "Overrides": null 时，把列表补回来，免得调用方到处判空。
        state.Overrides ??= new List<TileOverride>();
        return state;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            // 字段名保持 PascalCase：手改存档时最省眼力。
            // 枚举写成 "Grass" 而不是 1，坏值一眼能看出来。
            Converters = { new JsonStringEnumConverter() },
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        return options;
    }
}
