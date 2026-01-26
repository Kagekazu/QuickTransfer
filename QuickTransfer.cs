using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace QuickTransfer;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public bool Enabled { get; set; } = true;
    public bool DebugMode { get; set; } = false;
    public int TransferCooldownMs { get; set; } = 200;

    public bool EnableMiddleClickSort { get; set; } = true;
    public bool EnableCompanyChestMiddleClickOrganize { get; set; } = true;

    public bool EnableCompanyChest { get; set; } = true;
    public bool AutoConfirmCompanyChestQuantity { get; set; } = true;
    public int CompanyChestCompartments { get; set; } = 3; // 3..5 (default game starts at 3)

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

public sealed unsafe class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider InteropProvider { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    private const string CommandName = "/qt";

    public Configuration Configuration { get; }

    private readonly WindowSystem windowSystem = new("QuickTransfer");
    private readonly QuickTransferWindow configWindow;

    private long lastActionTickMs;
    private (nint AgentPtr, nint AddonPtr, long EnqueuedAtMs, ModifierMode Mode)? pendingDeferredMenuClick;
    private (string AddonName, long EnqueuedAtMs, ModifierMode Mode)? pendingDeferredDefaultMenu;
    private (nint AgentPtr, nint AddonPtr, long EnqueuedAtMs)? pendingDeferredSortMenuClick;
    private long pendingMiddleClickSortUntilMs;
    private (FFXIVClientStructs.FFXIV.Client.Game.InventoryType Type, int Slot, uint AddonId, long EnqueuedAtMs)? pendingMiddleClickSortRequest;
    private long lastMiddleClickSortMs;

    private long pendingCompanyChestNumericConfirmUntilMs;
    private int pendingCompanyChestNumericConfirmAttempts;
    private long pendingCloseContextMenuAtMs;
    private bool pendingCompanyChestNumericArmed;
    private bool pendingCompanyChestNumericValueSet;
    private long pendingCompanyChestNumericValueSetAtMs;
    private uint pendingCompanyChestNumericDesired;
    private enum PendingNumericKind { None, Store, Remove, Move }
    private PendingNumericKind pendingNumericKind;

    private long lastShiftSeenMs;
    private long lastCtrlSeenMs;

    // For stack moves that open InputNumeric, the native operation state must stay alive.
    // If it's stack-allocated, the resulting InputNumeric buttons can become "dead".
    private nint pendingMoveOutValuePtr;
    private long pendingMoveOutValueFreeAtMs;
    private nint pendingMoveAtkValuesPtr;
    private long pendingMoveCreatedAtMs;
    private bool pendingMoveSawInputNumeric;
    private static readonly Dictionary<uint, uint> StackSizeCache = new();

    private struct CompanyChestDepositState
    {
        public bool Active;
        public FFXIVClientStructs.FFXIV.Client.Game.InventoryType SourceType;
        public uint SourceSlot;
        public uint ItemId;
        public bool IsHq;
        public long NextAttemptAtMs;
        public long ExpiresAtMs;
        public int Steps;
        public uint LastQty;
        public long WaitForQtyChangeUntilMs;
    }

    private CompanyChestDepositState companyChestDeposit;

    private struct CompanyChestOrganizeState
    {
        public bool Active;
        public long NextAttemptAtMs;
        public long ExpiresAtMs;
        public int Steps;
        public int Phase; // 0=stack, 1=compact
    }

    private CompanyChestOrganizeState companyChestOrganize;

    private enum ModifierMode
    {
        Shift,
        Ctrl,
    }

    private delegate void OpenForItemSlotDelegate(
        AgentInventoryContext* agent,
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType inventoryType,
        int slot,
        int a4,
        uint addonId);

    // Inventory/armoury uses this; saddlebags often do not, so we also use IContextMenu fallback.
    [Signature("83 B9 ?? ?? ?? ?? ?? 7E ?? 39 91", DetourName = nameof(OpenForItemSlotDetour))]
    private Hook<OpenForItemSlotDelegate>? openForItemSlotHook = null;

    private delegate byte AtkUnitBaseCloseDelegate(AtkUnitBase* unitBase, byte a2);

    [Signature("40 53 48 83 EC 50 81 A1", Fallibility = Fallibility.Fallible)]
    private AtkUnitBaseCloseDelegate? atkUnitBaseClose = null;

    // NOTE: For inventory transfers (including Free Company Chest), use the client callback handler:
    // RaptureAtkModule::HandleItemMove(AtkValue* returnValue, AtkValue* values, uint valueCount)
    // This is exposed directly by FFXIVClientStructs as a member function, so we do not signature-scan it ourselves.

    private enum AutoContextAction
    {
        AddAllToSaddlebag,
        RemoveAllFromSaddlebag,
        PlaceInArmouryChest,
        ReturnToInventory,
        EntrustToRetainer,
        RetrieveFromRetainer,
        RemoveFromCompanyChest,
        Sort,
    }

    private static readonly string[] ArmouryAddonNames =
    [
        // Common internal names used by the Armoury Chest window across patches.
        "ArmouryBoard",
        "ArmoryBoard",
        "Armoury",
        "Armory",
        "ArmouryChest",
        "ArmoryChest",
    ];

    private const string FreeCompanyChestAddonName = "FreeCompanyChest";
    private const string InputNumericAddonName = "InputNumeric";
    private const string ContextMenuAddonName = "ContextMenu";

    // IMPORTANT:
    // We suppress by forcing alpha to 0 in PreDraw, which can "stick" because the same addon instance is reused.
    // Therefore we track suppression windows and also restore alpha when not suppressing.
    private long suppressContextMenuUntilMs;
    private long suppressInputNumericUntilMs;

    private void ArmSuppressContextMenu(long now, int durationMs = 250)
        => suppressContextMenuUntilMs = Math.Max(suppressContextMenuUntilMs, now + durationMs);

    private void ArmSuppressInputNumeric(long now, int durationMs = 1500)
        => suppressInputNumericUntilMs = Math.Max(suppressInputNumericUntilMs, now + durationMs);

    private FFXIVClientStructs.FFXIV.Client.Game.InventoryType[] GetCompanyChestInventoryTypes()
    {
        // Don't hardcode enum names; discover them by name at runtime so we don't break across patches/structs.
        // Limit to the configured number of item compartments (default 3; can be upgraded to 5).
        var max = Math.Clamp(Configuration.CompanyChestCompartments, 3, 5);
        return Enum.GetValues<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>()
            .Where(IsCompanyChestType)
            .OrderBy(v => (int)v)
            .Take(max)
            .ToArray();
    }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        configWindow = new QuickTransferWindow(Configuration);
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open QuickTransfer settings",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

        InteropProvider.InitializeFromAttributes(this);
        openForItemSlotHook?.Enable();

        // Saddlebags can bypass OpenForItemSlot, so use a safe deferred click via context menu events.
        ContextMenu.OnMenuOpened += OnContextMenuOpened;
        Framework.Update += OnFrameworkUpdate;

        // Pre-setup hook for InputNumeric so we can override the default quantity BEFORE the dialog is created.
        // Register without a name-filter so we can confirm it fires on this client build.
        AddonLifecycle.RegisterListener(AddonEvent.PreSetup, OnInputNumericPreSetup);
        AddonLifecycle.RegisterListener(AddonEvent.PreDraw, OnAddonPreDraw);
        AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, OnAddonReceiveEvent);

        Log.Information($"Loaded {PluginInterface.Manifest.Name}.");
        Log.Information($"[QuickTransfer] DebugMode={Configuration.DebugMode}, Enabled={Configuration.Enabled}");
        if (Configuration.DebugMode)
        {
            try
            {
                var matches = Enum.GetNames<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>()
                    .Where(n => n.Contains("FreeCompany", StringComparison.OrdinalIgnoreCase) ||
                                n.Contains("Company", StringComparison.OrdinalIgnoreCase) ||
                                n.Contains("Chest", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Log.Information($"[QuickTransfer] InventoryType names containing Company/Chest: {string.Join(", ", matches)}");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[QuickTransfer] Failed to enumerate InventoryType names (debug).");
            }
        }
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        AddonLifecycle.UnregisterListener(AddonEvent.PreSetup, OnInputNumericPreSetup);
        AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, OnAddonPreDraw);
        AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, OnAddonReceiveEvent);

        openForItemSlotHook?.Disable();
        openForItemSlotHook?.Dispose();

        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;

        windowSystem.RemoveAllWindows();
        configWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args) => OpenConfigUi();

    private void OpenConfigUi() => configWindow.IsOpen = true;

    private void OpenForItemSlotDetour(
        AgentInventoryContext* agent,
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType inventoryType,
        int slot,
        int a4,
        uint addonId)
    {
        openForItemSlotHook?.Original(agent, inventoryType, slot, a4, addonId);

        if (!Configuration.Enabled)
            return;

        // Modifier: Ctrl+RClick (special) or Shift+RClick (default).
        // Ctrl takes priority if both are held. Use a short "latch" so quick taps still work.
        var mode = GetModifierModeLatched(Environment.TickCount64);

        if (mode == null)
            return;

        var saddlebagOpen = IsSaddlebagOpen();
        var retainerOpen = IsRetainerOpen();
        var companyChestOpen = IsCompanyChestOpen();
        var specialOpen = saddlebagOpen || retainerOpen || companyChestOpen;

        // Ctrl is only enabled while a "special" container is open (Saddlebag or Retainer),
        // so Shift/Ctrl can be used to disambiguate behaviors.
        if (mode == ModifierMode.Ctrl && !specialOpen)
            return;

        // Never run Ctrl-mode from saddlebag slots.
        if (mode == ModifierMode.Ctrl && IsSaddlebagType(inventoryType))
            return;

        // Never run Ctrl-mode from retainer slots.
        if (mode == ModifierMode.Ctrl && IsRetainerType(inventoryType))
            return;

        // Never run Ctrl-mode from Company Chest slots.
        if (mode == ModifierMode.Ctrl && IsCompanyChestType(inventoryType))
            return;

        var now = Environment.TickCount64;
        if (now - lastActionTickMs < Configuration.TransferCooldownMs)
            return;

        if (mode == ModifierMode.Shift && companyChestOpen && Configuration.EnableCompanyChest)
        {
            // If a quantity dialog is already open, don't start another move.
            if (TryGetVisibleAddon(InputNumericAddonName, out _))
                return;

            // Deposit: Inventory -> Company Chest (UI-driven move).
            // This is handled as a small state machine so stacks can top-off existing stacks and spill into new stacks.
            if (IsPlayerInventoryType(inventoryType) && StartCompanyChestDeposit(inventoryType, (uint)slot))
            {
                lastActionTickMs = now;
                TryCloseCurrentContextMenu(agent);
                return;
            }
        }

        if (TryAutoSelectAndClose(agent, mode.Value, out var chosenText, out var chosenIndex))
        {
            lastActionTickMs = now;
            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] ({mode} + RClick) Selected context action '{chosenText}' (idx={chosenIndex}) via OpenForItemSlot.");
        }
        else if (Configuration.DebugMode && mode == ModifierMode.Ctrl)
        {
            Log.Information("[QuickTransfer] (Ctrl + RClick) No matching armoury action found in context menu.");
            DebugDumpContextMenu(agent, maxItems: 24);
        }
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (!Configuration.Enabled)
            return;

        var now = Environment.TickCount64;
        var middleSortActive = pendingMiddleClickSortUntilMs > 0 && now <= pendingMiddleClickSortUntilMs;
        var mode = middleSortActive ? null : GetModifierModeLatched(now);

        if (!middleSortActive && mode == null)
            return;

        var saddlebagOpen = IsSaddlebagOpen();
        var retainerOpen = IsRetainerOpen();
        var companyChestOpen = IsCompanyChestOpen();
        var specialOpen = saddlebagOpen || retainerOpen || companyChestOpen;

        if (!middleSortActive && mode == ModifierMode.Ctrl && !specialOpen)
            return;

        if (Configuration.DebugMode)
            Log.Information($"[QuickTransfer] OnMenuOpened: AddonName='{args.AddonName}', MenuType={args.MenuType}, AgentPtr=0x{args.AgentPtr.ToInt64():X}, AddonPtr=0x{args.AddonPtr.ToInt64():X}");

        // Middle-click "Sort" uses an inventory context menu, but does not require Shift/Ctrl.
        if (middleSortActive && args.MenuType == ContextMenuType.Inventory)
        {
            if (args.AgentPtr != IntPtr.Zero && args.AddonPtr != IntPtr.Zero)
            {
                pendingDeferredSortMenuClick = ((nint)args.AgentPtr, (nint)args.AddonPtr, now);
                return;
            }
        }

        // Free Company Chest uses MenuType.Default (not Inventory).
        if (args.MenuType == ContextMenuType.Default &&
            mode == ModifierMode.Shift &&
            Configuration.EnableCompanyChest &&
            string.Equals(args.AddonName, FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase))
        {
            pendingDeferredDefaultMenu = (args.AddonName ?? string.Empty, now, mode.Value);
            return;
        }

        // Only deal with inventory context menus otherwise.
        if (args.MenuType != ContextMenuType.Inventory)
            return;

        if (args.AgentPtr == IntPtr.Zero || args.AddonPtr == IntPtr.Zero)
            return;

        if (mode == null)
            return;

        // IMPORTANT: Do not click inside the open event (re-entrancy risk).
        pendingDeferredMenuClick = ((nint)args.AgentPtr, (nint)args.AddonPtr, Environment.TickCount64, mode.Value);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!Configuration.Enabled)
            return;

        var now = Environment.TickCount64;

        // Modifier latch (helps cases where the user taps Shift/Ctrl quickly).
        if (KeyState[VirtualKey.SHIFT])
            lastShiftSeenMs = now;
        if (KeyState[VirtualKey.CONTROL])
            lastCtrlSeenMs = now;

        // Company Chest quantity prompt auto-confirm (best effort).
        if (Configuration.EnableCompanyChest &&
            Configuration.AutoConfirmCompanyChestQuantity &&
            pendingCompanyChestNumericConfirmUntilMs > 0 &&
            now <= pendingCompanyChestNumericConfirmUntilMs)
        {
            if (TryGetVisibleAddon(InputNumericAddonName, out var inputNumeric))
            {
                ArmSuppressInputNumeric(now);
                // Phase 1: set max (and wait a frame so the component commits the value internally).
                if (!pendingCompanyChestNumericValueSet)
                {
                    if (!TrySetInputNumericToMax(inputNumeric, pendingNumericKind))
                    {
                        // Prompt doesn't match expectation; stop (prevents confirming wrong dialogs).
                        pendingCompanyChestNumericConfirmUntilMs = 0;
                        pendingCompanyChestNumericArmed = false;
                        pendingNumericKind = PendingNumericKind.None;
                        suppressInputNumericUntilMs = 0;
                    }
                    else
                    {
                        pendingCompanyChestNumericValueSet = true;
                        pendingCompanyChestNumericValueSetAtMs = now;
                    }
                    return;
                }

                // Phase 2: confirm after a short delay (next frame / ~50ms).
                if (now - pendingCompanyChestNumericValueSetAtMs < 50)
                    return;

                // Re-check prompt + re-apply max right before confirming (cheap + safer).
                if (!TrySetInputNumericToMax(inputNumeric, pendingNumericKind))
                {
                    pendingCompanyChestNumericConfirmUntilMs = 0;
                    pendingCompanyChestNumericArmed = false;
                    pendingNumericKind = PendingNumericKind.None;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    return;
                }

                try
                {
                    if (pendingCompanyChestNumericConfirmAttempts == 0)
                    {
                        // IMPORTANT:
                        // For InputNumeric, FireCallbackInt(value) is interpreted as the quantity being confirmed on this client build.
                        // Passing "2" causes exactly the observed behavior: moving 2 every time.
                        var toConfirm = pendingCompanyChestNumericDesired;
                        if (toConfirm == 0)
                            toConfirm = 1;
                        inputNumeric->FireCallbackInt((int)toConfirm);
                        pendingCompanyChestNumericConfirmAttempts = 1;
                        if (Configuration.DebugMode)
                            Log.Information($"[QuickTransfer] Auto-confirmed InputNumeric (Company Chest) attempt 1 (FireCallbackInt={toConfirm}).");

                        // Clear state after issuing confirm; the dialog should close itself.
                        pendingCompanyChestNumericConfirmUntilMs = 0;
                        pendingCompanyChestNumericArmed = false;
                        pendingNumericKind = PendingNumericKind.None;
                        pendingCompanyChestNumericValueSet = false;
                        pendingCompanyChestNumericValueSetAtMs = 0;
                        pendingCompanyChestNumericDesired = 0;
                        suppressInputNumericUntilMs = 0;
                    }
                    else
                    {
                        pendingCompanyChestNumericConfirmUntilMs = 0;
                        pendingCompanyChestNumericArmed = false;
                        pendingNumericKind = PendingNumericKind.None;
                        pendingCompanyChestNumericValueSet = false;
                        pendingCompanyChestNumericValueSetAtMs = 0;
                        pendingCompanyChestNumericDesired = 0;
                        suppressInputNumericUntilMs = 0;
                    }
                }
                catch (Exception ex)
                {
                    pendingCompanyChestNumericConfirmUntilMs = 0;
                    pendingCompanyChestNumericArmed = false;
                    pendingNumericKind = PendingNumericKind.None;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    suppressInputNumericUntilMs = 0;
                    Log.Warning(ex, "[QuickTransfer] Failed to auto-confirm InputNumeric.");
                }
            }
        }
        else if (pendingCompanyChestNumericConfirmUntilMs > 0 && now > pendingCompanyChestNumericConfirmUntilMs)
        {
            pendingCompanyChestNumericConfirmUntilMs = 0;
            pendingCompanyChestNumericArmed = false;
            pendingNumericKind = PendingNumericKind.None;
            pendingCompanyChestNumericValueSet = false;
            pendingCompanyChestNumericValueSetAtMs = 0;
            pendingCompanyChestNumericDesired = 0;
            suppressInputNumericUntilMs = 0;
        }

        // If we have a pending "move outValue" buffer for InputNumeric-driven moves, free it once the dialog is gone,
        // or when the timeout expires.
        if (pendingMoveOutValuePtr != 0 || pendingMoveAtkValuesPtr != 0)
        {
            var inputVisible = TryGetVisibleAddon(InputNumericAddonName, out _);
            if (inputVisible)
                pendingMoveSawInputNumeric = true;

            // Important: InputNumeric often appears on a subsequent frame.
            // Do NOT free the buffers immediately just because it's not visible yet.
            var graceExpired = pendingMoveCreatedAtMs > 0 && now - pendingMoveCreatedAtMs >= 1500;
            if ((pendingMoveSawInputNumeric && !inputVisible) || now >= pendingMoveOutValueFreeAtMs || (!inputVisible && graceExpired))
            {
                try { if (pendingMoveOutValuePtr != 0) Marshal.FreeHGlobal(pendingMoveOutValuePtr); } catch { /* ignore */ }
                try { if (pendingMoveAtkValuesPtr != 0) Marshal.FreeHGlobal(pendingMoveAtkValuesPtr); } catch { /* ignore */ }
                pendingMoveOutValuePtr = 0;
                pendingMoveOutValueFreeAtMs = 0;
                pendingMoveAtkValuesPtr = 0;
                pendingMoveCreatedAtMs = 0;
                pendingMoveSawInputNumeric = false;
            }
        }

        // Company Chest deposit state machine (Inventory -> FC Chest).
        if (Configuration.EnableCompanyChest)
            ProcessCompanyChestDeposit(now);

        // Company Chest organize (MMB): auto-stack + compact items within FC chest pages.
        if (Configuration.EnableCompanyChest && Configuration.EnableCompanyChestMiddleClickOrganize)
            ProcessCompanyChestOrganize(now);

        // Middle-click sort: open the context menu on the clicked slot, then auto-select "Sort".
        var mmb = pendingMiddleClickSortRequest;
        if (Configuration.EnableMiddleClickSort && mmb != null && now - mmb.Value.EnqueuedAtMs <= 1500)
        {
            // If the request was for Company Chest, run organize instead (there is no Sort entry on the item menu).
            if (IsCompanyChestType(mmb.Value.Type) && Configuration.EnableCompanyChest && Configuration.EnableCompanyChestMiddleClickOrganize)
            {
                StartCompanyChestOrganize(now);
                pendingMiddleClickSortRequest = null;
                pendingMiddleClickSortUntilMs = 0;
            }
            else
            {
                // Open context menu for that slot. Our OnMenuOpened handler will enqueue the deferred sort selection.
                var agentModule = AgentModule.Instance();
                if (agentModule != null)
                {
                    var agent = agentModule->GetAgentByInternalId(AgentId.InventoryContext);
                    var invCtx = (AgentInventoryContext*)agent;
                    if (invCtx != null)
                    {
                        try
                        {
                            ArmSuppressContextMenu(now, 250);
                            invCtx->OpenForItemSlot(mmb.Value.Type, mmb.Value.Slot, 0, mmb.Value.AddonId);
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }

                // Only try once; selection happens via deferred menu click.
                pendingMiddleClickSortRequest = null;
            }
        }

        // Delay-close the context menu slightly; closing immediately can cancel some default-menu actions.
        if (pendingCloseContextMenuAtMs > 0 && now >= pendingCloseContextMenuAtMs)
        {
            pendingCloseContextMenuAtMs = 0;
            try
            {
                var cm = (AtkUnitBase*)GameGui.GetAddonByName("ContextMenu", 1).Address;
                if (cm != null)
                {
                    try { cm->Hide(false, true, 0); } catch { /* ignore */ }
                    try { atkUnitBaseClose?.Invoke(cm, 0); } catch { /* ignore */ }
                }
            }
            catch
            {
                // ignore
            }
        }

        // Handle deferred "default" context menus (e.g., FreeCompanyChest).
        var pendingDefault = pendingDeferredDefaultMenu;
        if (pendingDefault != null)
        {
            pendingDeferredDefaultMenu = null;

            if (now - pendingDefault.Value.EnqueuedAtMs <= 1500 &&
                pendingDefault.Value.Mode == ModifierMode.Shift &&
                pendingDefault.Value.AddonName.Equals(FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase) &&
                Configuration.EnableCompanyChest)
            {
                ArmSuppressContextMenu(now, 1500);
                if (TrySelectRemoveFromCompanyChestContextMenu())
                {
                    lastActionTickMs = now;
                    pendingCompanyChestNumericConfirmUntilMs = Configuration.AutoConfirmCompanyChestQuantity ? now + 1500 : 0;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Remove;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    ArmSuppressInputNumeric(now, 1500);
                }
                // Keep suppression on while the remove dialog is being handled.
            }
        }

        var pending = pendingDeferredMenuClick;
        if (pending == null)
        {
            // Process deferred middle-click sort selection even if no normal deferred click.
            ProcessDeferredSortMenuClick(now);
            return;
        }

        // Consume (only try once).
        pendingDeferredMenuClick = null;

        if (now - pending.Value.EnqueuedAtMs > 1500)
            return;

        // If we already acted this tick/window via OpenForItemSlot, don't deref pointers.
        if (now - lastActionTickMs < Configuration.TransferCooldownMs)
            return;

        try
        {
            var agent = (AgentInventoryContext*)pending.Value.AgentPtr;
            var addon = (AtkUnitBase*)pending.Value.AddonPtr;

            if (TryAutoSelectAndClose(agent, addon, pending.Value.Mode, out var chosenText, out var chosenIndex))
            {
                lastActionTickMs = now;
                ArmSuppressContextMenu(now, 1500);
                if (Configuration.EnableCompanyChest &&
                    pending.Value.Mode == ModifierMode.Shift &&
                    chosenText.Length > 0 &&
                    ContextLabelMatches(AutoContextAction.RemoveFromCompanyChest, chosenText))
                {
                    pendingCompanyChestNumericConfirmUntilMs = Configuration.AutoConfirmCompanyChestQuantity ? now + 1500 : 0;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Remove;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    ArmSuppressInputNumeric(now, 1500);
                }
                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] ({pending.Value.Mode} + RClick) Selected context action '{chosenText}' (idx={chosenIndex}) via deferred OnMenuOpened.");
            }
            else if (Configuration.DebugMode && pending.Value.Mode == ModifierMode.Ctrl)
            {
                Log.Information("[QuickTransfer] (Ctrl + RClick) Deferred menu opened but no matching 'Place in Armoury Chest' action was found.");
                DebugDumpContextMenu(agent, maxItems: 24);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Deferred menu select failed.");
        }

        // Also process a pending sort click (if any) after normal transfers.
        ProcessDeferredSortMenuClick(now);
    }

    private void ProcessDeferredSortMenuClick(long now)
    {
        var pendingSort = pendingDeferredSortMenuClick;
        if (pendingSort == null)
            return;

        pendingDeferredSortMenuClick = null;
        pendingMiddleClickSortUntilMs = 0;

        if (now - pendingSort.Value.EnqueuedAtMs > 1500)
            return;

        try
        {
            var agent = (AgentInventoryContext*)pendingSort.Value.AgentPtr;
            var addon = (AtkUnitBase*)pendingSort.Value.AddonPtr;

            if (TrySelectSortAndClose(agent, addon, out var chosenText, out var chosenIndex))
            {
                lastActionTickMs = now;
                ArmSuppressContextMenu(now, 500);
                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] (MMB) Selected context action '{chosenText}' (idx={chosenIndex}) via deferred OnMenuOpened.");
            }
            else
            {
                // If we opened a menu but didn't find Sort, close it to avoid leaving a hidden menu behind.
                try { CloseContextMenuAddon(agent, addon); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Deferred sort select failed.");
        }
    }

    private void OnAddonPreDraw(AddonEvent type, AddonArgs args)
    {
        try
        {
            var name = args.AddonName ?? string.Empty;
            var now = Environment.TickCount64;

            if (string.Equals(name, ContextMenuAddonName, StringComparison.OrdinalIgnoreCase))
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                if (now <= suppressContextMenuUntilMs)
                    MakeAddonInvisible(addon);
                else
                    MakeAddonVisible(addon);
            }

            if (string.Equals(name, InputNumericAddonName, StringComparison.OrdinalIgnoreCase))
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                if (now <= suppressInputNumericUntilMs)
                    MakeAddonInvisible(addon);
                else
                    MakeAddonVisible(addon);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void OnAddonReceiveEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!Configuration.Enabled || !Configuration.EnableMiddleClickSort)
                return;

            if (args is not AddonReceiveEventArgs recv)
                return;

            var now = Environment.TickCount64;
            if (now - lastMiddleClickSortMs < 250)
                return;

            var eventType = (AtkEventType)recv.AtkEventType;
            if (eventType != AtkEventType.DragDropClick && eventType != AtkEventType.MouseClick && eventType != AtkEventType.MouseDown)
                return;

            var eventData = (AtkEventData*)recv.AtkEventData;
            if (eventData == null)
                return;

            // Inventory slots are drag-drop components; use drag-drop mouse button id when available.
            var buttonId = eventType == AtkEventType.DragDropClick ? eventData->DragDropData.MouseButtonId : eventData->MouseData.ButtonId;
            const byte middleButtonId = 2;
            if (buttonId != middleButtonId)
                return;

            var ddi = eventData->DragDropData.DragDropInterface;
            if (ddi == null)
                return;

            var payload = ddi->GetPayloadContainer();
            if (payload == null)
                return;

            var invType = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)payload->Int1;
            var slot = payload->Int2;
            if (slot < 0 || slot > 500)
                return;

            // Only act on inventory containers we understand (avoid hotbars, etc.).
            if (!IsPlayerInventoryType(invType) && !IsArmouryType(invType) && !IsSaddlebagType(invType) && !IsRetainerType(invType) && !IsCompanyChestType(invType))
                return;

            // Require a real item slot unless it's Company Chest (organize operates on whole chest).
            if (!IsCompanyChestType(invType))
            {
                if (!TryGetItemInfo(invType, slot, out var itemId, out _, out _))
                    return;
                if (itemId == 0)
                    return;
            }

            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null)
                return;

            pendingMiddleClickSortRequest = (invType, slot, addon->Id, now);
            pendingMiddleClickSortUntilMs = now + 1500;
            lastMiddleClickSortMs = now;

            // Prevent the underlying UI from processing the click further.
            var atkEvent = (AtkEvent*)recv.AtkEvent;
            if (atkEvent != null)
                atkEvent->SetEventIsHandled();
        }
        catch
        {
            // ignore
        }
    }

    private static void MakeAddonInvisible(AtkUnitBase* addon)
    {
        if (addon == null)
            return;
        var root = addon->RootNode;
        if (root == null)
            return;

        // Keep it logically visible/interactive, but force it fully transparent before it draws.
        root->Color.A = 0;
        root->Alpha_2 = 0;
    }

    private static void MakeAddonVisible(AtkUnitBase* addon)
    {
        if (addon == null)
            return;
        var root = addon->RootNode;
        if (root == null)
            return;

        // Restore fully visible alpha; this prevents "stuck invisible" menus after a suppression frame.
        root->Color.A = 255;
        root->Alpha_2 = 255;
    }

    private void OnInputNumericPreSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!Configuration.EnableCompanyChest)
                return;

            if (!pendingCompanyChestNumericArmed)
                return;

            if (!string.Equals(args.AddonName, InputNumericAddonName, StringComparison.OrdinalIgnoreCase))
                return;

            // Only touch this dialog if the Company Chest is open (avoid affecting unrelated InputNumeric uses).
            if (!IsCompanyChestOpen())
                return;

            if (args is not AddonSetupArgs setup)
                return;

            var values = (AtkValue*)setup.AtkValues;
            var count = (int)setup.AtkValueCount;
            if (values == null || count < 7)
                return;

            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] InputNumeric PreSetup (armed): AtkValueCount={count}");

            // Guard against cross-confirmation: only touch the prompt we intended (store/remove).
            if (pendingNumericKind != PendingNumericKind.None)
            {
                var prompt = values[6].Type is AtkValueType.String or AtkValueType.ManagedString ? ReadAtkValueString(values[6]) : string.Empty;
                if (pendingNumericKind == PendingNumericKind.Store && !prompt.Contains("store", StringComparison.OrdinalIgnoreCase))
                    return;
                if (pendingNumericKind == PendingNumericKind.Remove && !prompt.Contains("remove", StringComparison.OrdinalIgnoreCase))
                    return;
                // For "Move" we accept any prompt while the Company Chest is open (used for internal stack/organize moves).
            }

            // Standard InputNumeric layout (also used by SimpleTweaks):
            // [2]=min (UInt), [3]=max (UInt), [4]=default (UInt), [6]=prompt text (String)
            if (values[2].Type != AtkValueType.UInt || values[3].Type != AtkValueType.UInt || values[4].Type != AtkValueType.UInt)
            {
                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] InputNumeric PreSetup: unexpected types: [2]={values[2].Type}, [3]={values[3].Type}, [4]={values[4].Type}");
                return;
            }

            var min = values[2].UInt;
            var max = values[3].UInt;
            var desired = max < min ? min : max;

            // Log current/default if present.
            if (Configuration.DebugMode)
            {
                var curStr = (count > 5 && values[5].Type == AtkValueType.UInt) ? values[5].UInt.ToString() : "n/a";
                Log.Information($"[QuickTransfer] InputNumeric PreSetup: min={min}, max={max}, default={values[4].UInt}, current={curStr}");
            }

            values[4].UInt = desired; // default
            if (count > 5)
            {
                if (values[5].Type == AtkValueType.UInt)
                    values[5].UInt = desired; // some layouts have current (UInt)
                else if (values[5].Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8)
                    WriteUtf8InPlace(values[5].String, desired.ToString()); // some builds use String current
            }

            if (Configuration.DebugMode)
            {
                var prompt = values[6].Type is AtkValueType.String or AtkValueType.ManagedString ? ReadAtkValueString(values[6]) : string.Empty;
                Log.Information($"[QuickTransfer] InputNumeric PreSetup: prompt='{prompt}', min={min}, max={max}, setDefault={desired}");
            }

        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] InputNumeric PreSetup failed.");
        }
    }

    private bool TryAutoSelectAndClose(AgentInventoryContext* agent, ModifierMode mode, out string chosenText, out int chosenIndex)
    {
        chosenText = string.Empty;
        chosenIndex = -1;

        var agentAddonId = agent->AgentInterface.GetAddonId();
        if (agentAddonId == 0)
            return false;

        var addon = GetAddonById(agentAddonId);
        if (addon == null)
        {
            var cm = GameGui.GetAddonByName("ContextMenu", 1);
            addon = (AtkUnitBase*)cm.Address;
        }

        if (addon == null)
            return false;

        return TryAutoSelectAndClose(agent, addon, mode, out chosenText, out chosenIndex);
    }

    private bool TryAutoSelectAndClose(AgentInventoryContext* agent, AtkUnitBase* contextMenuAddon, ModifierMode mode, out string chosenText, out int chosenIndex)
    {
        chosenText = string.Empty;
        chosenIndex = -1;

        // Single-pass: decode each label once, record first match per action.
        var foundAny = false;

        int removeIdx = -1, addIdx = -1, placeIdx = -1, returnIdx = -1, entrustIdx = -1, retrieveIdx = -1, companyRemoveIdx = -1;
        string? removeTxt = null, addTxt = null, placeTxt = null, returnTxt = null, entrustTxt = null, retrieveTxt = null, companyRemoveTxt = null;

        var max = Math.Min(agent->ContextItemCount, 64);
        for (var i = 0; i < max; i++)
        {
            var param = agent->EventParams[agent->ContexItemStartIndex + i];
            if (param.Type is not (AtkValueType.String or AtkValueType.ManagedString))
                continue;

            var text = ReadAtkValueString(param);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            foundAny = true;

            // Priority matters: we want the first matching index for each action.
            if (removeIdx < 0 && ContextLabelMatches(AutoContextAction.RemoveAllFromSaddlebag, text))
            {
                removeIdx = i;
                removeTxt = text;
                continue;
            }

            if (companyRemoveIdx < 0 && ContextLabelMatches(AutoContextAction.RemoveFromCompanyChest, text))
            {
                companyRemoveIdx = i;
                companyRemoveTxt = text;
                continue;
            }

            if (addIdx < 0 && ContextLabelMatches(AutoContextAction.AddAllToSaddlebag, text))
            {
                addIdx = i;
                addTxt = text;
                continue;
            }

            if (placeIdx < 0 && ContextLabelMatches(AutoContextAction.PlaceInArmouryChest, text))
            {
                placeIdx = i;
                placeTxt = text;
                continue;
            }

            if (returnIdx < 0 && ContextLabelMatches(AutoContextAction.ReturnToInventory, text))
            {
                returnIdx = i;
                returnTxt = text;
                continue;
            }

            if (entrustIdx < 0 && ContextLabelMatches(AutoContextAction.EntrustToRetainer, text))
            {
                entrustIdx = i;
                entrustTxt = text;
                continue;
            }

            if (retrieveIdx < 0 && ContextLabelMatches(AutoContextAction.RetrieveFromRetainer, text))
            {
                retrieveIdx = i;
                retrieveTxt = text;
            }
        }

        if (!foundAny)
            return false;

        var saddlebagOpen = IsSaddlebagOpen();
        var retainerOpen = IsRetainerOpen();
        var companyChestOpen = IsCompanyChestOpen();

        // Choose the best action that exists in the menu.
        //
        // When Company Chest is open:
        // - Shift mode: remove from chest (withdraw)
        // - Ctrl mode: armoury actions (Inventory <-> Armoury) are allowed (like other "special" containers)
        //
        // When Retainer is open:
        // - Shift mode: retainer actions (Entrust/Retrieve), and if Saddlebags are also open, retainer<->saddlebag.
        //
        // When Saddlebags are open (no retainer):
        // - Shift mode: saddlebag actions (Add/Remove)
        //
        // Ctrl mode (only enabled when Retainer OR Saddlebags are open):
        // - Armoury actions (Inventory <-> Armoury): Return/Place
        //
        // No Retainer/Saddlebags:
        // - Shift mode: allow armoury transfers (Place/Return).
        (int idx, string? txt) chosen;
        if (mode == ModifierMode.Shift && companyChestOpen && Configuration.EnableCompanyChest)
        {
            chosen = companyRemoveIdx >= 0 ? (companyRemoveIdx, companyRemoveTxt) : (-1, (string?)null);
        }
        else if (mode == ModifierMode.Ctrl)
        {
            chosen = returnIdx >= 0 ? (returnIdx, returnTxt) :
                placeIdx >= 0 ? (placeIdx, placeTxt) :
                (-1, (string?)null);
        }
        else if (retainerOpen)
        {
            if (saddlebagOpen)
            {
                // Retainer <-> Saddlebag:
                // - Retainer item: Add All to Saddlebag
                // - Saddlebag item: Entrust to Retainer
                chosen = addIdx >= 0 ? (addIdx, addTxt) :
                    entrustIdx >= 0 ? (entrustIdx, entrustTxt) :
                    // last-resort fallback
                    removeIdx >= 0 ? (removeIdx, removeTxt) :
                    (-1, (string?)null);
            }
            else
            {
                // Retainer <-> Player (Inventory/Armoury):
                // - Retainer item: Retrieve from Retainer
                // - Player item: Entrust to Retainer
                chosen = retrieveIdx >= 0 ? (retrieveIdx, retrieveTxt) :
                    entrustIdx >= 0 ? (entrustIdx, entrustTxt) :
                    (-1, (string?)null);
            }
        }
        else if (saddlebagOpen)
        {
            chosen = removeIdx >= 0 ? (removeIdx, removeTxt) :
                addIdx >= 0 ? (addIdx, addTxt) :
                (-1, (string?)null);
        }
        else
        {
            chosen = placeIdx >= 0 ? (placeIdx, placeTxt) :
                returnIdx >= 0 ? (returnIdx, returnTxt) :
                (-1, (string?)null);
        }

        if (chosen.idx < 0 || string.IsNullOrWhiteSpace(chosen.txt))
            return false;

        GenerateCallback(contextMenuAddon, 0, chosen.idx, 0U, 0, 0);
        CloseContextMenuAddon(agent, contextMenuAddon);

        chosenText = chosen.txt!;
        chosenIndex = chosen.idx;
        return true;
    }

    private bool TrySelectSortAndClose(AgentInventoryContext* agent, AtkUnitBase* contextMenuAddon, out string chosenText, out int chosenIndex)
    {
        chosenText = string.Empty;
        chosenIndex = -1;

        var max = Math.Min(agent->ContextItemCount, 64);
        for (var i = 0; i < max; i++)
        {
            var param = agent->EventParams[agent->ContexItemStartIndex + i];
            if (param.Type is not (AtkValueType.String or AtkValueType.ManagedString))
                continue;

            var text = ReadAtkValueString(param);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (!ContextLabelMatches(AutoContextAction.Sort, text))
                continue;

            GenerateCallback(contextMenuAddon, 0, i, 0U, 0, 0);
            CloseContextMenuAddon(agent, contextMenuAddon);
            chosenText = text;
            chosenIndex = i;
            return true;
        }

        return false;
    }

    private bool StartCompanyChestDeposit(FFXIVClientStructs.FFXIV.Client.Game.InventoryType sourceType, uint sourceSlot)
    {
        try
        {
            if (!Configuration.EnableCompanyChest)
                return false;
            if (RaptureAtkModule.Instance() == null)
                return false;
            if (!IsCompanyChestOpen())
                return false;
            if (!IsPlayerInventoryType(sourceType))
                return false;

            if (!TryGetItemInfo(sourceType, (int)sourceSlot, out var itemId, out var isHq, out var qty))
                return false;

            var now = Environment.TickCount64;
            companyChestDeposit = new CompanyChestDepositState
            {
                Active = true,
                SourceType = sourceType,
                SourceSlot = sourceSlot,
                ItemId = itemId,
                IsHq = isHq,
                NextAttemptAtMs = now,
                ExpiresAtMs = now + 12000,
                Steps = 0,
                LastQty = qty,
                WaitForQtyChangeUntilMs = 0,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ProcessCompanyChestDeposit(long now)
    {
        if (!companyChestDeposit.Active)
            return;

        // Stop if conditions no longer apply.
        if (!Configuration.EnableCompanyChest || RaptureAtkModule.Instance() == null || !IsCompanyChestOpen())
        {
            companyChestDeposit.Active = false;
            return;
        }

        if (now >= companyChestDeposit.ExpiresAtMs || companyChestDeposit.Steps >= 40)
        {
            companyChestDeposit.Active = false;
            return;
        }

        // If we just issued a move, wait for the source stack quantity to change (or for the dialog to appear).
        // This prevents spamming the same move over and over when the game hasn't applied it yet.
        if (companyChestDeposit.WaitForQtyChangeUntilMs > 0 && now <= companyChestDeposit.WaitForQtyChangeUntilMs)
        {
            if (TryGetVisibleAddon(InputNumericAddonName, out _))
                return;

            if (TryGetItemInfo(companyChestDeposit.SourceType, (int)companyChestDeposit.SourceSlot, out var _, out var _, out var qNow) &&
                qNow != companyChestDeposit.LastQty)
            {
                companyChestDeposit.LastQty = qNow;
                companyChestDeposit.WaitForQtyChangeUntilMs = 0;
            }
            else
            {
                return;
            }
        }

        // Don't issue a new move while the quantity dialog is open.
        if (TryGetVisibleAddon(InputNumericAddonName, out _))
            return;

        if (now < companyChestDeposit.NextAttemptAtMs)
            return;

        if (!TryGetItemInfo(companyChestDeposit.SourceType, (int)companyChestDeposit.SourceSlot, out var itemId, out var isHq, out var qty) ||
            itemId == 0 ||
            qty == 0)
        {
            companyChestDeposit.Active = false;
            return;
        }

        // If the slot changed (user moved/split), stop to avoid moving the wrong thing.
        if (itemId != companyChestDeposit.ItemId || isHq != companyChestDeposit.IsHq)
        {
            companyChestDeposit.Active = false;
            return;
        }

        var pages = GetCompanyChestInventoryTypes();
        if (pages.Length == 0)
        {
            companyChestDeposit.Active = false;
            return;
        }

        var maxStack = GetItemStackSize(itemId);
        var needsQuantityConfirm = qty > 1 && maxStack > 1;

        // Prefer stacking into an existing stack; otherwise use the first empty slot.
        if (!TryFindCompanyChestBestStackSlot(pages, itemId, isHq, maxStack, out var destType, out var destSlot) &&
            !TryFindCompanyChestFirstEmptySlot(pages, out destType, out destSlot))
        {
            companyChestDeposit.Active = false;
            return;
        }

        if (!TryCompanyChestMoveItem(companyChestDeposit.SourceType, companyChestDeposit.SourceSlot, destType, destSlot, needsQuantityConfirm))
        {
            companyChestDeposit.Active = false;
            return;
        }

        companyChestDeposit.Steps++;
        companyChestDeposit.NextAttemptAtMs = now + (needsQuantityConfirm ? 600 : 350);
        companyChestDeposit.LastQty = qty;
        companyChestDeposit.WaitForQtyChangeUntilMs = now + (needsQuantityConfirm ? 2000 : 1200);

        if (Configuration.AutoConfirmCompanyChestQuantity && needsQuantityConfirm)
        {
            pendingCompanyChestNumericConfirmUntilMs = now + 1500;
            pendingCompanyChestNumericConfirmAttempts = 0;
            pendingCompanyChestNumericArmed = true;
            pendingNumericKind = PendingNumericKind.Store;
            pendingCompanyChestNumericValueSet = false;
            pendingCompanyChestNumericValueSetAtMs = 0;
            pendingCompanyChestNumericDesired = 0;
        }

        if (Configuration.DebugMode)
            Log.Information($"[QuickTransfer] (Shift+RClick) Company Chest deposit step {companyChestDeposit.Steps}: {companyChestDeposit.SourceType} slot={companyChestDeposit.SourceSlot} -> {destType} slot={destSlot} (qty={qty}, stackMax={maxStack}).");
    }

    private void StartCompanyChestOrganize(long now)
    {
        if (!Configuration.EnableCompanyChest || !IsCompanyChestOpen() || RaptureAtkModule.Instance() == null)
            return;

        companyChestOrganize = new CompanyChestOrganizeState
        {
            Active = true,
            NextAttemptAtMs = now,
            ExpiresAtMs = now + 20000,
            Steps = 0,
            Phase = 0,
        };
    }

    private void ProcessCompanyChestOrganize(long now)
    {
        if (!companyChestOrganize.Active)
            return;

        if (!Configuration.EnableCompanyChest || RaptureAtkModule.Instance() == null || !IsCompanyChestOpen())
        {
            companyChestOrganize.Active = false;
            return;
        }

        if (now >= companyChestOrganize.ExpiresAtMs || companyChestOrganize.Steps >= 80)
        {
            companyChestOrganize.Active = false;
            return;
        }

        if (TryGetVisibleAddon(InputNumericAddonName, out _))
            return;

        if (now < companyChestOrganize.NextAttemptAtMs)
            return;

        var pages = GetCompanyChestInventoryTypes();
        if (pages.Length == 0)
        {
            companyChestOrganize.Active = false;
            return;
        }

        // Phase 0: merge stacks where possible.
        if (companyChestOrganize.Phase == 0)
        {
            if (TryFindCompanyChestMergeMove(pages, out var srcType, out var srcSlot, out var dstType, out var dstSlot, out var needsNumeric))
            {
                if (!TryCompanyChestMoveItem(srcType, srcSlot, dstType, dstSlot, needsNumeric))
                {
                    companyChestOrganize.Active = false;
                    return;
                }

                companyChestOrganize.Steps++;
                companyChestOrganize.NextAttemptAtMs = now + (needsNumeric ? 650 : 350);

                if (Configuration.AutoConfirmCompanyChestQuantity && needsNumeric)
                {
                    pendingCompanyChestNumericConfirmUntilMs = now + 1500;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Move;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    ArmSuppressInputNumeric(now, 1500);
                }

                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] (MMB) Company Chest organize step {companyChestOrganize.Steps}: {srcType} slot={srcSlot} -> {dstType} slot={dstSlot} (phase=stack, numeric={needsNumeric}).");
                return;
            }

            // No more merges; move on to compaction.
            companyChestOrganize.Phase = 1;
        }

        // Phase 1: compact items to fill empty slots from the start.
        if (TryFindCompanyChestCompactionMove(pages, out var cSrcType, out var cSrcSlot, out var cDstType, out var cDstSlot))
        {
            if (!TryCompanyChestMoveItem(cSrcType, cSrcSlot, cDstType, cDstSlot, keepAliveForInputNumeric: false))
            {
                companyChestOrganize.Active = false;
                return;
            }

            companyChestOrganize.Steps++;
            companyChestOrganize.NextAttemptAtMs = now + 250;

            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] (MMB) Company Chest organize step {companyChestOrganize.Steps}: {cSrcType} slot={cSrcSlot} -> {cDstType} slot={cDstSlot} (phase=compact).");
            return;
        }

        // Done.
        companyChestOrganize.Active = false;
    }

    private bool TryFindCompanyChestMergeMove(
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType[] pages,
        out FFXIVClientStructs.FFXIV.Client.Game.InventoryType srcType,
        out uint srcSlot,
        out FFXIVClientStructs.FFXIV.Client.Game.InventoryType dstType,
        out uint dstSlot,
        out bool needsNumeric)
    {
        srcType = default;
        srcSlot = 0;
        dstType = default;
        dstSlot = 0;
        needsNumeric = false;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        const int slotCap = 80;

        // Find a destination stack with free space, then a later source stack of same item to merge.
        foreach (var dt in pages)
        {
            for (var di = 0; di < slotCap; di++)
            {
                var d = inv->GetInventorySlot(dt, di);
                if (d == null)
                    break;
                if (d->ItemId == 0 || d->Quantity <= 0)
                    continue;

                var itemId = d->ItemId;
                var isHq = d->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                var maxStack = GetItemStackSize(itemId);
                if (maxStack <= 1)
                    continue;

                var free = (int)maxStack - d->Quantity;
                if (free <= 0)
                    continue;

                // Find a later stack to merge into this one.
                var foundDest = false;
                var destGlobalIndex = 0;
                for (var pi = 0; pi < pages.Length; pi++)
                {
                    if (pages[pi] != dt) continue;
                    destGlobalIndex = pi * slotCap + di;
                    foundDest = true;
                    break;
                }
                if (!foundDest)
                    continue;

                for (var p = 0; p < pages.Length; p++)
                {
                    var st = pages[p];
                    for (var si = 0; si < slotCap; si++)
                    {
                        var s = inv->GetInventorySlot(st, si);
                        if (s == null)
                            break;
                        if (s->ItemId == 0 || s->Quantity <= 0)
                            continue;
                        if (s->ItemId != itemId)
                            continue;
                        var sHq = s->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                        if (sHq != isHq)
                            continue;

                        var srcGlobalIndex = p * slotCap + si;
                        if (srcGlobalIndex <= destGlobalIndex)
                            continue;
                        if (st == dt && si == di)
                            continue;

                        // Merging stacks usually prompts for quantity.
                        srcType = st;
                        srcSlot = (uint)si;
                        dstType = dt;
                        dstSlot = (uint)di;
                        needsNumeric = s->Quantity > 1;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryFindCompanyChestCompactionMove(
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType[] pages,
        out FFXIVClientStructs.FFXIV.Client.Game.InventoryType srcType,
        out uint srcSlot,
        out FFXIVClientStructs.FFXIV.Client.Game.InventoryType dstType,
        out uint dstSlot)
    {
        srcType = default;
        srcSlot = 0;
        dstType = default;
        dstSlot = 0;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        const int slotCap = 80;

        // Find first empty, then next non-empty after it.
        for (var dp = 0; dp < pages.Length; dp++)
        {
            var dt = pages[dp];
            for (var di = 0; di < slotCap; di++)
            {
                var d = inv->GetInventorySlot(dt, di);
                if (d == null)
                    break;
                if (d->ItemId != 0)
                    continue;

                // Found empty destination.
                for (var sp = dp; sp < pages.Length; sp++)
                {
                    var st = pages[sp];
                    var start = sp == dp ? di + 1 : 0;
                    for (var si = start; si < slotCap; si++)
                    {
                        var s = inv->GetInventorySlot(st, si);
                        if (s == null)
                            break;
                        if (s->ItemId == 0 || s->Quantity <= 0)
                            continue;

                        srcType = st;
                        srcSlot = (uint)si;
                        dstType = dt;
                        dstSlot = (uint)di;
                        return true;
                    }
                }

                return false;
            }
        }

        return false;
    }

    private bool TryCompanyChestMoveItem(
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType sourceType,
        uint sourceSlot,
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType destType,
        uint destSlot,
        bool keepAliveForInputNumeric)
    {
        var module = RaptureAtkModule.Instance();
        if (module == null)
            return false;

        // IMPORTANT:
        // HandleItemMove expects InventoryType values (e.g. Inventory1=0, FreeCompanyPage1=20000),
        // not "container ids" like 48/57.
        var srcInvType = (uint)sourceType;
        var dstInvType = (uint)destType;

        nint localValuesAlloc = 0;
        nint localRetAlloc = 0;
        try
        {
            AtkValue* values;
            AtkValue* ret;
            if (keepAliveForInputNumeric)
            {
                // Keep alive across the InputNumeric dialog.
                if (pendingMoveOutValuePtr != 0)
                {
                    try { Marshal.FreeHGlobal(pendingMoveOutValuePtr); } catch { /* ignore */ }
                    pendingMoveOutValuePtr = 0;
                }
                if (pendingMoveAtkValuesPtr != 0)
                {
                    try { Marshal.FreeHGlobal(pendingMoveAtkValuesPtr); } catch { /* ignore */ }
                    pendingMoveAtkValuesPtr = 0;
                }

                pendingMoveOutValuePtr = Marshal.AllocHGlobal(sizeof(AtkValue));
                pendingMoveAtkValuesPtr = Marshal.AllocHGlobal(sizeof(AtkValue) * 4);
                pendingMoveCreatedAtMs = Environment.TickCount64;
                pendingMoveSawInputNumeric = false;
                pendingMoveOutValueFreeAtMs = pendingMoveCreatedAtMs + 8000;

                ret = (AtkValue*)pendingMoveOutValuePtr;
                values = (AtkValue*)pendingMoveAtkValuesPtr;
            }
            else
            {
                localRetAlloc = Marshal.AllocHGlobal(sizeof(AtkValue));
                localValuesAlloc = Marshal.AllocHGlobal(sizeof(AtkValue) * 4);
                ret = (AtkValue*)localRetAlloc;
                values = (AtkValue*)localValuesAlloc;
            }

            ret->Type = AtkValueType.Int;
            ret->Int = 0;

            for (var i = 0; i < 4; i++) values[i].Type = AtkValueType.UInt;
            values[0].UInt = srcInvType;
            values[1].UInt = sourceSlot;
            values[2].UInt = dstInvType;
            values[3].UInt = destSlot;

            module->HandleItemMove(ret, values, 4);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Company Chest HandleItemMove failed.");
            return false;
        }
        finally
        {
            if (localRetAlloc != 0) { try { Marshal.FreeHGlobal(localRetAlloc); } catch { /* ignore */ } }
            if (localValuesAlloc != 0) { try { Marshal.FreeHGlobal(localValuesAlloc); } catch { /* ignore */ } }
        }
    }

    private static bool TryFindCompanyChestFirstEmptySlot(
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType[] pages,
        out FFXIVClientStructs.FFXIV.Client.Game.InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;

        if (pages.Length == 0)
            return false;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        const int slotCap = 80;
        foreach (var t in pages)
        {
            for (var i = 0; i < slotCap; i++)
            {
                var item = inv->GetInventorySlot(t, i);
                if (item == null)
                    break;
                if (item->ItemId == 0)
                {
                    destType = t;
                    destSlot = (uint)i;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindCompanyChestBestStackSlot(
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType[] pages,
        uint itemId,
        bool isHq,
        uint maxStack,
        out FFXIVClientStructs.FFXIV.Client.Game.InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;

        if (pages.Length == 0 || itemId == 0 || maxStack <= 1)
            return false;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        const int slotCap = 80;
        var bestFree = 0;
        foreach (var t in pages)
        {
            for (var i = 0; i < slotCap; i++)
            {
                var it = inv->GetInventorySlot(t, i);
                if (it == null)
                    break;

                if (it->ItemId != itemId)
                    continue;

                var hq = it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                if (hq != isHq)
                    continue;

                var qty = it->Quantity;
                if (qty <= 0)
                    continue;

                var free = (int)maxStack - qty;
                if (free <= 0)
                    continue;

                if (free > bestFree)
                {
                    bestFree = free;
                    destType = t;
                    destSlot = (uint)i;
                }
            }
        }

        return bestFree > 0;
    }

    private bool TrySetInputNumericToMax(AtkUnitBase* inputNumeric, PendingNumericKind kind)
    {
        try
        {
            if (inputNumeric == null)
                return false;
            if (inputNumeric->AtkValues == null || inputNumeric->AtkValuesCount < 7)
                return false;

            var minValue = inputNumeric->AtkValues + 2;
            var maxValue = inputNumeric->AtkValues + 3;
            var defaultValue = inputNumeric->AtkValues + 4;
            var currentValue = inputNumeric->AtkValuesCount > 5 ? (inputNumeric->AtkValues + 5) : null;
            var promptVal = inputNumeric->AtkValues + 6;
            var prompt = promptVal->Type is AtkValueType.String or AtkValueType.ManagedString ? ReadAtkValueString(*promptVal) : string.Empty;

            // Guard: only confirm prompts we expect.
            if (kind == PendingNumericKind.Store && !prompt.Contains("store", StringComparison.OrdinalIgnoreCase))
                return false;
            if (kind == PendingNumericKind.Remove && !prompt.Contains("remove", StringComparison.OrdinalIgnoreCase))
                return false;

            if (minValue->Type != AtkValueType.UInt || maxValue->Type != AtkValueType.UInt || defaultValue->Type != AtkValueType.UInt)
                return false;

            // Set default = max (clamped).
            var min = minValue->UInt;
            var max = maxValue->UInt;
            var desired = max < min ? min : max;
            pendingCompanyChestNumericDesired = desired;

            var beforeDefault = defaultValue->UInt;
            var beforeCurrentUInt = (currentValue != null && currentValue->Type == AtkValueType.UInt) ? currentValue->UInt : 0U;
            var beforeCurrentStr = (currentValue != null && currentValue->Type is (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8))
                ? ReadAtkValueString(*currentValue)
                : string.Empty;

            // Many InputNumeric uses have both "default" and "current" values; set both so OK uses max.
            defaultValue->UInt = desired;
            if (currentValue != null)
            {
                if (currentValue->Type == AtkValueType.UInt)
                {
                    currentValue->UInt = desired;
                }
                else if (currentValue->Type is (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8))
                {
                    // This dialog uses a String "current quantity" slot on your client build.
                    // Overwrite the existing buffer in-place (max is <= 999 so this is safe).
                    var s = desired.ToString();
                    WriteUtf8InPlace(currentValue->String, s);
                }
            }

            // Critical: Some builds don't actually use AtkValues for the editable quantity; they use the NumericInput component's Raw/Evaluated strings.
            // Set that too, if present, so the OK action applies "desired" instead of a stale value (e.g. 2).
            TrySetInputNumericComponentValue(inputNumeric, desired);

            if (Configuration.DebugMode)
            {
                var curType = currentValue != null ? currentValue->Type.ToString() : "n/a";
                var afterCurrentUInt = (currentValue != null && currentValue->Type == AtkValueType.UInt) ? currentValue->UInt : 0U;
                var afterCurrentStr = (currentValue != null && currentValue->Type is (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8))
                    ? ReadAtkValueString(*currentValue)
                    : string.Empty;
                Log.Information($"[QuickTransfer] InputNumeric(Update): prompt='{prompt}', min={min}, max={max}, default {beforeDefault}->{defaultValue->UInt}, currentUInt {beforeCurrentUInt}->{afterCurrentUInt}, currentStr '{beforeCurrentStr}'->'{afterCurrentStr}' (idx5 type {curType})");
            }

            return true;
        }
        catch
        {
            // ignore
            return false;
        }
    }

    private static void TrySetInputNumericComponentValue(AtkUnitBase* inputNumeric, uint desired)
    {
        try
        {
            if (inputNumeric == null)
                return;
            if (inputNumeric->UldManager.NodeList == null)
                return;

            var desiredStr = desired.ToString();

            for (var i = 0; i < inputNumeric->UldManager.NodeListCount; i++)
            {
                var node = inputNumeric->UldManager.NodeList[i];
                if (node == null)
                    continue;

                if ((int)node->Type < 1000)
                    continue;

                var compNode = (AtkComponentNode*)node;
                var comp = compNode->Component;
                if (comp == null)
                    continue;

                if (comp->GetComponentType() != ComponentType.NumericInput)
                    continue;

                var ni = (AtkComponentNumericInput*)comp;

                // RawString / EvaluatedString are Utf8String.
                WriteUtf8StringInPlace(&ni->RawString, desiredStr);
                WriteUtf8StringInPlace(&ni->EvaluatedString, desiredStr);

                // The authoritative value used by OK is the numeric input's internal Value.
                // Setting strings alone can leave the internal Value at its old value (commonly 2).
                ni->SetValue((int)desired);

                // Update cursor to end.
                ni->CursorPos = (ushort)desiredStr.Length;
                ni->SelectionStart = ni->CursorPos;
                ni->SelectionEnd = ni->CursorPos;

                // Only need first numeric input.
                return;
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void WriteUtf8StringInPlace(FFXIVClientStructs.FFXIV.Client.System.String.Utf8String* s, string value)
    {
        if (s == null)
            return;

        WriteUtf8InPlace(s->StringPtr, value);
        s->StringLength = value.Length;
        s->BufUsed = value.Length + 1;
    }

    private static void WriteUtf8InPlace(byte* dst, string value)
    {
        if (dst == null)
            return;

        var bytes = Encoding.UTF8.GetBytes(value);
        // write bytes + null terminator
        for (var i = 0; i < bytes.Length; i++)
            dst[i] = bytes[i];
        dst[bytes.Length] = 0;
    }

    private bool TrySelectRemoveFromCompanyChestContextMenu()
    {
        try
        {
            var ctxAddr = GameGui.GetAddonByName("ContextMenu", 1).Address;
            if (ctxAddr == nint.Zero)
                return false;

            var ctxMenu = (AddonContextMenu*)ctxAddr;
            if (ctxMenu == null)
                return false;

            // Find the list component and pick the row whose label is "Remove".
            // FreeCompanyChest uses a Default context menu, so the AgentInventoryContext index-based selection does not apply.
            for (uint listId = 1; listId <= 6; listId++)
            {
                var list = ctxMenu->GetComponentListById(listId);
                if (list == null)
                    continue;

                var itemCount = list->GetItemCount();
                if (itemCount <= 0 || itemCount > 64)
                    continue;

                for (var i = 0; i < itemCount; i++)
                {
                    var labelPtr = list->GetItemLabel(i);
                    if (labelPtr == null)
                        continue;

                    var label = Marshal.PtrToStringUTF8(new IntPtr(labelPtr))?.TrimEnd('\0') ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(label))
                        continue;

                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] ContextMenu listId={listId} row={i} label='{label}'");

                    if (!label.Equals("Remove", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Trigger via callback payload (matches the inventory context menu pattern).
                    GenerateCallback((AtkUnitBase*)ctxMenu, 0, i, 0U, 0, 0);

                    // Close slightly later (immediate close can cancel the action).
                    pendingCloseContextMenuAtMs = Environment.TickCount64 + 50;

                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] Triggered Company Chest 'Remove' (listId={listId}, row={i}).");
                    return true;
                }
            }

            // Fallback: keep old string-scan (helpful for debugging), but don't attempt a blind click.
            return ContextMenuContainsString((AtkUnitBase*)ctxMenu, "Remove");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Failed to select Remove from Company Chest context menu.");
            return false;
        }
    }

    private bool ContextMenuContainsString(AtkUnitBase* ctxAddon, string needle)
    {
        try
        {
            if (ctxAddon == null || ctxAddon->AtkValues == null || ctxAddon->AtkValuesCount <= 0)
                return false;

            var count = Math.Min((int)ctxAddon->AtkValuesCount, 128);
            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] ContextMenu AtkValuesCount={ctxAddon->AtkValuesCount} (scanning {count}).");
            for (var i = 0; i < count; i++)
            {
                var v = ctxAddon->AtkValues[i];
                if (v.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8))
                    continue;

                var s = ReadAtkValueString(v);
                if (string.IsNullOrWhiteSpace(s))
                    continue;

                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] ContextMenu AtkValue[{i}] = '{s}'");

                if (s.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] ContextMenu contains '{needle}' (found '{s}' at AtkValue[{i}]).");
                    return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    // (removed) GetMoveContainerId:
    // Company Chest transfers must use InventoryType values directly with RaptureAtkModule.HandleItemMove.
    private static bool TryFindFirstCompanyChestEmptySlot(
        out FFXIVClientStructs.FFXIV.Client.Game.InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;

        // This method is static; use all known pages as a fallback, callers should prefer GetCompanyChestInventoryTypes().
        var invTypes = Enum.GetValues<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>()
            .Where(IsCompanyChestType)
            .OrderBy(v => (int)v)
            .ToArray();
        if (invTypes.Length == 0)
            return false;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        // Most FC chest tabs are 50 slots; we keep a conservative cap.
        const int slotCap = 80;

        foreach (var t in invTypes)
        {
            for (var i = 0; i < slotCap; i++)
            {
                var item = inv->GetInventorySlot(t, i);
                if (item == null)
                    break;

                if (item->ItemId == 0)
                {
                    destType = t;
                    destSlot = (uint)i;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindCompanyChestExistingStackSlot(
        uint itemId,
        bool isHq,
        uint maxStack,
        out FFXIVClientStructs.FFXIV.Client.Game.InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;

        var invTypes = Enum.GetValues<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>()
            .Where(IsCompanyChestType)
            .OrderBy(v => (int)v)
            .ToArray();
        if (invTypes.Length == 0)
            return false;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        const int slotCap = 80;
        var bestFree = 0;
        foreach (var t in invTypes)
        {
            for (var i = 0; i < slotCap; i++)
            {
                var it = inv->GetInventorySlot(t, i);
                if (it == null)
                    break;

                if (it->ItemId != itemId)
                continue;

                var hq = it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                if (hq != isHq)
                    continue;

                if (maxStack <= 1)
                    continue;

                var qty = it->Quantity;
                if (qty <= 0)
                    continue;

                var free = (int)maxStack - qty;
                if (free <= 0)
                    continue;

                // Pick the stack with the most free space, to reduce "moves 1 then repeats" behavior.
                if (free > bestFree)
                {
                    bestFree = free;
                    destType = t;
                    destSlot = (uint)i;
                }
            }
        }

        return bestFree > 0;
    }

    private static uint GetItemStackSize(uint itemId)
    {
        try
        {
            // If item isn't known/stackable, return 1.
            if (itemId == 0)
                return 1;

            lock (StackSizeCache)
            {
                if (StackSizeCache.TryGetValue(itemId, out var cached))
                    return cached;
            }

            var sheet = DataManager.GetExcelSheet<Item>();
            if (sheet == null)
                return 999;

            // Item row IDs are base IDs; InventoryItem.ItemId is expected to already be base.
            var row = sheet.GetRow(itemId);
            if (row.RowId == 0)
                return 999;

            // In modern Lumina sheets, Item.StackSize exists.
            var s = row.StackSize;
            var result = s <= 0 ? 1U : (uint)s;
            lock (StackSizeCache)
                StackSizeCache[itemId] = result;
            return result;
        }
        catch
        {
            // Fallback: most stackables are 999, and non-stackables will hit maxStack <= 1 cases anyway.
            return 999;
        }
    }

    private static bool TryGetItemInfo(
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType type,
        int slot,
        out uint itemId,
        out bool isHq,
        out uint quantity)
    {
        itemId = 0;
        isHq = false;
        quantity = 0;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        var it = inv->GetInventorySlot(type, slot);
        if (it == null)
            return false;

        itemId = it->ItemId;
        isHq = it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        quantity = (uint)it->Quantity;
        return itemId != 0;
    }

    private ModifierMode? GetModifierModeLatched(long nowMs)
    {
        const int latchWindowMs = 180;
        if (KeyState[VirtualKey.CONTROL] || nowMs - lastCtrlSeenMs <= latchWindowMs)
            return ModifierMode.Ctrl;
        if (KeyState[VirtualKey.SHIFT] || nowMs - lastShiftSeenMs <= latchWindowMs)
            return ModifierMode.Shift;
        return null;
    }

    private void TryCloseCurrentContextMenu(AgentInventoryContext* agent)
    {
        try
        {
            var agentAddonId = agent->AgentInterface.GetAddonId();
            if (agentAddonId != 0)
            {
                var addon = GetAddonById(agentAddonId);
                if (addon != null)
                {
                    CloseContextMenuAddon(agent, addon);
                    return;
                }
            }
        }
        catch
        {
            // ignore
        }

        // Fallback attempt.
        try
        {
            var cm = GameGui.GetAddonByName("ContextMenu", 1);
            if (!cm.IsNull)
                CloseContextMenuAddon(agent, (AtkUnitBase*)cm.Address);
        }
        catch
        {
            // ignore
        }
    }

    private void DebugDumpContextMenu(AgentInventoryContext* agent, int maxItems)
    {
        try
        {
            var max = Math.Min(Math.Min(agent->ContextItemCount, 64), maxItems);
            for (var i = 0; i < max; i++)
            {
                var param = agent->EventParams[agent->ContexItemStartIndex + i];
                if (param.Type is not (AtkValueType.String or AtkValueType.ManagedString))
                    continue;

                var text = ReadAtkValueString(param);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                Log.Information($"[QuickTransfer] Menu idx={i}: '{text}'");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Failed to dump context menu.");
        }
    }

    private static bool IsPlayerInventoryType(FFXIVClientStructs.FFXIV.Client.Game.InventoryType inventoryType)
        => inventoryType is
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1 or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory2 or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory3 or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4;

    private static bool IsArmouryType(FFXIVClientStructs.FFXIV.Client.Game.InventoryType inventoryType)
        => inventoryType is
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryMainHand or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryOffHand or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryHead or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryBody or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryHands or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryLegs or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryFeets or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryEar or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryNeck or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryWrist or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryRings;

    private static bool IsAddonVisible(string addonName, int index = 1)
    {
        var addon = GameGui.GetAddonByName(addonName, index);
        return !addon.IsNull && addon.IsVisible;
    }

    private static bool IsAddonVisibleAnyIndex(string addonName, int maxIndex = 6)
    {
        for (var i = 1; i <= maxIndex; i++)
        {
            if (IsAddonVisible(addonName, i))
                return true;
        }

        return false;
    }

    private static bool IsAnyAddonVisible(IEnumerable<string> addonNames, int index = 1)
    {
        foreach (var name in addonNames)
        {
            if (IsAddonVisible(name, index))
                return true;
        }

        return false;
    }

    private static bool IsAnyAddonVisibleAnyIndex(IEnumerable<string> addonNames, int maxIndex = 6)
    {
        foreach (var name in addonNames)
        {
            if (IsAddonVisibleAnyIndex(name, maxIndex))
                return true;
        }

        return false;
    }

    private static bool IsInventoryAndSaddlebagOpen()
    {
        var inventoryOpen = IsAddonVisibleAnyIndex("Inventory");
        var saddlebagOpen = IsAddonVisibleAnyIndex("InventoryBuddy") || IsAddonVisibleAnyIndex("InventoryBuddy2");
        return inventoryOpen && saddlebagOpen;
    }

    private static bool IsSaddlebagOpen()
        => IsAddonVisibleAnyIndex("InventoryBuddy") || IsAddonVisibleAnyIndex("InventoryBuddy2");

    private static bool IsSaddlebagType(FFXIVClientStructs.FFXIV.Client.Game.InventoryType inventoryType)
        => inventoryType is
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.SaddleBag1 or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.SaddleBag2;

    private static bool IsRetainerOpen()
    {
        // Common retainer inventory addons.
        // (SimpleTweaks checks "RetainerGrid0" for retainer inventory visibility.)
        return IsAddonVisibleAnyIndex("RetainerGrid0") ||
               IsAddonVisibleAnyIndex("RetainerSellList") ||
               IsAddonVisibleAnyIndex("RetainerGrid");
    }

    private static bool IsRetainerType(FFXIVClientStructs.FFXIV.Client.Game.InventoryType inventoryType)
        => inventoryType is
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage1 or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage2 or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage3 or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage4 or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage5 or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage6 or
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage7;

    private static bool IsCompanyChestOpen()
        => IsAddonVisibleAnyIndex(FreeCompanyChestAddonName);

    private static bool IsCompanyChestType(FFXIVClientStructs.FFXIV.Client.Game.InventoryType inventoryType)
    {
        var name = Enum.GetName(typeof(FFXIVClientStructs.FFXIV.Client.Game.InventoryType), inventoryType);
        if (string.IsNullOrEmpty(name))
            return false;

        // We only want the *item compartments*, not crystals/gil/etc.
        // Observed names: FreeCompanyPage1..FreeCompanyPage5
        return name.StartsWith("FreeCompanyPage", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetVisibleAddon(string addonName, out AtkUnitBase* addon, int maxIndex = 6)
    {
        addon = null;
        for (var i = 1; i <= maxIndex; i++)
        {
            var a = GameGui.GetAddonByName(addonName, i);
            if (!a.IsNull && a.IsVisible)
            {
                addon = (AtkUnitBase*)a.Address;
                return true;
            }
        }

        return false;
    }

    private void CloseContextMenuAddon(AgentInventoryContext* agent, AtkUnitBase* contextMenuAddon)
    {
        try { agent->AgentInterface.Hide(); } catch { /* ignore */ }
        try { contextMenuAddon->Hide(false, true, 0); } catch { /* ignore */ }
        try { atkUnitBaseClose?.Invoke(contextMenuAddon, 0); } catch { /* ignore */ }
    }

    private static bool ContextLabelMatches(AutoContextAction desiredAction, string menuText)
    {
        var t = menuText.Trim();
        static bool Has(string s, string needle) => s.Contains(needle, StringComparison.OrdinalIgnoreCase);

        return desiredAction switch
        {
            AutoContextAction.AddAllToSaddlebag =>
                t.Equals("Add All to Saddlebag", StringComparison.OrdinalIgnoreCase) ||
                (Has(t, "Add All") && Has(t, "Saddlebag")),

            AutoContextAction.RemoveAllFromSaddlebag =>
                t.Equals("Remove All from Saddlebag", StringComparison.OrdinalIgnoreCase) ||
                (Has(t, "Remove All") && Has(t, "Saddlebag")) ||
                (Has(t, "Remove") && Has(t, "Saddlebag")) ||
                t.Equals("Remove All", StringComparison.OrdinalIgnoreCase) ||
                ((Has(t, "Retrieve") || Has(t, "Take out") || Has(t, "Take Out")) && Has(t, "Saddlebag")),

            AutoContextAction.PlaceInArmouryChest =>
                t.Equals("Place in Armoury Chest", StringComparison.OrdinalIgnoreCase) ||
                (Has(t, "Place") && (Has(t, "Armoury") || Has(t, "Armory")) && Has(t, "Chest")),

            AutoContextAction.ReturnToInventory =>
                t.Equals("Return to Inventory", StringComparison.OrdinalIgnoreCase) ||
                (Has(t, "Return") && Has(t, "Inventory")),

            AutoContextAction.EntrustToRetainer =>
                t.Equals("Entrust to Retainer", StringComparison.OrdinalIgnoreCase) ||
                (Has(t, "Entrust") && Has(t, "Retainer")),

            AutoContextAction.RetrieveFromRetainer =>
                t.Equals("Retrieve from Retainer", StringComparison.OrdinalIgnoreCase) ||
                (Has(t, "Retrieve") && Has(t, "Retainer")),

            AutoContextAction.RemoveFromCompanyChest =>
                t.Equals("Remove", StringComparison.OrdinalIgnoreCase) ||
                (Has(t, "Remove") && (Has(t, "Company") || Has(t, "Chest"))) ||
                (Has(t, "Withdraw") && (Has(t, "Company") || Has(t, "Chest"))),

            AutoContextAction.Sort =>
                t.Equals("Sort", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("Sort", StringComparison.OrdinalIgnoreCase),

            _ => false,
        };
    }

    private static string ReadAtkValueString(AtkValue v)
    {
        if (v.String == null)
            return string.Empty;

        try
        {
            // SimpleTweaks-style decoding.
            return Marshal.PtrToStringUTF8(new IntPtr(v.String))?.TrimEnd('\0') ?? string.Empty;
        }
        catch
        {
            return ReadUtf8(v.String);
        }
    }

    private static string ReadUtf8(byte* ptr)
    {
        if (ptr == null)
            return string.Empty;

        var len = 0;
        while (ptr[len] != 0)
            len++;

        return len <= 0 ? string.Empty : Encoding.UTF8.GetString(ptr, len);
    }

    private const int UnitListCount = 18;

    private static AtkUnitBase* GetAddonById(uint id)
    {
        var unitManagers = &AtkStage.Instance()->RaptureAtkUnitManager->AtkUnitManager.DepthLayerOneList;
        for (var i = 0; i < UnitListCount; i++)
        {
            var unitManager = &unitManagers[i];
            for (var j = 0; j < Math.Min(unitManager->Count, unitManager->Entries.Length); j++)
            {
                var unitBase = unitManager->Entries[j].Value;
                if (unitBase != null && unitBase->Id == id)
                    return unitBase;
            }
        }

        return null;
    }

    private static AtkValue* CreateAtkValueArray(params object[] values)
    {
        var atkValues = (AtkValue*)Marshal.AllocHGlobal(values.Length * sizeof(AtkValue));
        if (atkValues == null)
            return null;

        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var v = values[i];
                switch (v)
                {
                    case uint u:
                        atkValues[i].Type = AtkValueType.UInt;
                        atkValues[i].UInt = u;
                        break;
                    case int n:
                        atkValues[i].Type = AtkValueType.Int;
                        atkValues[i].Int = n;
                        break;
                    case float f:
                        atkValues[i].Type = AtkValueType.Float;
                        atkValues[i].Float = f;
                        break;
                    case bool b:
                        atkValues[i].Type = AtkValueType.Bool;
                        atkValues[i].Byte = (byte)(b ? 1 : 0);
                        break;
                    case string s:
                    {
                        atkValues[i].Type = AtkValueType.String;
                        var bytes = Encoding.UTF8.GetBytes(s);
                        var alloc = Marshal.AllocHGlobal(bytes.Length + 1);
                        Marshal.Copy(bytes, 0, alloc, bytes.Length);
                        Marshal.WriteByte(alloc, bytes.Length, 0);
                        atkValues[i].String = (byte*)alloc;
                        break;
                    }
                    default:
                        throw new ArgumentException($"Unsupported AtkValue type {v.GetType()}");
                }
            }
        }
        catch
        {
            Marshal.FreeHGlobal(new IntPtr(atkValues));
            return null;
        }

        return atkValues;
    }

    private static void GenerateCallback(AtkUnitBase* unitBase, params object[] values)
    {
        var atkValues = CreateAtkValueArray(values);
        if (atkValues == null)
            return;

        try
        {
            unitBase->FireCallback((uint)values.Length, atkValues);
        }
        finally
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (atkValues[i].Type == AtkValueType.String)
                    Marshal.FreeHGlobal(new IntPtr(atkValues[i].String));
            }

            Marshal.FreeHGlobal(new IntPtr(atkValues));
        }
    }
}

