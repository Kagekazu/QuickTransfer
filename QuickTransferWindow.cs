using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System.Numerics;
namespace QuickTransfer;

public class QuickTransferWindow : Window, IDisposable
{
    private readonly Configuration config;

    public QuickTransferWindow(Configuration config)
        : base("QuickTransfer Settings###QuickTransferConfig")
    {
        this.config = config;

        SizeCondition = ImGuiCond.FirstUseEver;
        Size = new Vector2(500, 400);
    }

    public void Dispose()
    {
        // no-op
    }

    public override void Draw()
    {
        // Main settings
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "QuickTransfer Configuration");
        ImGui.Separator();

        // Enable/Disable
        bool enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled###Enabled", ref enabled))
        {
            config.Enabled = enabled;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), config.Enabled ? "(Active)" : "(Disabled)");

        ImGui.Spacing();

        // Debug mode
        bool debugMode = config.DebugMode;
        if (ImGui.Checkbox("Debug Mode###DebugMode", ref debugMode))
        {
            config.DebugMode = debugMode;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(Logs to chat - for troubleshooting)");

        ImGui.Spacing();

        // Middle-click sort
        bool mmbSort = config.EnableMiddleClickSort;
        if (ImGui.Checkbox("Enable Middle-Click Sort###EnableMiddleClickSort", ref mmbSort))
        {
            config.EnableMiddleClickSort = mmbSort;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(MMB on an item: auto-select \"Sort\" when available)");

        // Company Chest
        bool enableCompanyChest = config.EnableCompanyChest;
        if (ImGui.Checkbox("Enable Company Chest (Free Company Chest)###EnableCompanyChest", ref enableCompanyChest))
        {
            config.EnableCompanyChest = enableCompanyChest;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(Shift/Alt: deposit/withdraw while FC chest is open)");

        bool mmbCompanyOrganize = config.EnableCompanyChestMiddleClickOrganize;
        if (ImGui.Checkbox("Company Chest: Middle-Click Organize###EnableCompanyChestMiddleClickOrganize", ref mmbCompanyOrganize))
        {
            config.EnableCompanyChestMiddleClickOrganize = mmbCompanyOrganize;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(MMB: auto-stack + compact in FC chest)");

        bool autoConfirmQty = config.AutoConfirmCompanyChestQuantity;
        if (ImGui.Checkbox("Auto-confirm quantity prompts (Company Chest / Split)###AutoConfirmCompanyChestQty", ref autoConfirmQty))
        {
            config.AutoConfirmCompanyChestQuantity = autoConfirmQty;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.85f, 0.75f, 0.45f, 0.9f), "(Best effort; disable if it misbehaves)");

        // Vendor Quick Sell
        bool enableVendorQuickSell = config.EnableVendorQuickSell;
        if (ImGui.Checkbox("Enable Vendor Quick Sell###EnableVendorQuickSell", ref enableVendorQuickSell))
        {
            config.EnableVendorQuickSell = enableVendorQuickSell;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(Shift+RClick: auto-select \"Sell\" when vendor is open)");

        bool autoConfirmVendorSell = config.AutoConfirmVendorSell;
        if (ImGui.Checkbox("Auto-confirm vendor sell dialogs###AutoConfirmVendorSell", ref autoConfirmVendorSell))
        {
            config.AutoConfirmVendorSell = autoConfirmVendorSell;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(Auto-fill quantity, confirm \"How many?\", and click OK on \"Are you certain?\")");

        // Transfer cooldown
        ImGui.Spacing();
        ImGui.Text("Transfer Cooldown (ms):");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        int cooldown = config.TransferCooldownMs;
        if (ImGui.InputInt("###Cooldown", ref cooldown))
        {
            config.TransferCooldownMs = Math.Max(0, Math.Min(1000, cooldown));
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();

        // Instructions
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "How to Use:");
        ImGui.BulletText("Hold SHIFT and RIGHT-CLICK to use the open container's quick action");
        ImGui.BulletText("Hold CTRL and RIGHT-CLICK to use Armoury actions when a Saddlebag, Retainer, or Company Chest is open (Inventory ↔ Armoury)");
        ImGui.BulletText("Hold ALT and RIGHT-CLICK to split a stack in half (or remove half from Company Chest)");
        ImGui.BulletText("Inventory + Saddlebags: Inventory → \"Add All to Saddlebag\", Saddlebags → \"Remove All from Saddlebag\"");
        ImGui.BulletText("Armoury + Saddlebags: Armoury → \"Add All to Saddlebag\"");
        ImGui.BulletText("Inventory + Retainer: Inventory → \"Entrust to Retainer\", Retainer → \"Retrieve from Retainer\"");
        ImGui.BulletText("Armoury + Retainer: Armoury → \"Entrust to Retainer\", Retainer → \"Retrieve from Retainer\"");
        ImGui.BulletText("Retainer + Saddlebags: Retainer → \"Add All to Saddlebag\", Saddlebags → \"Entrust to Retainer\"");
        ImGui.BulletText("Inventory + Armoury (no special container): (Gear) Inventory → \"Place in Armoury Chest\", Armoury → \"Return to Inventory\"");
        ImGui.BulletText("Company Chest (FreeCompanyChest) open: Shift+RClick Inventory/Armoury deposits, Shift+RClick Company Chest withdraws (\"Remove\")");
        ImGui.BulletText("Vendor Shop open: Shift+RClick to auto-select \"Sell\"; enable \"Auto-confirm vendor sell\" to auto-fill quantity and confirm.");
        ImGui.BulletText("Middle-Click: Sort the clicked container when a \"Sort\" menu entry exists. In Company Chest, MMB runs an organize pass (stack + compact).");
        ImGui.BulletText("Use /qt or click 'Open Config' in plugin list to reopen this window");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.4f, 1f), "Notes:");
        ImGui.BulletText("This uses the game's existing context menu options (no manual slot moving).");
        ImGui.BulletText("If an option isn't available for the clicked item, nothing happens.");
        ImGui.BulletText("If you tap Shift briefly, the action still triggers (it is captured when the menu opens).");
        ImGui.BulletText("For Company Chest deposits, this uses the same UI move function as drag+drop would.");
        ImGui.Spacing();

        // Save button
        if (ImGui.Button("Save & Close###SaveClose"))
        {
            config.Save();
            IsOpen = false;
        }
    }
}
