using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;
namespace QuickTransfer;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    [NonSerialized] private bool _pendingPersist;

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public bool Enabled { get; set; } = true;
    // Default OFF (explicitly requested).
    public bool DebugMode { get; set; }
    public int TransferCooldownMs { get; set; } = 200;

    public bool EnableMiddleClickSort { get; set; } = true;
    public bool EnableCompanyChestMiddleClickOrganize { get; set; } = true;

    public bool EnableCompanyChest { get; set; } = true;
    public bool AutoConfirmCompanyChestQuantity { get; set; } = true;
    public bool CompanyChestDepositEmptySlotsFirst { get; set; } = true;
    public int CompanyChestCompartments { get; set; } = 3; // 3..5 (default game starts at 3)

    public bool EnableVendorQuickSell { get; set; } = true;
    public bool AutoConfirmVendorSell { get; set; } = true;

    public bool EnableShiftQuickTransfer { get; set; } = true;
    public bool EnableCtrlArmoury { get; set; } = true;
    public bool EnableAltSplit { get; set; } = true;
    public VirtualKey ShiftActionModifier { get; set; } = VirtualKey.SHIFT;
    public VirtualKey CtrlActionModifier { get; set; } = VirtualKey.CONTROL;
    public VirtualKey AltActionModifier { get; set; } = VirtualKey.MENU;
    public int ModifierLatchMs { get; set; } = 180;
    public bool MiddleClickUseMButton { get; set; } = true;
    public bool MiddleClickUseXButton1 { get; set; } = true;
    public bool MiddleClickUseXButton2 { get; set; } = true;

    public int Version { get; set; } = 4;

    public void ResetKeybindingsToDefaults()
    {
        EnableShiftQuickTransfer = true;
        EnableCtrlArmoury = true;
        EnableAltSplit = true;
        ShiftActionModifier = VirtualKey.SHIFT;
        CtrlActionModifier = VirtualKey.CONTROL;
        AltActionModifier = VirtualKey.MENU;
        ModifierLatchMs = 180;
        MiddleClickUseMButton = true;
        MiddleClickUseXButton1 = true;
        MiddleClickUseXButton2 = true;
        EnableMiddleClickSort = true;
    }

    public void SanitizeKeybindings()
    {
        ShiftActionModifier = ModifierBindings.SanitizeModifier(ShiftActionModifier);
        CtrlActionModifier = ModifierBindings.SanitizeModifier(CtrlActionModifier);
        AltActionModifier = ModifierBindings.SanitizeModifier(AltActionModifier);
        ModifierLatchMs = Math.Clamp(ModifierLatchMs, 0, 500);
    }

    public void Initialize(IDalamudPluginInterface pluginInterface) => this.pluginInterface = pluginInterface;

    public void Save()
    {
        WriteToDisk();
        _pendingPersist = false;
    }

    public void OnSettingChanged() => _pendingPersist = true;

    public void PersistIfDirty()
    {
        if (!_pendingPersist)
        {
            return;
        }

        WriteToDisk();
        _pendingPersist = false;
    }

    private void WriteToDisk() => pluginInterface!.SavePluginConfig(this);
}
