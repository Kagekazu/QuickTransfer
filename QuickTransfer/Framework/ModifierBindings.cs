using Dalamud.Game.ClientState.Keys;
namespace QuickTransfer.Framework;

public static class ModifierBindings
{
    private const int VkMiddleButton = 0x04;
    private const int VkXButton1 = 0x05;
    private const int VkXButton2 = 0x06;
    private const byte MiddleButtonEventMask = 0x04;

    public static readonly VirtualKey[] AllowedModifiers =
    [
        VirtualKey.SHIFT,
        VirtualKey.CONTROL,
        VirtualKey.MENU
    ];

    public static string GetDisplayName(VirtualKey key) => key switch
    {
        VirtualKey.SHIFT => "Shift",
        VirtualKey.CONTROL => "Ctrl",
        VirtualKey.MENU => "Alt",
        _ => key.ToString()
    };

    public static VirtualKey SanitizeModifier(VirtualKey key) =>
        Array.IndexOf(AllowedModifiers, key) >= 0 ? key : VirtualKey.SHIFT;

    public static bool IsMiddleClickEdge(
        bool mDown,
        bool prevM,
        bool x1Down,
        bool prevX1,
        bool x2Down,
        bool prevX2,
        Configuration configuration)
    {
        if (configuration.MiddleClickUseMButton && mDown && !prevM)
        {
            return true;
        }

        if (configuration.MiddleClickUseXButton1 && x1Down && !prevX1)
        {
            return true;
        }

        return configuration.MiddleClickUseXButton2 && x2Down && !prevX2;
    }

    public static bool IsConfiguredMiddleClickDown(Configuration configuration)
    {
        if (configuration.MiddleClickUseMButton && CursorHoverHelpers.IsMouseButtonDown(VkMiddleButton))
        {
            return true;
        }

        if (configuration.MiddleClickUseXButton1 && CursorHoverHelpers.IsMouseButtonDown(VkXButton1))
        {
            return true;
        }

        return configuration.MiddleClickUseXButton2 && CursorHoverHelpers.IsMouseButtonDown(VkXButton2);
    }

    public static bool IsMiddleClickEventMask(byte mouseButtonId, byte dragDropMouseButtonId, Configuration configuration) =>
        configuration.MiddleClickUseMButton &&
        ((mouseButtonId & MiddleButtonEventMask) != 0 || (dragDropMouseButtonId & MiddleButtonEventMask) != 0);

    public static bool IsMiddleClickPressed(Configuration configuration, byte mouseButtonId, byte dragDropMouseButtonId, bool? keyStateMiddleDown) =>
        IsConfiguredMiddleClickDown(configuration) ||
        IsMiddleClickEventMask(mouseButtonId, dragDropMouseButtonId, configuration) ||
        keyStateMiddleDown == true;

    public static string FormatRightClickBinding(VirtualKey modifier) => $"{GetDisplayName(modifier)} + Right-click";

    public static bool IsMiddleClickConfigured(Configuration configuration) =>
        configuration.EnableMiddleClickSort &&
        (configuration.MiddleClickUseMButton || configuration.MiddleClickUseXButton1 || configuration.MiddleClickUseXButton2);

    public static string FormatMiddleClickBinding(Configuration configuration)
    {
        var parts = new List<string>(3);
        if (configuration.MiddleClickUseMButton)
        {
            parts.Add("Middle-click");
        }

        if (configuration.MiddleClickUseXButton1)
        {
            parts.Add("Mouse 4");
        }

        if (configuration.MiddleClickUseXButton2)
        {
            parts.Add("Mouse 5");
        }

        return parts.Count == 0 ? "(disabled)" : string.Join(" / ", parts);
    }

    public static bool HasModifierConflict(Configuration configuration, out string conflict)
    {
        conflict = string.Empty;
        var bindings = new List<(string Label, VirtualKey Key)>(3);
        if (configuration.EnableShiftQuickTransfer)
        {
            bindings.Add(("Quick transfer", configuration.ShiftActionModifier));
        }

        if (configuration.EnableCtrlArmoury)
        {
            bindings.Add(("Armoury actions", configuration.CtrlActionModifier));
        }

        if (configuration.EnableAltSplit)
        {
            bindings.Add(("Split", configuration.AltActionModifier));
        }

        for (var i = 0; i < bindings.Count; i++)
        {
            for (var j = i + 1; j < bindings.Count; j++)
            {
                if (bindings[i].Key == bindings[j].Key)
                {
                    conflict = $"{bindings[i].Label} and {bindings[j].Label} both use {GetDisplayName(bindings[i].Key)}.";
                    return true;
                }
            }
        }

        return false;
    }
}
