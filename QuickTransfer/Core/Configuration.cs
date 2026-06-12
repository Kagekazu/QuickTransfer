using Dalamud.Configuration;
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
    public int Version { get; set; } = 3;

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
            return;

        WriteToDisk();
        _pendingPersist = false;
    }

    private void WriteToDisk() => pluginInterface!.SavePluginConfig(this);
}
