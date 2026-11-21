using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using System.Diagnostics;
using System.Numerics;
namespace QuickTransfer;

public class PluginUI : Window
{
    private const string WindowId = "QuickTransfer";
    private const float ControlColumn = 230f;

    private static readonly Vector4 WarningColor = new(0.95f, 0.75f, 0.35f, 1f);

    private static readonly (string Open, string Result)[] QuickTransferTargets =
    [
        ("Vendor", "Sells the item"),
        ("Trade window", "Adds the item to the trade"),
        ("FC chest", "Deposits to the current tab, or takes the item out"),
        ("Retainer + saddlebag", "Inventory and retainer items to the saddlebag, saddlebag items to the retainer"),
        ("Retainer", "Between inventory and retainer"),
        ("Saddlebag", "Between inventory and saddlebag"),
        ("Nothing else", "Between inventory and Armoury Chest")
    ];

    private readonly Configuration config;

    public PluginUI(Configuration config)
        : base($"QuickTransfer v{Svc.PluginInterface.Manifest.AssemblyVersion}###{WindowId}", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.config = config;

        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Heart,
            ShowTooltip = () => ImGui.SetTooltip("Ko-fi (because sorting is thirsty work)"),
            Click = _ => Process.Start(new ProcessStartInfo
            {
                FileName = "https://ko-fi.com/kagekazu",
                UseShellExecute = true
            })
        });
    }

    public override void OnClose()
    {
        config.PersistIfDirty();
        base.OnClose();
    }

    public override void PreDraw() => QuickTransferTheme.Push();

    public override void PostDraw()
    {
        QuickTransferTheme.Pop();
        base.PostDraw();
    }

    public override void Draw()
    {
        Toggle("Enabled", config.Enabled, v => config.Enabled = v);
        ImGuiComponents.HelpMarker("When off, every QuickTransfer shortcut is ignored.");

        if (ModifierBindings.HasModifierConflict(config, out var conflict))
        {
            ImGui.TextColored(WarningColor, $"{conflict} Split wins, then Armoury, then Quick transfer.");
        }

        ImGui.Spacing();
        DrawShortcuts();
        DrawCompanyChest();
        DrawAdvanced();
    }

    private void DrawShortcuts()
    {
        if (!ImGui.CollapsingHeader("Shortcuts", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var open = OpenWindows.Current();

        DrawModifierRow(
            "Quick transfer",
            config.EnableShiftQuickTransfer,
            config.ShiftActionModifier,
            v => config.EnableShiftQuickTransfer = v,
            v => config.ShiftActionModifier = v);
        QuickTransferHelp();
        Indent(config.EnableShiftQuickTransfer, () =>
        {
            LiveLine(ContextMenuHandler.DescribeQuickTransfer(config, open));

            Toggle("Sell to vendors", config.EnableVendorQuickSell, v => config.EnableVendorQuickSell = v);
            ImGui.SameLine(ControlColumn);
            using (ImRaii.Disabled(!config.EnableVendorQuickSell))
            {
                Toggle("Confirm sell prompts", config.AutoConfirmVendorSell, v => config.AutoConfirmVendorSell = v);
            }

            Toggle("Leave the retainer market list alone", config.YieldQuickTransferOnRetainerSellList, v => config.YieldQuickTransferOnRetainerSellList = v);
            ImGuiComponents.HelpMarker("While you are listing items on the market board, right-clicks are for listing, so Quick transfer stays out of the way.");
        });

        DrawModifierRow(
            "Armoury in / out",
            config.EnableCtrlArmoury,
            config.CtrlActionModifier,
            v => config.EnableCtrlArmoury = v,
            v => config.CtrlActionModifier = v);
        ImGuiComponents.HelpMarker("Moves gear between your inventory and the Armoury Chest while a saddlebag, retainer or FC chest is open. With nothing else open, Quick transfer already does this.");
        Indent(config.EnableCtrlArmoury, () => LiveLine(ContextMenuHandler.DescribeArmoury(open)));

        DrawModifierRow(
            "Split stack in half",
            config.EnableAltSplit,
            config.AltActionModifier,
            v => config.EnableAltSplit = v,
            v => config.AltActionModifier = v);
        ImGuiComponents.HelpMarker("Splits a stack into two halves. On an FC chest item, takes half of it out.");

        Toggle("Sort under cursor", config.EnableMiddleClickSort, v => config.EnableMiddleClickSort = v);
        ImGui.SameLine(ControlColumn);
        using (ImRaii.Disabled(!config.EnableMiddleClickSort))
        {
            Toggle("Middle", config.MiddleClickUseMButton, v => config.MiddleClickUseMButton = v);
            ImGui.SameLine();
            Toggle("Mouse 4", config.MiddleClickUseXButton1, v => config.MiddleClickUseXButton1 = v);
            ImGui.SameLine();
            Toggle("Mouse 5", config.MiddleClickUseXButton2, v => config.MiddleClickUseXButton2 = v);
        }

        ImGuiComponents.HelpMarker("Click with one of these buttons over an inventory, saddlebag, retainer or armoury window to sort it. Mouse 4 / 5 are off by default because they are often Back / Forward or push-to-talk.");
        Indent(config.EnableMiddleClickSort, () =>
        {
            using (ImRaii.Disabled(!config.EnableCompanyChest))
            {
                Toggle("FC chest: tidy the current tab", config.EnableCompanyChestMiddleClickOrganize, v => config.EnableCompanyChestMiddleClickOrganize = v);
            }

            ImGuiComponents.HelpMarker("The FC chest has no Sort of its own. This stacks partial stacks, closes gaps and sorts the tab you are on, one move at a time.");
        });

        ImGui.Spacing();
        ImGui.Spacing();
        Toggle("Fill in quantity prompts for me", config.AutoConfirmCompanyChestQuantity, v => config.AutoConfirmCompanyChestQuantity = v);
        ImGuiComponents.HelpMarker("Fills in and confirms the \"How many?\" box for FC chest moves and stack splits. Trade amounts are always filled in.");
        ImGui.Spacing();
    }

    private void DrawCompanyChest()
    {
        if (!ImGui.CollapsingHeader("FC chest", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        Toggle("Enable FC chest helpers", config.EnableCompanyChest, v => config.EnableCompanyChest = v);
        ImGuiComponents.HelpMarker("Quick transfer on an inventory, armoury or crystal item deposits it to the chest tab you have open. On a chest item, it takes it out.");

        using (ImRaii.Disabled(!config.EnableCompanyChest))
        {
            Toggle("Deposit into empty slots first", config.CompanyChestDepositEmptySlotsFirst, v => config.CompanyChestDepositEmptySlotsFirst = v);
            ImGuiComponents.HelpMarker("Uses an empty slot before topping up a partial stack. Tidy the tab later to merge stacks.");
        }

        ImGui.Spacing();
    }

    private void DrawAdvanced()
    {
        if (!ImGui.CollapsingHeader("Advanced"))
        {
            return;
        }

        Slider("Modifier latch", "##ModifierLatchMs", config.ModifierLatchMs, 0, 500, v => config.ModifierLatchMs = v);
        ImGuiComponents.HelpMarker("A quick tap of the key still counts for this long after you let go, so you don't have to hold it while clicking.");

        Slider("Transfer cooldown", "##TransferCooldownMs", config.TransferCooldownMs, 0, 1000, v => config.TransferCooldownMs = v);
        ImGuiComponents.HelpMarker("Shortest time between two right-click actions. Raise it if moves get skipped.");

        Toggle("Debug log", config.DebugMode, v => config.DebugMode = v);
        if (config.DebugMode)
        {
            ImGui.SameLine();
            ImGui.TextColored(WarningColor, "Writes detailed logs to /xllog. Turn off when done.");
        }

        if (ImGui.Button("Reset shortcuts to defaults"))
        {
            config.ResetKeybindingsToDefaults();
            config.OnSettingChanged();
        }

        ImGuiComponents.HelpMarker("Resets the shortcut keys and mouse buttons. FC chest and vendor options stay as they are.");
    }

    private void DrawModifierRow(
        string label,
        bool enabled,
        VirtualKey modifier,
        Action<bool> setEnabled,
        Action<VirtualKey> setModifier)
    {
        Toggle(label, enabled, setEnabled);
        ImGui.SameLine(ControlColumn);
        using (ImRaii.Disabled(!enabled))
        {
            ImGui.SetNextItemWidth(100);
            if (DrawModifierCombo($"##{label}Modifier", modifier, setModifier))
            {
                config.OnSettingChanged();
            }

            ImGui.SameLine();
            ImGui.TextUnformatted("+ right-click");
        }
    }

    private bool DrawModifierCombo(string id, VirtualKey modifier, Action<VirtualKey> setModifier)
    {
        var rowModifier = ModifierBindings.SanitizeModifier(modifier);
        if (rowModifier != modifier)
        {
            setModifier(rowModifier);
            config.OnSettingChanged();
        }

        var changed = false;
        using var combo = ImRaii.Combo(id, ModifierBindings.GetDisplayName(rowModifier));
        if (!combo)
        {
            return false;
        }

        foreach (var option in ModifierBindings.AllowedModifiers)
        {
            var selected = option == rowModifier;
            if (ImGui.Selectable(ModifierBindings.GetDisplayName(option), selected))
            {
                setModifier(option);
                changed = true;
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        return changed;
    }

    private static void QuickTransferHelp()
    {
        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());
        }

        if (!ImGui.IsItemHovered())
        {
            return;
        }

        using var tooltip = ImRaii.Tooltip();
        ImGui.TextUnformatted("Moves the item where it belongs. The first window in this list that is open wins:");
        using var table = ImRaii.Table("QuickTransferTargets", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Open");
        ImGui.TableSetupColumn("Quick transfer");
        ImGui.TableHeadersRow();
        foreach (var (window, result) in QuickTransferTargets)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextColored(QuickTransferTheme.Heading, window);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(result);
        }
    }

    private void LiveLine(string text)
    {
        if (config.Enabled)
        {
            ImGui.TextColored(QuickTransferTheme.Slate, $"Right now: {text}");
        }
    }

    private void Toggle(string label, bool value, Action<bool> set)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            config.OnSettingChanged();
        }
    }

    private void Slider(string label, string id, int value, int min, int max, Action<int> set)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(ControlColumn);
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderInt(id, ref value, min, max, "%d ms"))
        {
            set(value);
            config.OnSettingChanged();
        }
    }

    private static void Indent(bool enabled, Action draw)
    {
        ImGui.Indent(20);
        using (ImRaii.Disabled(!enabled))
        {
            draw();
        }

        ImGui.Unindent(20);
    }
}
