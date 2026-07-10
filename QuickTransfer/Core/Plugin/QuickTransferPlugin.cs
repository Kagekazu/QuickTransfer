using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.EzHookManager;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Runtime.InteropServices;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;
using AutoContextAction = QuickTransfer.Framework.ContextMenuHandler.AutoContextAction;
using ModifierBindings = QuickTransfer.Framework.ModifierBindings;
using ModifierMode = QuickTransfer.Framework.ContextMenuHandler.ModifierMode;

namespace QuickTransfer;

public sealed unsafe partial class QuickTransferPlugin : IDalamudPlugin
{
    private readonly Dictionary<int, Dictionary<int, InventoryType>> companyChestSelectedTabCandidates = [];

    // Cache a known-good (type, slot, a4) that successfully produced a populated inventory context menu for a given addon.
    // This allows MMB to "Sort" even when hover payloads are weird/un-decodable, because Sort applies to the container.
    private readonly Dictionary<uint, (InventoryType Type, int Slot, int A4)> lastGoodContextTargetByAddonId = [];

    // Cache the "a4" parameter observed when the game opens inventory context menus.
    // Some UIs (notably ArmouryBoard on some builds) appear to require a non-zero a4 to actually populate items.
    private readonly Dictionary<(uint OwnerAddonId, uint InventoryType), int> observedContextA4 = [];

    // Inventory/armoury uses this; saddlebags often do not, so we also use IContextMenu fallback.
    // Use ClientStructs delegate for better compatibility (per Discord feedback).
    private readonly EzHook<AgentInventoryContext.Delegates.OpenForItemSlot>? openForItemSlotHook;
    private readonly PluginUI pluginUi;

    private readonly WindowSystem windowSystem = new("QuickTransfer");
    private int companyChestBusyHits;
    private long companyChestBusyUntilMs;

    private CompanyChestDepositState companyChestDeposit;

    private CompanyChestOrganizeState companyChestOrganize;
    private int companyChestSelectedTabAtkValueIndex = -1;
    private bool debugPrintedReceiveEventHook;

    private long lastActionTickMs;
    private long lastAltSeenMs;
    private long lastCompanyChestOrganizeSkipLogMs;
    private string lastCompanyChestOrganizeSkipReason = string.Empty;
    private long lastCtrlSeenMs;
    private long lastCursorHitTestLogMs;
    private long lastFcChestTabUnmappedLogMs;
    private (string AddonName, uint AddonId, long SeenAtMs)? lastHoverAddon;
    private string lastHoverAddonName = string.Empty;
    private (InventoryType Page, uint AddonId, long SeenAtMs)? lastHoverCompanyChestPage;
    private (nint DdiPtr, uint AddonId, long SeenAtMs)? lastHoverDdi;
    private long lastMiddleClickSortMs;
    private long lastObservedA4LogMs;
    private long lastReceiveEventDebugLogMs;
    private (InventoryType Page, uint AddonId, long SeenAtMs)? lastSelectedCompanyChestPage;

    private long lastShiftSeenMs;
    private bool lastVkLButtonDown;
    private bool lastVkMButtonDown;
    private bool lastVkRButtonDown;
    private bool lastVkX1ButtonDown;
    private bool lastVkX2ButtonDown;
    private long pendingCloseContextMenuAtMs;
    private bool pendingCompanyChestNumericArmed;
    private int pendingCompanyChestNumericConfirmAttempts;

    private long pendingCompanyChestNumericConfirmUntilMs;
    private uint pendingCompanyChestNumericDesired;
    private bool pendingCompanyChestNumericHalf;
    private bool pendingCompanyChestNumericValueSet;
    private long pendingCompanyChestNumericValueSetAtMs;
    private (string AddonName, long EnqueuedAtMs, ModifierMode Mode)? pendingDeferredDefaultMenu;
    private (nint AgentPtr, nint AddonPtr, long EnqueuedAtMs, ModifierMode Mode)? pendingDeferredMenuClick;
    private (nint AgentPtr, nint AddonPtr, long EnqueuedAtMs)? pendingDeferredSortMenuClick;
    private (InventoryType Type, int Slot, uint AddonId, long EnqueuedAtMs)? pendingMiddleClickSortRequest;
    private long pendingMiddleClickSortUntilMs;
    private nint pendingMoveAtkValuesPtr;
    private long pendingMoveCreatedAtMs;
    private long pendingMoveOutValueFreeAtMs;

    // For stack moves that open InputNumeric, the native operation state must stay alive.
    // If it's stack-allocated, the resulting InputNumeric buttons can become "dead".
    private nint pendingMoveOutValuePtr;
    private bool pendingMoveSawInputNumeric;
    private PendingNumericKind pendingNumericKind;

    // Extra safety for inventory Split dialogs (InventoryExpansion / non-English prompts):
    // When we arm a Split, record the expected "max" value (usually qty-1).
    // Then we can recognize the correct InputNumeric without relying on prompt text.
    private uint pendingSplitExpectedMax;
    private long pendingSplitExpectedUntilMs;

    // IMPORTANT:
    // We suppress by forcing alpha to 0 in PreDraw, which can "stick" because the same addon instance is reused.
    // Therefore we track suppression windows and also restore alpha when not suppressing.
    private long suppressContextMenuUntilMs;
    private long suppressInputNumericUntilMs;

    public QuickTransferPlugin(IDalamudPluginInterface pluginInterface)
    {
        ECommonsMain.Init(pluginInterface, this);

        Configuration = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(Svc.PluginInterface);

        // Config migration: ensure DebugMode defaults to OFF even for existing installs.
        try
        {
            if (Configuration.Version < 3)
            {
                Configuration.DebugMode = false;
                Configuration.Version = 3;
                Configuration.Save();
            }
            else if (Configuration.Version < 4)
            {
                // New v4 fields use property defaults when absent from saved JSON; no reset needed.
                Configuration.Version = 4;
                Configuration.Save();
            }
            else if (Configuration.Version < 5)
            {
                Configuration.Version = 5;
                Configuration.Save();
            }
            else if (Configuration.Version > 5)
            {
                // If the user downgrades, don't overwrite their config; just keep their stored values.
            }
            // Version == 3: still ensure debug isn't accidentally on by default after updates.
            // (User can re-enable it explicitly.)
            // No auto-save here to avoid writing config every startup.
        }
        catch
        {
            // ignore
        }

        Configuration.SanitizeKeybindings();

        pluginUi = new(Configuration);
        windowSystem.AddWindow(pluginUi);

        Svc.Commands.AddHandler(QuickTransferConstants.CommandName, new(OnCommand)
        {
            HelpMessage = "Open QuickTransfer settings"
        });

        Svc.PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        Svc.PluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;

        // Hook using ClientStructs delegate (per Discord feedback for better compatibility)
        try
        {
            var funcPtr = AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot;
            if (funcPtr != null)
            {
                openForItemSlotHook = new(
                    (nint)funcPtr,
                    OpenForItemSlotDetour);
            }
            else
            {
                Svc.Log.Warning("[QuickTransfer] AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot is null - signature may not be resolved");
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] Failed to hook OpenForItemSlot using ClientStructs delegate - falling back to manual signature");
            try
            {
                openForItemSlotHook = new("83 B9 ?? ?? ?? ?? ?? 7E ?? 39 91", OpenForItemSlotDetour);
            }
            catch (Exception ex2)
            {
                Svc.Log.Error(ex2, "[QuickTransfer] Failed to hook OpenForItemSlot with fallback signature");
            }
        }

        // Saddlebags can bypass OpenForItemSlot, so use a safe deferred click via context menu events.
        Svc.ContextMenu.OnMenuOpened += OnContextMenuOpened;
        Svc.Framework.Update += OnFrameworkUpdate;

        // Lifecycle hooks:
        // Register with explicit addon names; wildcard registration is not reliable across Dalamud versions/builds.
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, QuickTransferConstants.InputNumericAddonName, OnInputNumericPreSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, QuickTransferConstants.ContextMenuAddonName, OnAddonPreDraw);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, QuickTransferConstants.InputNumericAddonName, OnAddonPreDraw);
        foreach (var name in QuickTransferConstants.ReceiveEventAddonNames)
        {
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, name, OnAddonReceiveEvent);
        }

        // Listen for system error messages (e.g. "Another player is using the chest") so we can stop FC chest organize/deposit
        // instead of spamming actions.
        Svc.Chat.ChatMessage += OnChatMessage;

        Svc.Log.Information($"Loaded {Svc.PluginInterface.Manifest.Name}.");
        Svc.Log.Information(
            $"[QuickTransfer] DebugMode={Configuration.DebugMode}, Enabled={Configuration.Enabled}, " +
            $"EnableMiddleClickSort={Configuration.EnableMiddleClickSort}, " +
            $"EnableCompanyChest={Configuration.EnableCompanyChest}, " +
            $"EnableCompanyChestMiddleClickOrganize={Configuration.EnableCompanyChestMiddleClickOrganize}");
        if (Configuration.DebugMode)
        {
            try
            {
                var matches = Enum.GetNames<InventoryType>()
                    .Where(n => n.Contains("FreeCompany", StringComparison.OrdinalIgnoreCase) ||
                                n.Contains("Company", StringComparison.OrdinalIgnoreCase) ||
                                n.Contains("Chest", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Svc.Log.Information($"[QuickTransfer] InventoryType names containing Company/Chest: {string.Join(", ", matches)}");
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, "[QuickTransfer] Failed to enumerate InventoryType names (debug).");
            }
        }
    }
    public Configuration Configuration { get; }

    public void Dispose()
    {
        Configuration.PersistIfDirty();

        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        Svc.Chat.ChatMessage -= OnChatMessage;
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreSetup, QuickTransferConstants.InputNumericAddonName, OnInputNumericPreSetup);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, QuickTransferConstants.ContextMenuAddonName, OnAddonPreDraw);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, QuickTransferConstants.InputNumericAddonName, OnAddonPreDraw);
        foreach (var name in QuickTransferConstants.ReceiveEventAddonNames)
        {
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, name, OnAddonReceiveEvent);
        }

        Svc.PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        Svc.PluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;

        windowSystem.RemoveAllWindows();
        Svc.Commands.RemoveHandler(QuickTransferConstants.CommandName);

        ECommonsMain.Dispose();
    }

    private void ArmSuppressContextMenu(long now, int durationMs = 250)
        => suppressContextMenuUntilMs = Math.Max(suppressContextMenuUntilMs, now + durationMs);

    private void ArmSuppressInputNumeric(long now, int durationMs = 1500)
        => suppressInputNumericUntilMs = Math.Max(suppressInputNumericUntilMs, now + durationMs);

    private void OnCommand(string command, string args) => OpenConfigUi();

    private void OpenConfigUi() => pluginUi.IsOpen = true;

    private void OpenForItemSlotDetour(
        AgentInventoryContext* agent,
        InventoryType inventoryType,
        int slot,
        int a4,
        uint addonId)
    {
        openForItemSlotHook?.Original(agent, inventoryType, slot, a4, addonId);

        if (!Configuration.Enabled)
        {
            return;
        }

        // Record observed a4 values so we can reuse them for MMB-driven opens.
        try
        {
            observedContextA4[(addonId, (uint)inventoryType)] = a4;

            // If this call actually produced a context menu, remember it as a safe fallback for MMB sorting.
            if (agent != null && agent->ContextItemCount > 0)
            {
                lastGoodContextTargetByAddonId[addonId] = (inventoryType, slot, a4);
            }

            if (Configuration.DebugMode && Environment.TickCount64 - lastObservedA4LogMs >= 1000)
            {
                lastObservedA4LogMs = Environment.TickCount64;
                Svc.Log.Information($"[QuickTransfer] Observed OpenForItemSlot: type={inventoryType} slot={slot} a4={a4} addonId={addonId} ctxCount={(agent != null ? agent->ContextItemCount : -1)}");
            }
        }
        catch
        {
            // ignore
        }

        // Modifier: Ctrl+RClick (special) or Shift+RClick (default).
        // Ctrl takes priority if both are held. Use a short "latch" so quick taps still work.
        var mode = GetModifierModeLatched(Environment.TickCount64);

        if (mode == null)
        {
            return;
        }

        if (InventoryHelpers.ShouldYieldQuickTransferForRetainerMarket(Configuration, mode.Value, inventoryType))
        {
            if (Configuration.DebugMode)
            {
                Svc.Log.Information("[QuickTransfer] Yielding quick transfer — retainer sell list open.");
            }

            return;
        }

        var saddlebagOpen = InventoryHelpers.IsSaddlebagOpen();
        var retainerOpen = InventoryHelpers.IsRetainerOpen();
        var companyChestOpen = InventoryHelpers.IsCompanyChestOpen();
        var specialOpen = saddlebagOpen || retainerOpen || companyChestOpen;

        // Ctrl is only enabled while a "special" container is open (Saddlebag or Retainer),
        // so Shift/Ctrl can be used to disambiguate behaviors.
        if (mode == ModifierMode.Ctrl && !specialOpen)
        {
            return;
        }

        // Never run Ctrl-mode from saddlebag slots.
        if (mode == ModifierMode.Ctrl && InventoryHelpers.IsSaddlebagType(inventoryType))
        {
            return;
        }

        // Never run Ctrl-mode from retainer slots.
        if (mode == ModifierMode.Ctrl && InventoryHelpers.IsRetainerType(inventoryType))
        {
            return;
        }

        // Never run Ctrl-mode from Company Chest slots.
        if (mode == ModifierMode.Ctrl && InventoryHelpers.IsCompanyChestType(inventoryType))
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - lastActionTickMs < Configuration.TransferCooldownMs)
        {
            return;
        }

        // For Alt (Split), prefer the deferred OnMenuOpened path (more reliable than firing callbacks during OpenForItemSlot).
        if (mode == ModifierMode.Alt)
        {
            return;
        }

        if (mode == ModifierMode.Shift && companyChestOpen && Configuration.EnableCompanyChest)
        {
            // If a quantity dialog is already open, don't start another move.
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out var _))
            {
                return;
            }

            // Deposit: Inventory/Armoury -> Company Chest (UI-driven move).
            // This is handled as a small state machine so stacks can top-off existing stacks and spill into new stacks.
            if (InventoryHelpers.IsCompanyChestDepositSourceType(inventoryType) && StartCompanyChestDeposit(inventoryType, (uint)slot))
            {
                lastActionTickMs = now;
                ContextMenuHandler.TryCloseCurrentContextMenu(agent);
                return;
            }
        }

        if (ContextMenuHandler.TryAutoSelectFromAgent(agent, mode.Value, Configuration, out var chosenText, out var chosenIndex, ref pendingCloseContextMenuAtMs))
        {
            lastActionTickMs = now;
            if (Configuration.DebugMode)
            {
                Svc.Log.Information($"[QuickTransfer] ({mode} + RClick) Selected context action '{chosenText}' (idx={chosenIndex}) via OpenForItemSlot.");
            }

            // Set up Trade quantity auto-confirm if Trade was selected
            if (mode == ModifierMode.Shift &&
                chosenText.Length > 0 &&
                ContextMenuHandler.ContextLabelMatches(AutoContextAction.Trade, chosenText) &&
                InventoryHelpers.IsTradeOpen())
            {
                pendingCompanyChestNumericConfirmUntilMs = now + 1500;
                pendingCompanyChestNumericConfirmAttempts = 0;
                pendingCompanyChestNumericArmed = true;
                pendingNumericKind = PendingNumericKind.Trade;
                pendingCompanyChestNumericValueSet = false;
                pendingCompanyChestNumericValueSetAtMs = 0;
                pendingCompanyChestNumericDesired = 0;
                pendingCompanyChestNumericHalf = false;
                ArmSuppressInputNumeric(now);
            }
            // Set up vendor Sell quantity auto-confirm if Sell was selected
            if (Configuration.AutoConfirmVendorSell &&
                mode == ModifierMode.Shift &&
                chosenText.Length > 0 &&
                ContextMenuHandler.ContextLabelMatches(AutoContextAction.Sell, chosenText) &&
                InventoryHelpers.IsVendorOpen())
            {
                pendingCompanyChestNumericConfirmUntilMs = now + 1500;
                pendingCompanyChestNumericConfirmAttempts = 0;
                pendingCompanyChestNumericArmed = true;
                pendingNumericKind = PendingNumericKind.Sell;
                pendingCompanyChestNumericValueSet = false;
                pendingCompanyChestNumericValueSetAtMs = 0;
                pendingCompanyChestNumericDesired = 0;
                pendingCompanyChestNumericHalf = false;
                ArmSuppressInputNumeric(now);
            }
        }
        else if (Configuration.DebugMode && mode == ModifierMode.Ctrl)
        {
            Svc.Log.Information("[QuickTransfer] (Ctrl + RClick) No matching armoury action found in context menu.");
            ContextMenuHandler.DebugDumpContextMenu(agent, 24);
        }
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (!Configuration.Enabled)
        {
            return;
        }

        var now = Environment.TickCount64;
        var middleSortActive = pendingMiddleClickSortUntilMs > 0 && now <= pendingMiddleClickSortUntilMs;
        var mode = middleSortActive ? null : GetModifierModeLatched(now);

        if (!middleSortActive && mode == null)
        {
            return;
        }

        var saddlebagOpen = InventoryHelpers.IsSaddlebagOpen();
        var retainerOpen = InventoryHelpers.IsRetainerOpen();
        var companyChestOpen = InventoryHelpers.IsCompanyChestOpen();
        var specialOpen = saddlebagOpen || retainerOpen || companyChestOpen;

        if (!middleSortActive && mode == ModifierMode.Ctrl && !specialOpen)
        {
            return;
        }

        if (Configuration.DebugMode)
        {
            Svc.Log.Information($"[QuickTransfer] OnMenuOpened: AddonName='{args.AddonName}', MenuType={args.MenuType}, AgentPtr=0x{args.AgentPtr.ToInt64():X}, AddonPtr=0x{args.AddonPtr.ToInt64():X}");
        }

        // Middle-click "Sort" uses an inventory context menu, but does not require Shift/Ctrl.
        if (middleSortActive && args.MenuType == ContextMenuType.Inventory)
        {
            if (args.AgentPtr != nint.Zero && args.AddonPtr != nint.Zero)
            {
                pendingDeferredSortMenuClick = (args.AgentPtr, args.AddonPtr, now);
                return;
            }
        }

        // Free Company Chest uses MenuType.Default (not Inventory).
        if (args.MenuType == ContextMenuType.Default &&
            (mode == ModifierMode.Shift || mode == ModifierMode.Alt) &&
            Configuration.EnableCompanyChest &&
            string.Equals(args.AddonName, QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase))
        {
            pendingDeferredDefaultMenu = (args.AddonName ?? string.Empty, now, mode.Value);
            return;
        }

        // Only deal with inventory context menus otherwise.
        if (args.MenuType != ContextMenuType.Inventory)
        {
            return;
        }

        if (args.AgentPtr == nint.Zero || args.AddonPtr == nint.Zero)
        {
            return;
        }

        if (mode == null)
        {
            return;
        }

        try
        {
            var agent = (AgentInventoryContext*)args.AgentPtr;
            if (InventoryHelpers.ShouldYieldQuickTransferForRetainerMarket(
                    Configuration,
                    mode.Value,
                    agent->TargetInventoryId))
            {
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information("[QuickTransfer] Yielding deferred quick transfer — retainer sell list open.");
                }

                return;
            }
        }
        catch
        {
            // ignore
        }

        // IMPORTANT: Do not click inside the open event (re-entrancy risk).
        pendingDeferredMenuClick = (args.AgentPtr, args.AddonPtr, Environment.TickCount64, mode.Value);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!Configuration.Enabled)
        {
            return;
        }

        var now = Environment.TickCount64;

        // Poll mouse button state from Win32 and log transitions in DebugMode.
        // This helps diagnose cases where the game doesn't emit click events for MMB.
        var lDown = CursorHoverHelpers.IsMouseButtonDown(0x01); // VK_LBUTTON
        var rDown = CursorHoverHelpers.IsMouseButtonDown(0x02); // VK_RBUTTON
        var mDown = CursorHoverHelpers.IsMouseButtonDown(0x04); // VK_MBUTTON
        var x1Down = CursorHoverHelpers.IsMouseButtonDown(0x05); // VK_XBUTTON1
        var x2Down = CursorHoverHelpers.IsMouseButtonDown(0x06); // VK_XBUTTON2

        var prevL = lastVkLButtonDown;
        var prevR = lastVkRButtonDown;
        var prevM = lastVkMButtonDown;
        var prevX1 = lastVkX1ButtonDown;
        var prevX2 = lastVkX2ButtonDown;

        if (Configuration.DebugMode && (lDown != prevL || rDown != prevR || mDown != prevM || x1Down != prevX1 || x2Down != prevX2))
        {
            Svc.Log.Information($"[QuickTransfer] Win32 mouse state: L={(lDown ? 1 : 0)} R={(rDown ? 1 : 0)} M={(mDown ? 1 : 0)} X1={(x1Down ? 1 : 0)} X2={(x2Down ? 1 : 0)}");
        }

        lastVkLButtonDown = lDown;
        lastVkRButtonDown = rDown;
        lastVkMButtonDown = mDown;
        lastVkX1ButtonDown = x1Down;
        lastVkX2ButtonDown = x2Down;

        // If a "middle-ish" button is pressed (rising edge), queue a sort using the last hovered slot.
        // This works even if the client doesn't generate a distinct UI click event on this build.
        var middleEdge = ModifierBindings.IsMiddleClickEdge(mDown, prevM, x1Down, prevX1, x2Down, prevX2, Configuration);
        if (middleEdge)
        {
            if (ModifierBindings.IsMiddleClickConfigured(Configuration))
            {
                TryQueueMiddleClickSortFromHover(now);
            }
            else if (Configuration.DebugMode)
            {
                Svc.Log.Information("[QuickTransfer] (MMB) Press detected, but middle-click sort is disabled or no mouse buttons are selected.");
            }
        }

        // Modifier latch (helps cases where the user taps Shift/Ctrl quickly).
        if (Svc.KeyState[VirtualKey.SHIFT])
        {
            lastShiftSeenMs = now;
        }
        if (Svc.KeyState[VirtualKey.CONTROL])
        {
            lastCtrlSeenMs = now;
        }
        if (Svc.KeyState[VirtualKey.MENU])
        {
            lastAltSeenMs = now;
        }

        // Quantity prompt auto-confirm (best effort).
        // Trade and Split always auto-confirm; Company Chest and Vendor Sell respect their config settings.
        var shouldAutoConfirm = pendingNumericKind == PendingNumericKind.Trade ||
                                pendingNumericKind == PendingNumericKind.Split ||
                                Configuration.AutoConfirmVendorSell && pendingNumericKind == PendingNumericKind.Sell ||
                                Configuration.AutoConfirmCompanyChestQuantity && pendingNumericKind != PendingNumericKind.None;

        if (shouldAutoConfirm &&
            pendingNumericKind != PendingNumericKind.None &&
            pendingCompanyChestNumericConfirmUntilMs > 0 &&
            now <= pendingCompanyChestNumericConfirmUntilMs)
        {
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out var inputNumeric))
            {
                ArmSuppressInputNumeric(now);
                // Phase 1: set max (and wait a frame so the component commits the value internally).
                if (!pendingCompanyChestNumericValueSet)
                {
                    if (!TrySetInputNumericToMax(inputNumeric, pendingNumericKind))
                    {
                        if (Configuration.DebugMode)
                        {
                            try
                            {
                                var promptVal = inputNumeric->AtkValues + 6;
                                var prompt = promptVal->Type is AtkValueType.String or AtkValueType.ManagedString ? AtkValueHelpers.ReadAtkValueString(*promptVal) : string.Empty;
                                var minVal = inputNumeric->AtkValues + 2;
                                var maxVal = inputNumeric->AtkValues + 3;
                                var min = minVal->Type == AtkValueType.UInt ? minVal->UInt : 0U;
                                var max = maxVal->Type == AtkValueType.UInt ? maxVal->UInt : 0U;
                                Svc.Log.Information($"[QuickTransfer] Auto-confirm InputNumeric skipped (kind={pendingNumericKind}, prompt='{prompt}', min={min}, max={max}, expectedSplitMax={pendingSplitExpectedMax}).");
                            }
                            catch
                            {
                                Svc.Log.Information($"[QuickTransfer] Auto-confirm InputNumeric skipped (kind={pendingNumericKind}).");
                            }
                        }
                        // Prompt doesn't match expectation; stop (prevents confirming wrong dialogs).
                        pendingCompanyChestNumericConfirmUntilMs = 0;
                        pendingCompanyChestNumericArmed = false;
                        pendingNumericKind = PendingNumericKind.None;
                        pendingCompanyChestNumericHalf = false;
                        pendingSplitExpectedMax = 0;
                        pendingSplitExpectedUntilMs = 0;
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
                {
                    return;
                }

                // Re-check prompt + re-apply max right before confirming (cheap + safer).
                if (!TrySetInputNumericToMax(inputNumeric, pendingNumericKind))
                {
                    if (Configuration.DebugMode)
                    {
                        try
                        {
                            var promptVal = inputNumeric->AtkValues + 6;
                            var prompt = promptVal->Type is AtkValueType.String or AtkValueType.ManagedString ? AtkValueHelpers.ReadAtkValueString(*promptVal) : string.Empty;
                            var minVal = inputNumeric->AtkValues + 2;
                            var maxVal = inputNumeric->AtkValues + 3;
                            var min = minVal->Type == AtkValueType.UInt ? minVal->UInt : 0U;
                            var max = maxVal->Type == AtkValueType.UInt ? maxVal->UInt : 0U;
                            Svc.Log.Information($"[QuickTransfer] Auto-confirm InputNumeric aborted (kind={pendingNumericKind}, prompt='{prompt}', min={min}, max={max}, expectedSplitMax={pendingSplitExpectedMax}).");
                        }
                        catch
                        {
                            Svc.Log.Information($"[QuickTransfer] Auto-confirm InputNumeric aborted (kind={pendingNumericKind}).");
                        }
                    }
                    pendingCompanyChestNumericConfirmUntilMs = 0;
                    pendingCompanyChestNumericArmed = false;
                    pendingNumericKind = PendingNumericKind.None;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericHalf = false;
                    pendingSplitExpectedMax = 0;
                    pendingSplitExpectedUntilMs = 0;
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
                        {
                            // Default: confirm max (we already set the numeric input to max above).
                            try
                            {
                                var maxVal = inputNumeric->AtkValues + 3;
                                if (maxVal->Type == AtkValueType.UInt)
                                {
                                    toConfirm = maxVal->UInt;
                                }
                                else if (maxVal->Type == AtkValueType.Int)
                                {
                                    toConfirm = (uint)Math.Max(0, maxVal->Int);
                                }
                            }
                            catch
                            {
                                // ignore
                            }
                            if (toConfirm == 0)
                            {
                                toConfirm = 1;
                            }
                        }
                        inputNumeric->FireCallbackInt((int)toConfirm);
                        pendingCompanyChestNumericConfirmAttempts = 1;
                        if (Configuration.DebugMode)
                        {
                            Svc.Log.Information($"[QuickTransfer] Auto-confirmed InputNumeric attempt 1 (kind={pendingNumericKind}, FireCallbackInt={toConfirm}).");
                        }

                        // Clear state after issuing confirm; the dialog should close itself.
                        pendingCompanyChestNumericConfirmUntilMs = 0;
                        pendingCompanyChestNumericArmed = false;
                        pendingNumericKind = PendingNumericKind.None;
                        pendingCompanyChestNumericValueSet = false;
                        pendingCompanyChestNumericValueSetAtMs = 0;
                        pendingCompanyChestNumericDesired = 0;
                        pendingCompanyChestNumericHalf = false;
                        pendingSplitExpectedMax = 0;
                        pendingSplitExpectedUntilMs = 0;
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
                        pendingCompanyChestNumericHalf = false;
                        pendingSplitExpectedMax = 0;
                        pendingSplitExpectedUntilMs = 0;
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
                    pendingCompanyChestNumericHalf = false;
                    pendingSplitExpectedMax = 0;
                    pendingSplitExpectedUntilMs = 0;
                    suppressInputNumericUntilMs = 0;
                    Svc.Log.Warning(ex, "[QuickTransfer] Failed to auto-confirm InputNumeric.");
                }
            }
            else if (Configuration.AutoConfirmVendorSell && pendingNumericKind == PendingNumericKind.Sell && InventoryHelpers.IsVendorOpen() &&
                     GenericHelpers.TryGetAddonMaster(QuickTransferConstants.SelectYesnoAddonName, out AddonMaster.SelectYesno selectYesno))
            {
                try
                {
                    selectYesno.Yes();
                    if (Configuration.DebugMode)
                    {
                        Svc.Log.Information("[QuickTransfer] Auto-confirmed vendor sell Yes/No dialog (SelectYesno).");
                    }
                }
                catch (Exception ex)
                {
                    if (Configuration.DebugMode)
                    {
                        Svc.Log.Warning(ex, "[QuickTransfer] Failed to auto-confirm SelectYesno.");
                    }
                }
                pendingCompanyChestNumericConfirmUntilMs = 0;
                pendingCompanyChestNumericArmed = false;
                pendingNumericKind = PendingNumericKind.None;
                pendingCompanyChestNumericValueSet = false;
                pendingCompanyChestNumericValueSetAtMs = 0;
                pendingCompanyChestNumericDesired = 0;
                pendingCompanyChestNumericHalf = false;
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
            pendingCompanyChestNumericHalf = false;
            pendingSplitExpectedMax = 0;
            pendingSplitExpectedUntilMs = 0;
            suppressInputNumericUntilMs = 0;
        }

        // If we have a pending "move outValue" buffer for InputNumeric-driven moves, free it once the dialog is gone,
        // or when the timeout expires.
        if (pendingMoveOutValuePtr != 0 || pendingMoveAtkValuesPtr != 0)
        {
            var inputVisible = InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out var _);
            if (inputVisible)
            {
                pendingMoveSawInputNumeric = true;
            }

            // Important: InputNumeric often appears on a subsequent frame.
            // Do NOT free the buffers immediately just because it's not visible yet.
            var graceExpired = pendingMoveCreatedAtMs > 0 && now - pendingMoveCreatedAtMs >= 1500;
            if (pendingMoveSawInputNumeric && !inputVisible || now >= pendingMoveOutValueFreeAtMs || !inputVisible && graceExpired)
            {
                try
                {
                    if (pendingMoveOutValuePtr != 0)
                    {
                        Marshal.FreeHGlobal(pendingMoveOutValuePtr);
                    }
                }
                catch
                {
                    /* ignore */
                }
                try
                {
                    if (pendingMoveAtkValuesPtr != 0)
                    {
                        Marshal.FreeHGlobal(pendingMoveAtkValuesPtr);
                    }
                }
                catch
                {
                    /* ignore */
                }
                pendingMoveOutValuePtr = 0;
                pendingMoveOutValueFreeAtMs = 0;
                pendingMoveAtkValuesPtr = 0;
                pendingMoveCreatedAtMs = 0;
                pendingMoveSawInputNumeric = false;
            }
        }

        // Company Chest deposit state machine (Inventory -> FC Chest).
        if (Configuration.EnableCompanyChest)
        {
            ProcessCompanyChestDeposit(now);
        }

        // Company Chest organize (MMB): auto-stack + compact items within FC chest pages.
        if (Configuration is { EnableCompanyChest: true, EnableCompanyChestMiddleClickOrganize: true })
        {
            ProcessCompanyChestOrganize(now);
        }

        // Middle-click sort: open the context menu on the clicked slot, then auto-select "Sort".
        var mmb = pendingMiddleClickSortRequest;
        if (Configuration.EnableMiddleClickSort && mmb != null && now - mmb.Value.EnqueuedAtMs <= 1500)
        {
            // If the request was for Company Chest, run organize instead (there is no Sort entry on the item menu).
            if (InventoryHelpers.IsCompanyChestType(mmb.Value.Type) && Configuration is { EnableCompanyChest: true, EnableCompanyChestMiddleClickOrganize: true })
            {
                // Only organize the currently selected tab (we use mmb.Value.Type as the selected FreeCompanyPage).
                StartCompanyChestOrganize(now, mmb.Value.Type);
                pendingMiddleClickSortRequest = null;
                pendingMiddleClickSortUntilMs = 0;
            }
            else
            {
                // Safety: never call OpenForItemSlot with unknown inventory types; this can crash the game client.
                if (!InventoryHelpers.IsPlayerInventoryType(mmb.Value.Type) && !InventoryHelpers.IsArmouryType(mmb.Value.Type) && !InventoryHelpers.IsSaddlebagType(mmb.Value.Type) &&
                    !InventoryHelpers.IsRetainerType(mmb.Value.Type) && !InventoryHelpers.IsCompanyChestType(mmb.Value.Type))
                {
                    if (Configuration.DebugMode)
                    {
                        Svc.Log.Information($"[QuickTransfer] (MMB) Refusing to call OpenForItemSlot for unrecognized inventory type={mmb.Value.Type} slot={mmb.Value.Slot} addonId={mmb.Value.AddonId} (crash-prevention).");
                    }
                    pendingMiddleClickSortRequest = null;
                    pendingMiddleClickSortUntilMs = 0;
                    return;
                }

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
                            ArmSuppressContextMenu(now);
                            if (Configuration.DebugMode)
                            {
                                Svc.Log.Information($"[QuickTransfer] (MMB) Calling OpenForItemSlot: type={mmb.Value.Type} slot={mmb.Value.Slot} addonId={mmb.Value.AddonId}");
                            }

                            // Try to open the inventory context menu using the same mysterious "a4" value the game uses.
                            // If we don't have a recorded value yet, try a small set of common candidates.
                            int[] candidates;
                            if (!observedContextA4.TryGetValue((mmb.Value.AddonId, (uint)mmb.Value.Type), out var observedA4))
                            {
                                // Heuristic: armoury boards often need a non-zero a4; try 1 first.
                                candidates = InventoryHelpers.IsArmouryType(mmb.Value.Type) ? [1, 0, 2] : [0, 1, 2];
                            }
                            else
                            {
                                candidates = [observedA4, 0, 1, 2];
                            }

                            var opened = false;
                            var usedA4 = 0;
                            foreach (var a4 in candidates.Distinct())
                            {
                                invCtx->OpenForItemSlot(mmb.Value.Type, mmb.Value.Slot, a4, mmb.Value.AddonId);
                                usedA4 = a4;
                                if (invCtx->ContextItemCount > 0)
                                {
                                    opened = true;
                                    observedContextA4[(mmb.Value.AddonId, (uint)mmb.Value.Type)] = a4;
                                    break;
                                }
                            }

                            // Fallback: don't rely solely on OnMenuOpened firing.
                            try
                            {
                                var cm = AddonHelpers.GetAddonByName("ContextMenu");
                                pendingDeferredSortMenuClick = ((nint)invCtx, cm != null ? (nint)cm : 0, now);
                            }
                            catch
                            {
                                pendingDeferredSortMenuClick = ((nint)invCtx, 0, now);
                            }

                            if (Configuration.DebugMode)
                            {
                                try
                                {
                                    Svc.Log.Information(
                                        $"[QuickTransfer] (MMB) Post OpenForItemSlot: opened={(opened ? 1 : 0)} usedA4={usedA4} ContextItemCount={invCtx->ContextItemCount}, " +
                                        $"OwnerAddonId={invCtx->OwnerAddonId}, BlockingAddonId={invCtx->BlockingAddonId}, " +
                                        $"TargetInv={invCtx->TargetInventoryId}, TargetSlot={invCtx->TargetInventorySlotId}");
                                }
                                catch
                                {
                                    // ignore
                                }
                            }
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
                var cm = AddonHelpers.GetAddonByName("ContextMenu");
                if (cm != null)
                {
                    try { cm->Hide(false, true, 0); }
                    catch
                    {
                        /* ignore */
                    }
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
                (pendingDefault.Value.Mode == ModifierMode.Shift || pendingDefault.Value.Mode == ModifierMode.Alt) &&
                pendingDefault.Value.AddonName.Equals(QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase) &&
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
                    pendingCompanyChestNumericHalf = pendingDefault.Value.Mode == ModifierMode.Alt;
                    ArmSuppressInputNumeric(now);
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

        if (now - pending.Value.EnqueuedAtMs > 1500)
        {
            // Consume (timed out).
            pendingDeferredMenuClick = null;
            return;
        }

        // Give the context menu a moment to populate after OnMenuOpened (InventoryExpansion often needs a frame).
        if (now - pending.Value.EnqueuedAtMs < 50)
        {
            return;
        }

        // Consume (only try once after the short delay).
        pendingDeferredMenuClick = null;

        // If we already acted this tick/window via OpenForItemSlot, don't deref pointers.
        if (now - lastActionTickMs < Configuration.TransferCooldownMs)
        {
            return;
        }

        try
        {
            var agent = (AgentInventoryContext*)pending.Value.AgentPtr;
            if (InventoryHelpers.ShouldYieldQuickTransferForRetainerMarket(
                    Configuration,
                    pending.Value.Mode,
                    agent->TargetInventoryId))
            {
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information("[QuickTransfer] Skipping deferred quick transfer — retainer sell list open.");
                }

                ProcessDeferredSortMenuClick(now);
                return;
            }

            // NOTE:
            // IMenuOpenedArgs.AddonPtr/AddOnName refers to the addon that *opened* the menu (e.g. Inventory/InventoryExpansion),
            // not the context menu addon itself. We must fire callbacks on the actual ContextMenu addon.
            AtkUnitBase* addon = null;
            try
            {
                addon = AddonHelpers.GetAddonByName(QuickTransferConstants.ContextMenuAddonName);
            }
            catch
            {
                // ignore
            }

            // Fallback: keep whatever we were given (older Dalamud builds may have provided the context menu pointer).
            if (addon == null)
            {
                addon = (AtkUnitBase*)pending.Value.AddonPtr;
            }

            if (ContextMenuHandler.TryAutoSelectAndClose(
                agent,
                addon,
                pending.Value.Mode,
                Configuration,
                out var chosenText,
                out var chosenIndex,
                ref pendingCloseContextMenuAtMs))
            {
                lastActionTickMs = now;
                // Split is finicky: keep the menu suppressed longer so it can't be cancelled by an early close/visibility change.
                var suppressMs = (pending.Value.Mode == ModifierMode.Alt && chosenText.Length > 0 && ContextMenuHandler.ContextLabelMatches(AutoContextAction.Split, chosenText))
                    ? 3000
                    : 1500;
                ArmSuppressContextMenu(now, suppressMs);
                if (pending.Value.Mode == ModifierMode.Shift &&
                    chosenText.Length > 0 &&
                    ContextMenuHandler.ContextLabelMatches(AutoContextAction.Trade, chosenText))
                {
                    // Trade: auto-confirm max quantity when InputNumeric appears
                    pendingCompanyChestNumericConfirmUntilMs = now + 1500;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Trade;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    pendingCompanyChestNumericHalf = false;
                    ArmSuppressInputNumeric(now);
                }
                if (Configuration.AutoConfirmVendorSell &&
                    pending.Value.Mode == ModifierMode.Shift &&
                    chosenText.Length > 0 &&
                    ContextMenuHandler.ContextLabelMatches(AutoContextAction.Sell, chosenText) &&
                    InventoryHelpers.IsVendorOpen())
                {
                    // Vendor Sell: auto-confirm max quantity when InputNumeric appears
                    pendingCompanyChestNumericConfirmUntilMs = now + 1500;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Sell;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    pendingCompanyChestNumericHalf = false;
                    ArmSuppressInputNumeric(now);
                }
                if (Configuration.EnableCompanyChest &&
                    pending.Value.Mode == ModifierMode.Shift &&
                    chosenText.Length > 0 &&
                    ContextMenuHandler.ContextLabelMatches(AutoContextAction.RemoveFromCompanyChest, chosenText))
                {
                    pendingCompanyChestNumericConfirmUntilMs = Configuration.AutoConfirmCompanyChestQuantity ? now + 1500 : 0;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Remove;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    pendingCompanyChestNumericHalf = false;
                    ArmSuppressInputNumeric(now);
                }
                if (pending.Value.Mode == ModifierMode.Alt &&
                    chosenText.Length > 0 &&
                    ContextMenuHandler.ContextLabelMatches(AutoContextAction.Split, chosenText))
                {
                    // InventoryExpansion can delay InputNumeric slightly; allow a longer window.
                    pendingCompanyChestNumericConfirmUntilMs = Configuration.AutoConfirmCompanyChestQuantity ? now + 5000 : 0;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Split;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    pendingCompanyChestNumericHalf = true;
                    ArmSuppressInputNumeric(now, 5000);

                    // Record expected split max (qty-1) to recognize the right dialog even if the prompt isn't English.
                    try
                    {
                        var srcType = agent->TargetInventoryId;
                        var srcSlot = agent->TargetInventorySlotId;
                        if (InventoryHelpers.TryGetItemInfo(srcType, srcSlot, out var _, out var _, out var qty) && qty > 1)
                        {
                            pendingSplitExpectedMax = qty - 1;
                            pendingSplitExpectedUntilMs = now + 5000;
                        }
                        else
                        {
                            pendingSplitExpectedMax = 0;
                            pendingSplitExpectedUntilMs = 0;
                        }
                    }
                    catch
                    {
                        pendingSplitExpectedMax = 0;
                        pendingSplitExpectedUntilMs = 0;
                    }
                }
                if (Configuration.DebugMode)
                {
                    Svc.Log.Information($"[QuickTransfer] ({pending.Value.Mode} + RClick) Selected context action '{chosenText}' (idx={chosenIndex}) via deferred OnMenuOpened.");
                }
            }
            else if (Configuration.DebugMode && pending.Value.Mode == ModifierMode.Ctrl)
            {
                Svc.Log.Information("[QuickTransfer] (Ctrl + RClick) Deferred menu opened but no matching 'Place in Armoury Chest' action was found.");
                ContextMenuHandler.DebugDumpContextMenu(agent, 24);
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] Deferred menu select failed.");
        }

        // Also process a pending sort click (if any) after normal transfers.
        ProcessDeferredSortMenuClick(now);
    }

    private void OnAddonPreDraw(AddonEvent type, AddonArgs args)
    {
        try
        {
            var name = args.AddonName;
            var now = Environment.TickCount64;

            if (string.Equals(name, QuickTransferConstants.ContextMenuAddonName, StringComparison.OrdinalIgnoreCase))
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                if (now <= suppressContextMenuUntilMs)
                {
                    AtkValueHelpers.MakeAddonInvisible(addon);
                }
                else
                {
                    AtkValueHelpers.MakeAddonVisible(addon);
                }
            }

            if (string.Equals(name, QuickTransferConstants.InputNumericAddonName, StringComparison.OrdinalIgnoreCase))
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                if (now <= suppressInputNumericUntilMs)
                {
                    AtkValueHelpers.MakeAddonInvisible(addon);
                }
                else
                {
                    AtkValueHelpers.MakeAddonVisible(addon);
                }
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
            if (!Configuration.Enabled || !ModifierBindings.IsMiddleClickConfigured(Configuration))
            {
                return;
            }

            if (args is not AddonReceiveEventArgs recv)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (Configuration.DebugMode && !debugPrintedReceiveEventHook)
            {
                debugPrintedReceiveEventHook = true;
                try { Svc.Chat.Print("[QuickTransfer] ReceiveEvent hook active (MMB debug)."); }
                catch
                {
                    /* ignore */
                }
                Svc.Log.Information("[QuickTransfer] ReceiveEvent hook active (MMB debug).");
            }

            var eventType = (AtkEventType)recv.AtkEventType;
            var eventData = (AtkEventData*)recv.AtkEventData;
            var mouseButtonId = eventData != null ? eventData->MouseData.ButtonId : (byte)255;
            var dragDropMouseButtonId = eventData != null ? eventData->DragDropData.MouseButtonId : (byte)255;

            // Track last-hovered dragdrop (for polling-based triggers).
            // IMPORTANT:
            // - For ArmouryBoard, only capture from drag-drop rollover/click (avoids bad union reads on some builds).
            // - For Inventory/Saddlebags, we also allow MouseOver by resolving the DDI from atkEvent->Node (safe path).
            var addonName = args.AddonName;
            var allowMouseOverCapture =
                addonName.Equals("Inventory", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("InventoryBuddy", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("InventoryBuddy2", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("RetainerGrid0", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("RetainerGrid", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("RetainerSellList", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals(QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase);

            // Always track which addon the cursor is currently interacting with, even if we can't resolve a DDI.
            // This enables a safe MMB "Sort" path that doesn't dereference drag/drop pointers.
            if (eventType is AtkEventType.MouseOver or AtkEventType.MouseOut or AtkEventType.DragDropRollOver or AtkEventType.DragDropRollOut or
                AtkEventType.ListItemRollOver or AtkEventType.ListItemRollOut)
            {
                try
                {
                    var ab = (AtkUnitBase*)args.Addon.Address;
                    var id = ab != null ? ab->Id : 0u;
                    if (eventType is AtkEventType.MouseOut or AtkEventType.DragDropRollOut or AtkEventType.ListItemRollOut)
                    {
                        lastHoverAddon = null;
                    }
                    else if (id != 0)
                    {
                        lastHoverAddon = (addonName, id, now);
                    }
                }
                catch
                {
                    // ignore
                }
            }

            // FreeCompanyChest: remember which compartment tab is selected based on its button click param.
            // This allows MMB organize to operate ONLY on the active tab, even if you don't hover an item slot first.
            if (addonName.Equals(QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase) &&
                eventType == AtkEventType.ButtonClick)
            {
                try
                {
                    var ab = (AtkUnitBase*)args.Addon.Address;
                    var id = ab != null ? ab->Id : 0u;
                    if (id != 0 && TryMapCompanyChestTabParamToPage(recv.EventParam, out var selectedPage))
                    {
                        lastSelectedCompanyChestPage = (selectedPage, id, now);
                        ObserveCompanyChestTabFromAtkValues(ab, selectedPage);
                        if (Configuration.DebugMode && now - lastReceiveEventDebugLogMs >= 250)
                        {
                            Svc.Log.Information($"[QuickTransfer] FC Chest selected tab: param={recv.EventParam} -> {selectedPage} (addonId={id})");
                        }

                        // If we're currently organizing a different tab, stop immediately.
                        if (companyChestOrganize.Active &&
                            (companyChestOrganize.OwnerAddonId == 0 || companyChestOrganize.OwnerAddonId == id) &&
                            companyChestOrganize.Pages is { Length: 1 } &&
                            companyChestOrganize.Pages[0] != selectedPage)
                        {
                            companyChestOrganize.Active = false;
                            companyChestOrganize.WaitingForApply = false;
                            companyChestOrganize.WaitObservedChangeAtMs = 0;
                            if (Configuration.DebugMode)
                            {
                                Svc.Log.Information($"[QuickTransfer] (MMB) Company Chest tab changed to {selectedPage}; stopping previous organize run.");
                            }
                        }

                        if (companyChestDeposit.Active &&
                            companyChestDeposit.DestPage != default &&
                            companyChestDeposit.DestPage != selectedPage)
                        {
                            companyChestDeposit.Active = false;
                            if (Configuration.DebugMode)
                            {
                                Svc.Log.Information($"[QuickTransfer] (Shift+RClick) Company Chest tab changed to {selectedPage}; stopping deposit run.");
                            }
                        }
                    }
                    else if (Configuration.DebugMode && id != 0 && now - lastFcChestTabUnmappedLogMs >= 250)
                    {
                        lastFcChestTabUnmappedLogMs = now;
                        Svc.Log.Information($"[QuickTransfer] FC Chest tab param unmapped: param={recv.EventParam} (addonId={id})");
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (eventType is AtkEventType.DragDropRollOut || allowMouseOverCapture && eventType is AtkEventType.MouseOut)
            {
                lastHoverDdi = null;
                lastHoverAddonName = string.Empty;
            }
            else if (eventType is AtkEventType.DragDropRollOver or AtkEventType.DragDropClick ||
                     allowMouseOverCapture && eventType is AtkEventType.MouseOver)
            {
                if (DragDropHelpers.TryGetDragDropInterfaceFromReceiveEvent(args, recv, eventType, eventData, out var hAddonId, out var hDdi) && hDdi != null)
                {
                    var ptr = (nint)hDdi;
                    if (ptr >= QuickTransferConstants.MinLikelyPointer)
                    {
                        lastHoverDdi = (ptr, hAddonId, now);
                        lastHoverAddonName = addonName;
                    }

                    // For FC Chest, decode the hovered page while the payload is fresh.
                    if (addonName.Equals(QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (DragDropHelpers.TryGetSlotFromDragDropInterface(hDdi, out var hoverInvType, out var _))
                            {
                                if (InventoryHelpers.IsCompanyChestDestinationType(hoverInvType))
                                {
                                    lastHoverCompanyChestPage = (hoverInvType, hAddonId, now);
                                }
                            }
                        }
                        catch
                        {
                            // ignore
                        }
                    }

                    // Debug: confirm hover capture occasionally.
                    if (Configuration.DebugMode && now - lastReceiveEventDebugLogMs >= 250)
                    {
                        Svc.Log.Information($"[QuickTransfer] HoverCapture: Addon='{args.AddonName}', EventType={eventType}, Param={recv.EventParam}, DDI=0x{(nint)hDdi:X}");
                    }
                }
            }

            // Determine middle-click reliably:
            // - Prefer Win32 VK_MBUTTON (works regardless of client button id mapping)
            // - Fall back to the event's button flags (some builds use bitmask: middle=0x04)
            // - As a final fallback, try Dalamud KeyState (may not include mouse buttons on some builds)
            bool? middleDown = null;
            try
            {
                const VirtualKey vkMButton = (VirtualKey)0x04; // VK_MBUTTON
                middleDown = Svc.KeyState[vkMButton];
            }
            catch
            {
                // ignore
            }

            var asyncMiddleDown = ModifierBindings.IsConfiguredMiddleClickDown(Configuration);
            var isMiddleByMask = ModifierBindings.IsMiddleClickEventMask(mouseButtonId, dragDropMouseButtonId, Configuration);
            var isMiddle = ModifierBindings.IsMiddleClickPressed(Configuration, mouseButtonId, dragDropMouseButtonId, middleDown);

            // Always log (rate-limited) in DebugMode so we can see which event types fire on MMB for this client.
            if (Configuration.DebugMode && now - lastReceiveEventDebugLogMs >= 250)
            {
                lastReceiveEventDebugLogMs = now;
                Svc.Log.Information(
                    $"[QuickTransfer] PreReceiveEvent: Addon='{args.AddonName}', Type={eventType}, Param={recv.EventParam}, " +
                    $"MouseBtn={mouseButtonId} (0x{mouseButtonId:X2}), DragBtn={dragDropMouseButtonId} (0x{dragDropMouseButtonId:X2}), " +
                    $"MaskMiddle={(isMiddleByMask ? "1" : "0")}, AsyncMiddle={(asyncMiddleDown ? "1" : "0")}, KeyStateMiddle={middleDown?.ToString() ?? "n/a"}");
            }

            if (now - lastMiddleClickSortMs < 250)
            {
                return;
            }

            // Only proceed on click events; other events can be noisy and don't carry slot payloads.
            if (eventType is not AtkEventType.DragDropClick and
                not AtkEventType.MouseClick and
                not AtkEventType.MouseDown)
            {
                return;
            }

            if (!isMiddle)
            {
                return;
            }

            if (!DragDropHelpers.TryGetDragDropInterfaceFromReceiveEvent(args, recv, eventType, eventData, out var addonId, out var ddi))
            {
                return;
            }
            if (!DragDropHelpers.TryGetSlotFromDragDropInterface(ddi, out var invType, out var slot))
            {
                return;
            }

            // Do not require a non-empty slot; "Sort" can be invoked from empty slots/spaces.

            pendingMiddleClickSortRequest = (invType, slot, addonId, now);
            pendingMiddleClickSortUntilMs = now + 1500;
            lastMiddleClickSortMs = now;

            // Prevent the underlying UI from processing the click further.
            var atkEvent2 = (AtkEvent*)recv.AtkEvent;
            if (atkEvent2 != null)
            {
                atkEvent2->SetEventIsHandled();
            }
        }
        catch
        {
            // ignore
        }
    }

    // Use AtkValueHelpers (implementation moved to AtkValueHelpers.cs)

    // Company Chest transfers must use InventoryType values directly with RaptureAtkModule.HandleItemMove.

    private ModifierMode? GetModifierModeLatched(long nowMs)
    {
        if (Configuration.EnableAltSplit && IsModifierActive(Configuration.AltActionModifier, nowMs))
        {
            return ModifierMode.Alt;
        }

        if (Configuration.EnableCtrlArmoury && IsModifierActive(Configuration.CtrlActionModifier, nowMs))
        {
            return ModifierMode.Ctrl;
        }

        return Configuration.EnableShiftQuickTransfer && IsModifierActive(Configuration.ShiftActionModifier, nowMs)
            ? ModifierMode.Shift
            : null;
    }

    private bool IsModifierActive(VirtualKey key, long nowMs)
    {
        if (Svc.KeyState[key])
        {
            return true;
        }

        var latchWindowMs = Configuration.ModifierLatchMs;
        return key switch
        {
            VirtualKey.SHIFT => nowMs - lastShiftSeenMs <= latchWindowMs,
            VirtualKey.CONTROL => nowMs - lastCtrlSeenMs <= latchWindowMs,
            VirtualKey.MENU => nowMs - lastAltSeenMs <= latchWindowMs,
            _ => false
        };
    }
}
