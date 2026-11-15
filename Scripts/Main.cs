using AsahiMono.Core;
using Godot;

/// <summary>
/// M0 smoke script: owns the scene and proves the Godot -> Adapters -> Game.Core
/// direction of the architecture actually compiles and runs.
/// No gameplay lives here; this file only forwards and draws.
/// </summary>
public partial class Main : Control
{
    private readonly WorldClock _clock = new WorldClock();

    public override void _Ready()
    {
        GD.Print($"Hello AsahiMono (core clock: {_clock})");

        Label label = GetNodeOrNull<Label>("Label");
        if (label != null)
        {
            label.Text = "Hello AsahiMono";
        }
    }
}
