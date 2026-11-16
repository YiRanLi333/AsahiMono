using Godot;

namespace AsahiMono;

/// <summary>
/// 临时截图工具（不进提交）：实例化主场景，跑几十帧后把根视口存成 PNG。
/// 用法：godot --path . res://DevPreview/Shot.tscn
/// </summary>
public partial class Shot : Node
{
    private int _frames;
    private Player? _player;

    public override void _Ready()
    {
        var scene = GD.Load<PackedScene>("res://Scenes/Main.tscn");
        Node main = scene.Instantiate();
        AddChild(main);
        _player = main.GetNodeOrNull<Player>("Player");
    }

    public override void _Process(double delta)
    {
        _frames++;

        // 第 20 帧：让玩家斜着走 5 格，用来验证等距行走 + 遮挡 + 走路动画。
        if (_frames == 20 && _player is not null && _player.IsoMode)
        {
            Vector2 here = _player.IsoGrid;
            Vector2 target = IsoWorldRenderer.GridToScreen(here.X + 5f, here.Y + 5f);
            _player.SetMoveTarget(target);
            GD.Print($"[shot] walk from {here} toward +5,+5");
        }

        if (_frames == 45)
        {
            var image = GetViewport().GetTexture().GetImage();
            image.SavePng("E:/AsahiMono/tmp/shot-iso.png");
            GD.Print($"[shot] saved {image.GetWidth()}x{image.GetHeight()}");
        }

        if (_frames == 130)
        {
            var image = GetViewport().GetTexture().GetImage();
            image.SavePng("E:/AsahiMono/tmp/shot-iso-walk.png");
            GD.Print($"[shot] walking grid = {_player?.IsoGrid}, facing = {_player?.IsoFacing}, moving = {_player?.IsoMoving}");
            GetTree().Quit();
        }
    }
}
