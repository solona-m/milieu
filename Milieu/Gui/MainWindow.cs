using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Milieu.Gui;

public sealed class MainWindow : Window
{
    public MainWindow() : base($"Milieu (build {Plugin.BuildNumber})###MilieuMain") { }

    public override void Draw()
    {
        ImGui.TextUnformatted("No backdrop loaded.");
    }
}
