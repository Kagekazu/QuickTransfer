using System.Numerics;

namespace QuickTransfer;

/// <summary>
/// Window theme sampled from images/icon.png: deep navy background, gold ring and arrows,
/// bright gold from the crystals for highlights, and ivory for text.
/// </summary>
internal static class QuickTransferTheme
{
    // Icon palette.
    public static readonly Vector4 Navy = Rgb(0x0F, 0x18, 0x27);
    public static readonly Vector4 NavyRaised = Rgb(0x18, 0x24, 0x3A);
    public static readonly Vector4 Gold = Rgb(0xD0, 0x9E, 0x43);
    public static readonly Vector4 GoldBright = Rgb(0xF1, 0xCA, 0x6E);
    public static readonly Vector4 GoldDark = Rgb(0x93, 0x6E, 0x30);
    public static readonly Vector4 Ivory = Rgb(0xF5, 0xF3, 0xF0);
    public static readonly Vector4 Slate = Rgb(0x8E, 0x97, 0xA8);

    // Buttons, inputs, sliders and checkboxes: a step lighter than the window so they stand out.
    public static readonly Vector4 Control = Rgb(0x2B, 0x3D, 0x5E);
    public static readonly Vector4 ControlHover = Rgb(0x36, 0x4C, 0x73);
    public static readonly Vector4 ControlActive = Rgb(0x41, 0x5A, 0x86);

    // Section headings and the bindings column in the Controls table.
    public static readonly Vector4 Heading = new(0.85f, 0.72f, 0.35f, 1f);

    private static readonly (ImGuiCol Col, Vector4 Color)[] Colors =
    [
        (ImGuiCol.Text, Ivory),
        (ImGuiCol.TextDisabled, Slate),
        (ImGuiCol.WindowBg, WithAlpha(Navy, 0.97f)),
        (ImGuiCol.ChildBg, Vector4.Zero),
        (ImGuiCol.PopupBg, WithAlpha(Navy, 0.98f)),
        (ImGuiCol.Border, WithAlpha(GoldDark, 0.55f)),
        (ImGuiCol.BorderShadow, Vector4.Zero),

        (ImGuiCol.FrameBg, Control),
        (ImGuiCol.FrameBgHovered, ControlHover),
        (ImGuiCol.FrameBgActive, ControlActive),

        (ImGuiCol.TitleBg, Navy),
        (ImGuiCol.TitleBgActive, NavyRaised),
        (ImGuiCol.TitleBgCollapsed, WithAlpha(Navy, 0.85f)),
        (ImGuiCol.MenuBarBg, NavyRaised),

        (ImGuiCol.ScrollbarBg, WithAlpha(Navy, 0.60f)),
        (ImGuiCol.ScrollbarGrab, WithAlpha(GoldDark, 0.55f)),
        (ImGuiCol.ScrollbarGrabHovered, WithAlpha(Gold, 0.75f)),
        (ImGuiCol.ScrollbarGrabActive, Gold),

        (ImGuiCol.CheckMark, GoldBright),
        (ImGuiCol.SliderGrab, Gold),
        (ImGuiCol.SliderGrabActive, GoldBright),

        (ImGuiCol.Button, Control),
        (ImGuiCol.ButtonHovered, ControlHover),
        (ImGuiCol.ButtonActive, WithAlpha(GoldDark, 0.90f)),

        // Collapsing headers, tree nodes, selectables, and the active page button.
        (ImGuiCol.Header, WithAlpha(GoldDark, 0.35f)),
        (ImGuiCol.HeaderHovered, WithAlpha(Gold, 0.40f)),
        (ImGuiCol.HeaderActive, WithAlpha(GoldDark, 0.90f)),

        (ImGuiCol.Tab, NavyRaised),
        (ImGuiCol.TabHovered, ControlHover),
        (ImGuiCol.TabSelected, WithAlpha(GoldDark, 0.90f)),
        (ImGuiCol.TabDimmed, NavyRaised),
        (ImGuiCol.TabDimmedSelected, WithAlpha(GoldDark, 0.60f)),

        (ImGuiCol.Separator, WithAlpha(GoldDark, 0.50f)),
        (ImGuiCol.SeparatorHovered, Gold),
        (ImGuiCol.SeparatorActive, GoldBright),

        (ImGuiCol.ResizeGrip, WithAlpha(GoldDark, 0.30f)),
        (ImGuiCol.ResizeGripHovered, WithAlpha(Gold, 0.65f)),
        (ImGuiCol.ResizeGripActive, GoldBright),

        (ImGuiCol.PlotLines, Gold),
        (ImGuiCol.PlotLinesHovered, GoldBright),
        (ImGuiCol.PlotHistogram, Gold),
        (ImGuiCol.PlotHistogramHovered, GoldBright),

        (ImGuiCol.TableHeaderBg, NavyRaised),
        (ImGuiCol.TableBorderStrong, WithAlpha(GoldDark, 0.60f)),
        (ImGuiCol.TableBorderLight, WithAlpha(GoldDark, 0.30f)),
        (ImGuiCol.TableRowBg, Vector4.Zero),
        (ImGuiCol.TableRowBgAlt, WithAlpha(NavyRaised, 0.45f)),

        (ImGuiCol.TextSelectedBg, WithAlpha(Gold, 0.35f)),
        (ImGuiCol.DragDropTarget, GoldBright),
        (ImGuiCol.ModalWindowDimBg, WithAlpha(Navy, 0.60f)),
    ];

    private const int PushedVars = 6;

    /// <summary>Push the theme. Every call must be matched by <see cref="Pop"/>.</summary>
    public static void Push()
    {
        foreach ((ImGuiCol col, Vector4 color) in Colors)
        {
            ImGui.PushStyleColor(col, color);
        }

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 4f);
    }

    public static void Pop()
    {
        ImGui.PopStyleVar(PushedVars);
        ImGui.PopStyleColor(Colors.Length);
    }

    private static Vector4 Rgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, 1f);

    private static Vector4 WithAlpha(Vector4 color, float alpha) => color with { W = alpha };
}
