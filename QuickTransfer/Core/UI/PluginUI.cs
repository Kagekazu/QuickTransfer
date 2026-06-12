using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using System.Diagnostics;
using System.Numerics;
namespace QuickTransfer;

public class PluginUI : Window
{
    private const string WindowId = "QuickTransfer";

    private static readonly Vector4 HeaderColor = new(0.45f, 0.78f, 1f, 1f);
    private static readonly Vector4 MutedColor = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 WarningColor = new(0.95f, 0.75f, 0.35f, 1f);
    private static readonly Vector4 ActiveColor = new(0.45f, 0.9f, 0.55f, 1f);
    private static readonly Vector4 DisabledColor = new(0.55f, 0.55f, 0.55f, 1f);

    private readonly Configuration config;
    private bool drewTitleBarVersion;

    public PluginUI(Configuration config)
        : base($"QuickTransfer###{WindowId}")
    {
        this.config = config;

        SizeCondition = ImGuiCond.FirstUseEver;
        Size = new Vector2(520, 560);

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
        TitleBarVersion.ClearCache();
        base.OnClose();
    }

    public override void PostDraw()
    {
        if (!drewTitleBarVersion)
        {
            TitleBarVersion.DrawFromWindowLookup(
                TitleBarButtons.Count,
                AllowPinning || AllowClickthrough,
                WindowName);
        }

        drewTitleBarVersion = false;
        base.PostDraw();
    }

    public override void Draw()
    {
        TitleBarVersion.DrawFromContext(
            TitleBarButtons.Count,
            AllowPinning || AllowClickthrough,
            WindowName);
        drewTitleBarVersion = true;

        DrawHeader();
        ImGui.Spacing();

        if (ImGui.BeginTabBar("QuickTransferTabs"))
        {
            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Controls"))
            {
                DrawControlsTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawHeader()
    {
        ImGui.TextColored(HeaderColor, "QuickTransfer");
        ImGui.SameLine();
        ImGui.TextColored(config.Enabled ? ActiveColor : DisabledColor, config.Enabled ? "Active" : "Disabled");

        ImGui.Spacing();

        bool enabled = config.Enabled;
        if (ImGui.Checkbox("Plugin enabled", ref enabled))
        {
            config.Enabled = enabled;
            config.OnSettingChanged();
        }

        Hint("Master switch. When off, all shortcuts are ignored.");
    }

    private void DrawSettingsTab()
    {
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("Middle-click", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Indent(() =>
            {
                bool mmbSort = config.EnableMiddleClickSort;
                if (ImGui.Checkbox("Sort inventories on middle-click", ref mmbSort))
                {
                    config.EnableMiddleClickSort = mmbSort;
                    config.OnSettingChanged();
                }

                Hint("Middle-click an item to auto-select Sort when the container supports it.");
            });
        }

        if (ImGui.CollapsingHeader("Free Company Chest", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Indent(() =>
            {
                bool enableFc = config.EnableCompanyChest;
                if (ImGui.Checkbox("Enable FC chest helpers", ref enableFc))
                {
                    config.EnableCompanyChest = enableFc;
                    config.OnSettingChanged();
                }

                Hint("Shift+right-click deposits from inventory/armoury/crystals; Shift+right-click the chest withdraws.");

                ImGui.Spacing();
                using (ImRaii.Disabled(!config.EnableCompanyChest))
                {
                    bool mmbOrganize = config.EnableCompanyChestMiddleClickOrganize;
                    if (ImGui.Checkbox("Middle-click organize (stack + compact)", ref mmbOrganize))
                    {
                        config.EnableCompanyChestMiddleClickOrganize = mmbOrganize;
                        config.OnSettingChanged();
                    }

                    Hint("FC chest has no Sort menu — middle-click runs an organize pass on the active tab.");

                    bool autoConfirmQty = config.AutoConfirmCompanyChestQuantity;
                    if (ImGui.Checkbox("Auto-confirm quantity dialogs", ref autoConfirmQty))
                    {
                        config.AutoConfirmCompanyChestQuantity = autoConfirmQty;
                        config.OnSettingChanged();
                    }

                    Hint("Auto-fills and confirms store, remove, and split quantity prompts.");

                    bool emptySlotsFirst = config.CompanyChestDepositEmptySlotsFirst;
                    if (ImGui.Checkbox("Deposit to empty slots first", ref emptySlotsFirst))
                    {
                        config.CompanyChestDepositEmptySlotsFirst = emptySlotsFirst;
                        config.OnSettingChanged();
                    }

                    Hint("Avoids topping off partial stacks when an empty slot exists — one server check per deposit. Use middle-click organize to stack later.");

                    ImGui.Text("Unlocked item tabs");
                    ImGui.SetNextItemWidth(220);
                    int compartments = config.CompanyChestCompartments;
                    if (ImGui.SliderInt("##CompanyChestCompartments", ref compartments, 3, 5))
                    {
                        config.CompanyChestCompartments = compartments;
                        config.OnSettingChanged();
                    }

                    ImGui.SameLine();
                    ImGui.TextColored(MutedColor, $"{compartments} tab(s)");

                    Hint("Match how many item compartments your FC has unlocked (usually 3, up to 5).");
                }
            });
        }

        if (ImGui.CollapsingHeader("Vendor quick sell", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Indent(() =>
            {
                bool enableVendor = config.EnableVendorQuickSell;
                if (ImGui.Checkbox("Enable vendor quick sell", ref enableVendor))
                {
                    config.EnableVendorQuickSell = enableVendor;
                    config.OnSettingChanged();
                }

                Hint("With a vendor shop open, Shift+right-click selects Sell.");

                using (ImRaii.Disabled(!config.EnableVendorQuickSell))
                {
                    bool autoConfirmSell = config.AutoConfirmVendorSell;
                    if (ImGui.Checkbox("Auto-confirm sell dialogs", ref autoConfirmSell))
                    {
                        config.AutoConfirmVendorSell = autoConfirmSell;
                        config.OnSettingChanged();
                    }

                    Hint("Auto-fills quantity and confirms \"How many?\" and \"Are you certain?\" prompts.");
                }
            });
        }

        if (ImGui.CollapsingHeader("Advanced"))
        {
            Indent(() =>
            {
                ImGui.Text("Transfer cooldown");
                ImGui.SetNextItemWidth(220);
                int cooldown = config.TransferCooldownMs;
                if (ImGui.SliderInt("##TransferCooldownMs", ref cooldown, 0, 1000, "%d ms"))
                {
                    config.TransferCooldownMs = cooldown;
                    config.OnSettingChanged();
                }

                Hint("Minimum time between right-click actions. Increase if actions feel too fast or get skipped.");

                ImGui.Spacing();

                bool debugMode = config.DebugMode;
                if (ImGui.Checkbox("Debug mode", ref debugMode))
                {
                    config.DebugMode = debugMode;
                    config.OnSettingChanged();
                }

                ImGui.TextColored(WarningColor, "Logs detailed actions to chat. For troubleshooting only.");
            });
        }
    }

    private void DrawControlsTab()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(
            "QuickTransfer picks existing context menu entries — it does not move items on its own. " +
            "If the menu option is not available for that item, nothing happens.");
        ImGui.Spacing();

        DrawModifierTable();
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("By container combination", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Indent(() =>
            {
                Bullet("Inventory + Saddlebags", "Add All to Saddlebag / Remove All from Saddlebag");
                Bullet("Inventory + Armoury", "Place in Armoury Chest / Return to Inventory");
                Bullet("Inventory + Retainer", "Entrust to Retainer / Retrieve from Retainer");
                Bullet("Retainer + Saddlebags", "Add All to Saddlebag / Entrust to Retainer");
                Bullet("Trade window open", "Shift → Trade (auto-confirms stack size when enabled)");
                Bullet("Vendor shop open", "Shift → Sell");
                Bullet("FC chest open", "Shift on inventory/crystals → deposit to active tab; Shift on chest → Remove");
            });
        }

        ImGui.Spacing();
        ImGui.TextColored(MutedColor, "Tip: Brief Shift/Ctrl/Alt taps still count — you do not need to hold through the whole menu.");
        ImGui.TextColored(MutedColor, "Command: /qt");
    }

    private void DrawModifierTable()
    {
        using ImRaii.TableDisposable table = ImRaii.Table("ModifierTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table)
            return;

        ImGui.TableSetupColumn("Input", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        TableRow("Shift + Right-click", "Default quick transfer (direction depends on what's open)");
        TableRow("Ctrl + Right-click", "Armoury actions when Saddlebag, Retainer, or FC chest is open");
        TableRow("Alt + Right-click", "Split stack in half (or remove half from FC chest)");
        TableRow("Middle-click", "Sort container, or FC chest organize if enabled");
    }

    private static void TableRow(string input, string action)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextColored(HeaderColor, input);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextWrapped(action);
    }

    private static void Bullet(string title, string detail)
    {
        ImGui.BulletText(title);
        ImGui.SameLine(0, 4);
        ImGui.TextColored(MutedColor, "—");
        ImGui.SameLine(0, 4);
        ImGui.TextWrapped(detail);
    }

    private static void Hint(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, MutedColor))
            ImGui.TextWrapped(text);

        ImGui.Spacing();
    }

    private static void Indent(Action draw)
    {
        ImGui.Indent(12);
        draw();
        ImGui.Unindent(12);
    }
}
