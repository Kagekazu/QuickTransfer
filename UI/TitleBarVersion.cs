using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace QuickTransfer;

internal static unsafe partial class TitleBarVersion
{
    private const int DefaultPosOffset = 0x68;
    private const int DefaultSizeOffset = 0x70;

    private static int posOffset = DefaultPosOffset;
    private static int sizeOffset = DefaultSizeOffset;
    private static bool offsetsCalibrated;

    public static void DrawFromContext(int customTitleBarButtonCount, bool showAdditionalOptionsButton, string windowName)
    {
        Vector2 windowPos = ImGui.GetWindowPos();
        Vector2 windowSize = ImGui.GetWindowSize();
        if (windowSize.X <= 0f || windowSize.Y <= 0f)
            return;

        TryCalibrateOffsets(windowName, windowPos, windowSize);
        DrawAt(windowPos, windowSize, customTitleBarButtonCount, showAdditionalOptionsButton);
    }

    public static void DrawFromWindowLookup(int customTitleBarButtonCount, bool showAdditionalOptionsButton, string windowName)
    {
        if (!TryResolveWindowRect(windowName, out Vector2 windowPos, out Vector2 windowSize))
            return;

        DrawAt(windowPos, windowSize, customTitleBarButtonCount, showAdditionalOptionsButton);
    }

    public static void ClearCache()
    {
        offsetsCalibrated = false;
        posOffset = DefaultPosOffset;
        sizeOffset = DefaultSizeOffset;
    }

    private static void DrawAt(
        Vector2 windowPos,
        Vector2 windowSize,
        int customTitleBarButtonCount,
        bool showAdditionalOptionsButton)
    {
        string text = GetVersionLabel();
        if (string.IsNullOrEmpty(text))
            return;

        Vector2 textSize = ImGui.CalcTextSize(text);
        ImGuiStylePtr style = ImGui.GetStyle();
        float buttonSize = ImGui.GetFontSize();
        float padRight = style.FramePadding.X + buttonSize + style.ItemInnerSpacing.X;

        if (style.WindowMenuButtonPosition == ImGuiDir.Right)
            padRight += buttonSize + style.ItemInnerSpacing.X;

        int extraButtons = customTitleBarButtonCount + (showAdditionalOptionsButton ? 1 : 0);
        padRight += extraButtons * (buttonSize + style.ItemInnerSpacing.X);
        padRight += style.ItemInnerSpacing.X;

        Vector2 position = new(
            windowPos.X + windowSize.X - padRight - textSize.X,
            windowPos.Y + style.FramePadding.Y);

        ImGui.GetForegroundDrawList().AddText(
            ImGui.GetFont(),
            ImGui.GetFontSize(),
            position,
            ImGui.ColorConvertFloat4ToU32(new(0.75f, 0.75f, 0.75f, 1f)),
            text);
    }

    private static void TryCalibrateOffsets(string windowName, Vector2 expectedPos, Vector2 expectedSize)
    {
        if (offsetsCalibrated || string.IsNullOrWhiteSpace(windowName))
            return;

        ImGuiWindow* window = FindWindowByName(windowName);
        if (window == null)
            return;

        byte* basePtr = (byte*)window;
        for(int offset = 0; offset < 512; offset += 4)
        {
            Vector2 candidatePos = ReadVector2(basePtr + offset);
            if (Vector2.Distance(candidatePos, expectedPos) > 1f)
                continue;

            Vector2 candidateSize = ReadVector2(basePtr + offset + 8);
            if (MathF.Abs(candidateSize.X - expectedSize.X) > 2f ||
                MathF.Abs(candidateSize.Y - expectedSize.Y) > 2f)
                continue;

            posOffset = offset;
            sizeOffset = offset + 8;
            offsetsCalibrated = true;
            return;
        }
    }

    private static bool TryResolveWindowRect(string windowName, out Vector2 windowPos, out Vector2 windowSize)
    {
        windowPos = default;
        windowSize = default;

        if (string.IsNullOrWhiteSpace(windowName))
            return false;

        ImGuiWindow* window = FindWindowByName(windowName);
        if (window == null)
            return false;

        byte* basePtr = (byte*)window;
        windowPos = ReadVector2(basePtr + posOffset);
        windowSize = ReadVector2(basePtr + sizeOffset);
        return windowSize.X > 0f && windowSize.Y > 0f;
    }

    private static Vector2 ReadVector2(byte* ptr) =>
        new(*(float*)ptr, *(float*)(ptr + 4));

    private static ImGuiWindow* FindWindowByName(string windowName)
    {
        nint namePtr = Marshal.StringToCoTaskMemUTF8(windowName);
        try
        {
            return (ImGuiWindow*)igFindWindowByName((byte*)namePtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    [LibraryImport("cimgui")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint igFindWindowByName(byte* name);

    private static string GetVersionLabel()
    {
        Version? manifestVersion = Svc.PluginInterface.Manifest.AssemblyVersion;
        if (manifestVersion != null)
            return "v" + FormatVersion(manifestVersion);

        Version? assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
        return assemblyVersion != null ? "v" + FormatVersion(assemblyVersion) : "v?.?.?.?";
    }

    private static string FormatVersion(Version version) =>
        version.Revision >= 0 ? version.ToString(4) : version.ToString(3);

    private struct ImGuiWindow;
}
