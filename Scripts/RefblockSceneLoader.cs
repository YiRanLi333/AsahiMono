using System;
using System.Collections.Generic;
using System.IO;
using AsahiMono.Core.World;
using Godot;

namespace AsahiMono;

/// <summary>
/// 把**编辑器里手画的那张图**（<c>res://Maps/Refblock.tscn</c>）直接读成引擎的
/// <see cref="WorldMap"/> + 装饰摆放表，供 <see cref="GameRoot"/> 在
/// <c>config.json</c> 的 <c>mapSource = "refblock_scene"</c> 时使用。
///
/// 为什么要有这条路：<see cref="RefblockMapLoader"/> 读的是 <c>Assets/maps/refblock.json</c>，
/// 而用户在编辑器里刷的是 <c>Maps/Refblock.tscn</c> 的 <c>TileMapLayer</c>。两者一旦分叉，
/// 就出现"编辑器一张图、游戏另一张图"。这里不读 json、也不读 <c>Cliff</c>/<c>Cliff2</c>
/// （那是美术用的装饰性侧壁层），只读：
/// <list type="bullet">
///   <item><see cref="GroundLayerPath"/>（<c>Ground</c>）：地块 kind，从 TileSet 的 source 反查
///   （<c>atlas_&lt;slug&gt;.png</c> 的文件名 → <see cref="TileKind"/>）；</item>
///   <item><c>YSort/Deco</c> / <c>Deco2</c> / <c>Deco3</c> / <c>Deco4</c>：装饰，从
///   <c>deco_tileset.tres</c> 的 <c>(图集列, 图集行) → stem</c> 反查（表由
///   <see cref="IsoWorldRenderer"/> 解析，同一份实现，不抄第二份）。</item>
/// </list>
/// 侧壁照旧由 <see cref="IsoWorldRenderer"/> 按"深度差"自己算，所以这里不碰 Cliff 层。
///
/// <b>要不要进树？不用。</b>Godot 4.5.2 里 <c>PackedScene.Instantiate()</c> 已经把
/// <c>tile_map_data</c> 反序列化进 TileMapLayer 的格子表，未进树的实例上
/// <c>GetUsedCells()</c> 就能读到 169 / 176 格（`TryLoad` 里第一次探测的结果会打进日志）。
/// 反而**不能**随手 <c>AddChild</c>：本类在 <see cref="GameRoot"/>._Ready() 里被调用，
/// 那时 SceneTree 正在挂 Main 场景，根节点 busy，<c>add_child()</c> 会直接报错。
/// 所以正常路径只读、读完 <c>Free()</c>；只有"读到 0 格"时才退回到进树兜底。
/// </summary>
public static class RefblockSceneLoader
{
    /// <summary>编辑器地图场景（用户刷地块的地方）。</summary>
    public const string ScenePath = "res://Maps/Refblock.tscn";

    /// <summary>地面层（地块 kind 的唯一来源）。</summary>
    private const string GroundLayerPath = "Ground";

    /// <summary>编辑器场景里那台相机：进树前必须关掉，否则会抢走游戏的 current camera。</summary>
    private const string CameraPath = "Camera2D";

    /// <summary>
    /// 装饰层，按"同一格堆叠深度"排列（与 <c>Maps/refblock_fill.gd</c> 灌格子时的顺序一致）。
    /// 一个 TileMapLayer 每格只能放一张瓦片，所以同格多件装饰被分到这四层里。
    /// </summary>
    private static readonly string[] DecoLayerPaths =
    {
        "YSort/Deco", "YSort/Deco2", "YSort/Deco3", "YSort/Deco4",
    };

    /// <summary>地面图集文件名前缀：<c>atlas_grass.png</c> → <c>grass</c>。</summary>
    private const string AtlasPrefix = "atlas_";

    /// <summary>
    /// 手放道具层：用户在编辑器里直接往 <c>YSort</c> 下拖 <see cref="Sprite2D"/> 的地方。
    /// 约定（用户 2026-11-17 定）：<c>position</c> = 该物件的**脚底中点**（world px，不是格坐标），
    /// <c>texture</c> = 散图 <c>res://Assets/tiles/iso/props/loose/&lt;stem&gt;.png</c>，
    /// stem = 贴图文件名去扩展名，**节点名无意义**（只用来保证唯一）。
    /// </summary>
    private const string PropsLayerPath = "YSort/Props";

    /// <summary>尺寸上限，与 <c>config.json</c> 里 worldWidth/worldHeight 的合法区间同值（防止误画一整片空地时炸内存）。</summary>
    private const int MaxSize = 512;

    /// <summary>读出来的结果：地图 + 装饰 + 一串对账用的计数（日志就照这些打）。</summary>
    public sealed class Result
    {
        /// <summary>地面层 used_rect 给出的地图（13×13 或用户改图后的任意尺寸）。</summary>
        public WorldMap Map { get; init; } = null!;

        /// <summary>
        /// 装饰摆放（浮点格坐标），顺序 = 绘制顺序：先 Deco 四层（整数格心），
        /// 再 <c>YSort/Props</c> 里手放的 Sprite2D（可能落在半格/格角，见 <see cref="PropsPlacements"/>）。
        /// </summary>
        public List<RefblockMapLoader.DecoPlacement> Deco { get; init; } = new();

        /// <summary>Ground 层实际读到的格子数。</summary>
        public int GroundCells { get; init; }

        /// <summary>四层装饰合计读到的格子数。</summary>
        public int DecoCells { get; init; }

        /// <summary>
        /// <c>YSort/Props</c> 下见到的 <see cref="Sprite2D"/> 个数（含隐藏、含没贴图的）。
        /// 与 <see cref="PropsPlacements"/> 的差 = 因为不可见 / 没贴图 / 贴图没有 ResourcePath 而跳过的数。
        /// </summary>
        public int PropsSprites { get; init; }

        /// <summary><c>YSort/Props</c> 成功转成装饰摆放（追加在 <see cref="Deco"/> 尾部）的条数。</summary>
        public int PropsPlacements { get; init; }

        /// <summary>装饰里 stem 查到的条数。</summary>
        public int DecoStemHits { get; init; }

        /// <summary>装饰里 stem 查不到的条数（图集里没这个格 → 跳过，不画）。</summary>
        public int DecoStemMisses { get; init; }

        /// <summary>地块里 slug 认不出、被按草地处理的**格子**数。</summary>
        public int UnknownSlugCells { get; init; }

        /// <summary>地块里 slug 认不出的 **source 种类**数（就是"未识别 slug 数"）。</summary>
        public int UnknownSlugSources { get; init; }
    }

    /// <summary>
    /// 读场景并建图。<paramref name="host"/> 必须是已在树里的节点（<see cref="GameRoot"/> 自己），
    /// 用它拿 <see cref="SceneTree"/> 来临时挂载场景实例。
    /// 失败（场景缺失 / 没有任何格子）返回 <c>null</c> 并打 warning，调用方应回退到生成世界。
    /// </summary>
    public static Result? TryLoad(Node host, string scenePath, int seed)
    {
        ArgumentNullException.ThrowIfNull(host);

        PackedScene? packed = GD.Load<PackedScene>(scenePath);
        if (packed is null)
        {
            GD.PushWarning($"[scene] cannot load {scenePath}; falling back to the generated world");
            return null;
        }

        Node instance = packed.Instantiate();
        if (instance is not Node2D root)
        {
            GD.PushWarning($"[scene] {scenePath} root is {instance.GetType().Name}, expected Node2D; skipped");
            instance.Free();
            return null;
        }

        // 隐藏整棵子树：TileMapLayer 是 CanvasItem，父节点 Visible=false 就够了，
        // 这样它在树里的那一帧不会被画出来（我们同一帧就 QueueFree）。
        root.Visible = false;

        // 场景自带一台 Camera2D；让它 enabled 会在进树时变成 current camera，
        // 抢掉 GameRoot 稍后 MakeCurrent 的那台。进树前关掉最省事。
        if (root.GetNodeOrNull<Camera2D>(CameraPath) is { } sceneCamera)
        {
            sceneCamera.Enabled = false;
        }

        // **实测结论（Godot 4.5.2）：不用进树就能读。**
        // `PackedScene.Instantiate()` 时 TileMapLayer 已经把 `tile_map_data` 反序列化进
        // 内部格子表，`GetUsedCells()` / `GetUsedRect()` 直接就有数（本图 169 / 176 格）。
        // 进树反而会撞上"Parent node is busy setting up children"——因为这段代码跑在
        // GameRoot._Ready() 里，而那时 SceneTree 正在挂 Main 场景。所以主路不 AddChild。
        // 下面留一个"0 格才进树"的兜底，只为引擎行为若变（或资源还没解析完）时不自断。
        TileMapLayer? ground = root.GetNodeOrNull<TileMapLayer>(GroundLayerPath);
        int cellsBeforeTree = ground?.GetUsedCells().Count ?? 0;

        bool attached = false;
        if (cellsBeforeTree == 0 && host.GetTree() is { } tree)
        {
            GD.PushWarning("[scene] TileMapLayer reported 0 cell(s) outside the tree; retrying inside the SceneTree");
            tree.Root.AddChild(root);
            attached = true;
        }

        try
        {
            return Read(root, scenePath, seed, cellsBeforeTree, attached);
        }
        finally
        {
            // 读完立刻丢掉：这张图只是"数据源"，渲染由 IsoWorldRenderer 负责。
            if (attached)
            {
                root.QueueFree();
            }
            else
            {
                root.Free();
            }
        }
    }

    private static Result? Read(Node2D root, string scenePath, int seed, int cellsBeforeTree, bool attached)
    {
        TileMapLayer? ground = root.GetNodeOrNull<TileMapLayer>(GroundLayerPath);
        if (ground is null)
        {
            GD.PushWarning($"[scene] {scenePath} has no '{GroundLayerPath}' TileMapLayer; skipped");
            return null;
        }

        Vector2I[] groundCells = ToArray(ground.GetUsedCells());
        Rect2I groundUsedRect = ground.GetUsedRect();
        if (groundCells.Length == 0)
        {
            GD.PushWarning($"[scene] '{GroundLayerPath}' has 0 cell(s) (cells before AddChild {cellsBeforeTree}); skipped");
            return null;
        }

        // 装饰层先全部取出来，尺寸用"地面 ∪ 装饰"的包围盒：
        // 用户把某个灌木刷到地块外面时也不该丢（地面 used_rect 仍是权威尺寸，日志分开打）。
        var decoLayers = new List<(string Path, TileMapLayer? Layer, Vector2I[] Cells)>();
        Rect2I? decoRect = null;
        foreach (string path in DecoLayerPaths)
        {
            TileMapLayer? layer = root.GetNodeOrNull<TileMapLayer>(path);
            Vector2I[] cells = layer is null ? System.Array.Empty<Vector2I>() : ToArray(layer.GetUsedCells());
            decoLayers.Add((path, layer, cells));
            if (cells.Length == 0)
            {
                continue;
            }

            Rect2I used = layer!.GetUsedRect();
            decoRect = decoRect is null ? used : Union(decoRect.Value, used);
        }

        Rect2I bounds = decoRect is null ? groundUsedRect : Union(groundUsedRect, decoRect.Value);
        if (bounds.Size.X <= 0 || bounds.Size.Y <= 0)
        {
            GD.PushWarning($"[scene] {scenePath} used_rect {groundUsedRect} is empty; skipped");
            return null;
        }

        if (bounds.Size.X > MaxSize || bounds.Size.Y > MaxSize)
        {
            GD.PushWarning($"[scene] {scenePath} is {bounds.Size.X}x{bounds.Size.Y}, larger than {MaxSize}; skipped");
            return null;
        }

        var map = new WorldMap(bounds.Size.X, bounds.Size.Y, seed);
        var origin = bounds.Position;

        // ---- 地面：source id → TileSet 的图集贴图文件名 → slug → TileKind ----
        var kindBySource = new Dictionary<int, TileKind>();
        var unknownBySource = new Dictionary<int, (string Slug, int Cells)>();
        var kindCount = new int[(int)TileKind.StoneFloor + 1];
        TileSet? groundTileSet = ground.TileSet;

        foreach (Vector2I cell in groundCells)
        {
            if (cell.X < bounds.Position.X || cell.Y < bounds.Position.Y
                || cell.X >= bounds.End.X || cell.Y >= bounds.End.Y)
            {
                continue;
            }

            int sourceId = ground.GetCellSourceId(cell);
            if (!kindBySource.TryGetValue(sourceId, out TileKind kind))
            {
                string slug = SlugForSource(groundTileSet, sourceId);
                if (slug.Length > 0 && IsoWorldRenderer.TryKindFromGroundSlug(slug, out kind))
                {
                    GD.Print($"[scene] Ground source {sourceId} = {slug} -> {kind}");
                }
                else
                {
                    // 认不出就按草地处理（绝不崩、绝不黑格），但一定要留痕：
                    // 美术新加一张图集却没登记 slug 时，这行日志是唯一的线索。
                    kind = TileKind.Grass;
                    unknownBySource[sourceId] = (slug.Length > 0 ? slug : $"source {sourceId} (no atlas texture)", 0);
                    GD.PushWarning($"[scene] Ground source {sourceId} slug '{slug}' is not a TileKind; drawing it as {kind}");
                }

                kindBySource[sourceId] = kind;
            }

            if (unknownBySource.TryGetValue(sourceId, out (string Slug, int Cells) seen))
            {
                unknownBySource[sourceId] = (seen.Slug, seen.Cells + 1);
            }

            map.SetTile(cell.X - origin.X, cell.Y - origin.Y, kind);
            kindCount[(int)kind]++;
        }

        // ---- 装饰：(图集列, 图集行) → stem ----
        Dictionary<Vector2I, string> stemByCoords = BuildStemTableByCoords(out int decoCellPx);
        var deco = new List<RefblockMapLoader.DecoPlacement>();
        int decoHits = 0;
        int decoMisses = 0;
        var missingCoords = new List<Vector2I>();
        var perLayer = new List<string>();
        int sourceMismatch = 0;

        foreach ((string path, TileMapLayer? layer, Vector2I[] cells) in decoLayers)
        {
            if (layer is null)
            {
                if (cells.Length == 0)
                {
                    GD.PushWarning($"[scene] deco layer '{path}' not found in {scenePath}");
                }

                continue;
            }

            int expectedSource = layer.TileSet is { } set && set.GetSourceCount() > 0 ? set.GetSourceId(0) : 0;
            int layerHits = 0;
            foreach (Vector2I cell in cells)
            {
                int sourceId = layer.GetCellSourceId(cell);
                if (sourceId != expectedSource)
                {
                    sourceMismatch++;
                }

                Vector2I atlasCoords = layer.GetCellAtlasCoords(cell);
                if (stemByCoords.TryGetValue(atlasCoords, out string? stem))
                {
                    deco.Add(new RefblockMapLoader.DecoPlacement(stem, cell.X - origin.X, cell.Y - origin.Y, 0));
                    layerHits++;
                    decoHits++;
                    continue;
                }

                decoMisses++;
                if (!missingCoords.Contains(atlasCoords))
                {
                    missingCoords.Add(atlasCoords);
                }
            }

            perLayer.Add($"{path[(path.LastIndexOf('/') + 1)..]} {layerHits}/{cells.Length}");
        }

        // ---- 手放的道具（YSort/Props 下的 Sprite2D）：position = 脚底中点，world px ----
        // 追加在 deco 层**之后**（列表尾巴）。注意：绘制顺序并不等于列表顺序 ——
        // IsoWorldRenderer._Draw 会把全部装饰按 depth = X + Y 再排一次（同 depth 按 Y），
        // 所以"手放的一定盖在图集装饰上"不成立，遮挡完全由所在格子的前后关系决定（这是对的）。
        int propSprites = 0;
        int propPlacements = 0;
        int propSkipped = 0;
        if (root.GetNodeOrNull<Node2D>(PropsLayerPath) is { } propsNode)
        {
            foreach (Node child in propsNode.GetChildren())
            {
                if (child is not Sprite2D sprite)
                {
                    continue;
                }

                propSprites++;
                if (!sprite.Visible || sprite.Texture is null)
                {
                    propSkipped++;
                    continue;
                }

                string texturePath = sprite.Texture.ResourcePath;
                string stem = string.IsNullOrEmpty(texturePath)
                    ? string.Empty
                    : Path.GetFileNameWithoutExtension(texturePath);
                if (stem.Length == 0)
                {
                    // 内嵌贴图（ResourcePath 为空）/ 名字取不出来 → 拿不到 stem，跳过并计数。
                    propSkipped++;
                    continue;
                }

                // 绘制时是 foot = GridToScreen(x, y) + DecoFootOffset，所以反算要先减掉同一个偏移；
                // 偏移常量直接从 IsoWorldRenderer 取，这里不另写 (0, 6)。
                // Props 与 YSort 都是默认 position (0,0)，sprite.Position 就是 world px。
                Vector2 gridPoint = IsoWorldRenderer.ScreenToGrid(sprite.Position - IsoWorldRenderer.DecoFootOffsetPx);
                deco.Add(new RefblockMapLoader.DecoPlacement(
                    stem, gridPoint.X - origin.X, gridPoint.Y - origin.Y, 0));
                propPlacements++;
            }
        }

        int unknownSlugCells = 0;
        foreach ((string _, int cellCount) in unknownBySource.Values)
        {
            unknownSlugCells += cellCount;
        }

        var kindParts = new List<string>();
        for (int i = 1; i < kindCount.Length; i++)
        {
            if (kindCount[i] > 0)
            {
                kindParts.Add($"{((TileKind)i).ToString().ToLowerInvariant().Replace("_", string.Empty)} {kindCount[i]}");
            }
        }

        GD.Print($"[scene] {scenePath} -> {map.Width}x{map.Height} (Ground used_rect {Format(groundUsedRect)}"
                  + $", deco rect {(decoRect is null ? "-" : Format(decoRect.Value))}, origin offset {origin}"
                  + $", cells read outside the tree {cellsBeforeTree}, entered tree {attached})");
        GD.Print($"[scene] Ground {groundCells.Length} cell(s), {kindBySource.Count} source(s)"
                  + (kindParts.Count > 0 ? $": {string.Join(", ", kindParts)}" : string.Empty)
                  + $" | unknown slug {unknownBySource.Count} source(s) / {unknownSlugCells} cell(s)");
        GD.Print($"[scene] deco {decoHits + decoMisses} cell(s) from {decoLayers.Count} layer(s)"
                  + $" ({string.Join(", ", perLayer)}), stem hit {decoHits}, miss {decoMisses}"
                  + $", atlas cell {decoCellPx}px, source-id mismatch {sourceMismatch}");
        GD.Print($"[scene] props: {propSprites} sprite(s) → {propPlacements} deco placement(s) (skipped {propSkipped})");

        if (unknownBySource.Count > 0)
        {
            var parts = new List<string>();
            foreach ((string slug, int cellTotal) in unknownBySource.Values)
            {
                parts.Add($"{slug} x{cellTotal}");
            }

            GD.PushWarning($"[scene] unrecognised Ground slug(s): {string.Join(", ", parts)} -> drawn as grass");
        }

        if (missingCoords.Count > 0)
        {
            var parts = new List<string>();
            foreach (Vector2I coords in missingCoords)
            {
                parts.Add($"{coords.X},{coords.Y}");
            }

            GD.PushWarning($"[scene] deco atlas coords with no stem metadata: {string.Join(" ", parts)}");
        }

        return new Result
        {
            Map = map,
            Deco = deco,
            GroundCells = groundCells.Length,
            DecoCells = decoHits + decoMisses,
            PropsSprites = propSprites,
            PropsPlacements = propPlacements,
            DecoStemHits = decoHits,
            DecoStemMisses = decoMisses,
            UnknownSlugCells = unknownSlugCells,
            UnknownSlugSources = unknownBySource.Count,
        };
    }

    /// <summary>
    /// 把 <see cref="IsoWorldRenderer"/> 解出来的 <c>stem → (列, 行)</c> 表**反转**成
    /// <c>(列, 行) → stem</c>：编辑器 TileMapLayer 存的是图集坐标，我们要的是精灵名。
    /// 表只有一份实现（见 <see cref="IsoWorldRenderer.ParseDecoStemTable"/>）。
    /// </summary>
    private static Dictionary<Vector2I, string> BuildStemTableByCoords(out int cellPx)
    {
        Dictionary<string, (int Col, int Row, Vector2 Origin)> stems =
            IsoWorldRenderer.ParseDecoStemTable(out cellPx, out bool viaResource);
        var reversed = new Dictionary<Vector2I, string>(stems.Count);
        foreach ((string stem, (int col, int row, Vector2 _)) in stems)
        {
            // 同一图集格重复登记 stem 时保留第一个（图集本来就是一格一个 stem）。
            reversed.TryAdd(new Vector2I(col, row), stem);
        }

        GD.Print($"[scene] deco stem table: {reversed.Count} cell(s) from "
                  + (viaResource ? "the TileSet resource" : IsoWorldRenderer.DecoTilesetPath));
        return reversed;
    }

    /// <summary>
    /// 某个 TileSet source 对应的磁盘图集 slug：<c>res://Assets/tiles/iso/ground/atlas_grass.png</c>
    /// → <c>grass</c>。拿不到贴图路径（内嵌贴图）时返回空串，调用方按"认不出"处理。
    /// </summary>
    private static string SlugForSource(TileSet? tileSet, int sourceId)
    {
        if (tileSet is null || tileSet.GetSource(sourceId) is not TileSetAtlasSource atlas || atlas.Texture is null)
        {
            return string.Empty;
        }

        string file = Path.GetFileNameWithoutExtension(atlas.Texture.ResourcePath);
        if (file.Length == 0)
        {
            return string.Empty;
        }

        return file.StartsWith(AtlasPrefix, StringComparison.OrdinalIgnoreCase)
            ? file[AtlasPrefix.Length..]
            : file;
    }

    private static Vector2I[] ToArray(Godot.Collections.Array<Vector2I> cells)
    {
        var copy = new Vector2I[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            copy[i] = cells[i];
        }

        return copy;
    }

    private static Rect2I Union(Rect2I a, Rect2I b)
    {
        int x0 = Math.Min(a.Position.X, b.Position.X);
        int y0 = Math.Min(a.Position.Y, b.Position.Y);
        int x1 = Math.Max(a.End.X, b.End.X);
        int y1 = Math.Max(a.End.Y, b.End.Y);
        return new Rect2I(x0, y0, x1 - x0, y1 - y0);
    }

    private static string Format(Rect2I rect)
        => $"P({rect.Position.X},{rect.Position.Y}) S({rect.Size.X},{rect.Size.Y})";
}
