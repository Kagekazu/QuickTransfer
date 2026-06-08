using Dalamud.Configuration;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.EzHookManager;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;
using AutoContextAction = QuickTransfer.ContextMenuHandler.AutoContextAction;
using ModifierMode = QuickTransfer.ContextMenuHandler.ModifierMode;

namespace QuickTransfer;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public bool Enabled { get; set; } = true;
    // Default OFF (explicitly requested).
    public bool DebugMode { get; set; }
    public int TransferCooldownMs { get; set; } = 200;

    public bool EnableMiddleClickSort { get; set; } = true;
    public bool EnableCompanyChestMiddleClickOrganize { get; set; } = true;

    public bool EnableCompanyChest { get; set; } = true;
    public bool AutoConfirmCompanyChestQuantity { get; set; } = true;
    public int CompanyChestCompartments { get; set; } = 3; // 3..5 (default game starts at 3)

    public bool EnableVendorQuickSell { get; set; } = true;
    public bool AutoConfirmVendorSell { get; set; } = true;
    public int Version { get; set; } = 3;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

public sealed unsafe class Plugin : IDalamudPlugin
{
    private readonly Dictionary<int, Dictionary<int, InventoryType>> companyChestSelectedTabCandidates = new();
    private readonly QuickTransferWindow configWindow;

    // Cache a known-good (type, slot, a4) that successfully produced a populated inventory context menu for a given addon.
    // This allows MMB to "Sort" even when hover payloads are weird/un-decodable, because Sort applies to the container.
    private readonly Dictionary<uint, (InventoryType Type, int Slot, int A4)> lastGoodContextTargetByAddonId = new();

    // Cache the "a4" parameter observed when the game opens inventory context menus.
    // Some UIs (notably ArmouryBoard on some builds) appear to require a non-zero a4 to actually populate items.
    private readonly Dictionary<(uint OwnerAddonId, uint InventoryType), int> observedContextA4 = new();

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

    // Inventory/armoury uses this; saddlebags often do not, so we also use IContextMenu fallback.
    // Use ClientStructs delegate for better compatibility (per Discord feedback).
    private EzHook<AgentInventoryContext.Delegates.OpenForItemSlot>? openForItemSlotHook;
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

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Config migration: ensure DebugMode defaults to OFF even for existing installs.
        try
        {
            if (Configuration.Version < 3)
            {
                Configuration.DebugMode = false;
                Configuration.Version = 3;
                Configuration.Save();
            }
            else if (Configuration.Version > 3)
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

        configWindow = new(Configuration);
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(QuickTransferConstants.CommandName, new(OnCommand)
        {
            HelpMessage = "Open QuickTransfer settings"
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;

        // Hook using ClientStructs delegate (per Discord feedback for better compatibility)
        try
        {
            delegate* unmanaged<AgentInventoryContext*, InventoryType, int, int, uint, void> funcPtr = AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot;
            if (funcPtr != null)
            {
                openForItemSlotHook = new EzHook<AgentInventoryContext.Delegates.OpenForItemSlot>(
                    (nint)funcPtr,
                    OpenForItemSlotDetour);
            }
            else
            {
                Log.Warning("[QuickTransfer] AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot is null - signature may not be resolved");
            }
        }
        catch(Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Failed to hook OpenForItemSlot using ClientStructs delegate - falling back to manual signature");
            try
            {
                openForItemSlotHook = new EzHook<AgentInventoryContext.Delegates.OpenForItemSlot>(
                    "83 B9 ?? ?? ?? ?? ?? 7E ?? 39 91",
                    OpenForItemSlotDetour);
            }
            catch(Exception ex2)
            {
                Log.Error(ex2, "[QuickTransfer] Failed to hook OpenForItemSlot with fallback signature");
            }
        }

        // Saddlebags can bypass OpenForItemSlot, so use a safe deferred click via context menu events.
        ContextMenu.OnMenuOpened += OnContextMenuOpened;
        Framework.Update += OnFrameworkUpdate;

        // Lifecycle hooks:
        // Register with explicit addon names; wildcard registration is not reliable across Dalamud versions/builds.
        AddonLifecycle.RegisterListener(AddonEvent.PreSetup, QuickTransferConstants.InputNumericAddonName, OnInputNumericPreSetup);
        AddonLifecycle.RegisterListener(AddonEvent.PreDraw, QuickTransferConstants.ContextMenuAddonName, OnAddonPreDraw);
        AddonLifecycle.RegisterListener(AddonEvent.PreDraw, QuickTransferConstants.InputNumericAddonName, OnAddonPreDraw);
        foreach(string name in QuickTransferConstants.ReceiveEventAddonNames)
            AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, name, OnAddonReceiveEvent);

        // Listen for system error messages (e.g. "Another player is using the chest") so we can stop FC chest organize/deposit
        // instead of spamming actions.
        ChatGui.ChatMessage += OnChatMessage;

        Log.Information($"Loaded {PluginInterface.Manifest.Name}.");
        Log.Information(
            $"[QuickTransfer] DebugMode={Configuration.DebugMode}, Enabled={Configuration.Enabled}, " +
            $"EnableMiddleClickSort={Configuration.EnableMiddleClickSort}, " +
            $"EnableCompanyChest={Configuration.EnableCompanyChest}, " +
            $"EnableCompanyChestMiddleClickOrganize={Configuration.EnableCompanyChestMiddleClickOrganize}");
        if (Configuration.DebugMode)
        {
            try
            {
                string[] matches = Enum.GetNames<InventoryType>()
                    .Where(n => n.Contains("FreeCompany", StringComparison.OrdinalIgnoreCase) ||
                                n.Contains("Company", StringComparison.OrdinalIgnoreCase) ||
                                n.Contains("Chest", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Log.Information($"[QuickTransfer] InventoryType names containing Company/Chest: {string.Join(", ", matches)}");
            }
            catch(Exception ex)
            {
                Log.Warning(ex, "[QuickTransfer] Failed to enumerate InventoryType names (debug).");
            }
        }
    }
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    public Configuration Configuration { get; }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        ChatGui.ChatMessage -= OnChatMessage;
        AddonLifecycle.UnregisterListener(AddonEvent.PreSetup, QuickTransferConstants.InputNumericAddonName, OnInputNumericPreSetup);
        AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, QuickTransferConstants.ContextMenuAddonName, OnAddonPreDraw);
        AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, QuickTransferConstants.InputNumericAddonName, OnAddonPreDraw);
        foreach(string name in QuickTransferConstants.ReceiveEventAddonNames)
            AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, name, OnAddonReceiveEvent);

        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;

        windowSystem.RemoveAllWindows();
        configWindow.Dispose();

        CommandManager.RemoveHandler(QuickTransferConstants.CommandName);

        ECommonsMain.Dispose();
    }

    // Win32: cursor position for addon hit-testing (Dalamud does not expose client-space cursor coords).
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hWnd, ref Point lpPoint);

    private static bool IsMouseButtonDown(int virtualKey)
    {
        try
        {
            return GenericHelpers.IsKeyPressed(virtualKey);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetClientCursorPos(out short x, out short y)
    {
        x = 0;
        y = 0;
        try
        {
            if (!GetCursorPos(out Point p))
                return false;

            nint hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == nint.Zero)
                return false;

            if (!ScreenToClient(hwnd, ref p))
                return false;

            if (p.X < short.MinValue || p.X > short.MaxValue || p.Y < short.MinValue || p.Y > short.MaxValue)
                return false;

            x = (short)p.X;
            y = (short)p.Y;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryUpdateLastHoverAddonFromCollisionManager(long now)
    {
        try
        {
            AtkStage* stage = AtkStage.Instance();
            if (stage == null || stage->AtkCollisionManager == null)
                return false;

            AtkUnitBase* hit = stage->AtkCollisionManager->IntersectingAddon;
            if (hit == null || hit->Id == 0)
                return false;

            // We can't reliably compare addon pointers here:
            // - The collision manager can report child addons/overlays
            // - Some users have addon indices > 6
            //
            // Instead, map via Id/HostId/ParentId to a known *owner* addon window.
            bool TryGetVisibleAddonId(string name, out uint id)
            {
                id = 0;
                try
                {
                    if (InventoryHelpers.TryGetVisibleAddon(name, out AtkUnitBase* a, QuickTransferConstants.WideAddonSearchMaxIndex) && a != null && a->Id != 0)
                    {
                        id = a->Id;
                        return true;
                    }
                }
                catch
                {
                    // ignore
                }

                return false;
            }

            Dictionary<uint, string> visibleById = new(capacity: 16);
            void AddVisible(string name)
            {
                if (TryGetVisibleAddonId(name, out uint id) && id != 0)
                {
                    // Don't overwrite an existing mapping. This prevents rare mis-labeling if an alias query
                    // accidentally returns an unexpected addon that reuses an id already mapped earlier.
                    visibleById.TryAdd(id, name);
                }
            }

            AddVisible("Inventory");
            AddVisible("InventoryBuddy");
            AddVisible("InventoryBuddy2");
            AddVisible("RetainerGrid0");
            AddVisible("RetainerGrid");
            AddVisible("RetainerSellList");
            AddVisible(QuickTransferConstants.FreeCompanyChestAddonName);
            foreach(string n in QuickTransferConstants.ArmouryAddonNames)
                AddVisible(n);

            uint hitId = hit->Id;
            uint hostId = hit->HostId;
            uint parentId = hit->ParentId;

            uint ownerId = 0;
            string ownerName = string.Empty;
            string ownerSource = string.Empty;

            bool Pick(uint id)
            {
                if (id == 0)
                    return false;
                if (!visibleById.TryGetValue(id, out string? n))
                    return false;
                ownerId = id;
                ownerName = n;
                ownerSource = "visible";
                return true;
            }

            // Prefer direct hit, then host, then parent.
            if (!Pick(hitId) && !Pick(hostId) && !Pick(parentId))
            {
                static string InferOwnerNameFromInvType(InventoryType t)
                {
                    if (InventoryHelpers.IsPlayerInventoryType(t))
                        return "Inventory";
                    if (InventoryHelpers.IsSaddlebagType(t))
                        return "InventoryBuddy";
                    if (InventoryHelpers.IsArmouryType(t))
                        return "ArmouryBoard";
                    if (InventoryHelpers.IsCompanyChestType(t))
                        return QuickTransferConstants.FreeCompanyChestAddonName;
                    if (InventoryHelpers.IsRetainerType(t))
                        return "RetainerGrid0";
                    return string.Empty;
                }

                bool PickFromLastGood(uint id)
                {
                    if (id == 0)
                        return false;
                    if (!lastGoodContextTargetByAddonId.TryGetValue(id, out (InventoryType Type, int Slot, int A4) good))
                        return false;

                    string inferred = InferOwnerNameFromInvType(good.Type);
                    if (string.IsNullOrEmpty(inferred))
                        return false;

                    ownerId = id;
                    ownerName = inferred;
                    ownerSource = "lastGood";
                    return true;
                }

                // If the owner window isn't visible via addon lookup (common for Inventory), fall back to previously observed "good" targets.
                if (!PickFromLastGood(hitId) && !PickFromLastGood(hostId) && !PickFromLastGood(parentId))
                {
                    // Heuristic: Inventory's owner addon id is commonly 17, while collision hits are child ids (e.g. 108/110)
                    // with HostId/ParentId pointing at 17. Prefer that to make MMB Inventory sort work even without a prior RClick.
                    const uint inventoryOwnerId = 17;
                    if (hostId == inventoryOwnerId || parentId == inventoryOwnerId)
                    {
                        ownerId = inventoryOwnerId;
                        ownerName = "Inventory";
                        ownerSource = "heuristic17";
                    }
                    else
                    {
                        // If we couldn't map to a known owner addon, still log what we saw to help diagnose.
                        if (Configuration.DebugMode && now - lastCursorHitTestLogMs >= 1000)
                        {
                            lastCursorHitTestLogMs = now;
                            Log.Information($"[QuickTransfer] (MMB) CollisionManager hit addonId={hitId} hostId={hostId} parentId={parentId} (unmapped). Visible owners=[{string.Join(", ", visibleById.Select(kv => $"{kv.Value}:{kv.Key}"))}] lastGoodOwnerIds=[{string.Join(", ", lastGoodContextTargetByAddonId.Keys.Take(24))}]");
                        }
                        return false;
                    }
                }
            }

            lastHoverAddon = (ownerName, ownerId, now);

            if (Configuration.DebugMode && now - lastCursorHitTestLogMs >= 1000)
            {
                lastCursorHitTestLogMs = now;
                Log.Information($"[QuickTransfer] (MMB) CollisionManager picked addon '{ownerName}' (ownerAddonId={ownerId}, hitAddonId={hitId}, source={ownerSource}).");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryUpdateLastHoverAddonFromCursorHitTest(long now)
    {
        try
        {
            // Prefer the game's own collision manager; it already knows what addon is under the cursor.
            if (TryUpdateLastHoverAddonFromCollisionManager(now))
                return true;

            if (!TryGetClientCursorPos(out short x, out short y))
                return false;

            AtkUnitBase* best = null;
            string bestName = string.Empty;
            uint bestId = 0;
            uint bestDepth = 0;
            ushort bestDraw = 0;

            void Consider(string? name, AtkUnitBase* a)
            {
                if (a == null)
                    return;

                try
                {
                    if (!a->IsVisible || !a->IsReady)
                        return;

                    if (!a->CheckWindowCollisionAtCoords(x, y))
                        return;

                    uint depth = a->DepthLayer;
                    ushort draw = a->DrawOrderIndex;
                    if (best == null || depth > bestDepth || (depth == bestDepth && draw > bestDraw))
                    {
                        best = a;
                        bestName = name ?? string.Empty;
                        bestId = a->Id;
                        bestDepth = depth;
                        bestDraw = draw;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (InventoryHelpers.TryGetVisibleAddon("Inventory", out AtkUnitBase* inv) && inv != null)
                Consider("Inventory", inv);

            if (InventoryHelpers.TryGetVisibleAddon("InventoryBuddy", out AtkUnitBase* sb) && sb != null)
                Consider("InventoryBuddy", sb);
            if (InventoryHelpers.TryGetVisibleAddon("InventoryBuddy2", out AtkUnitBase* sb2) && sb2 != null)
                Consider("InventoryBuddy2", sb2);

            if (InventoryHelpers.TryGetVisibleAddon("RetainerGrid0", out AtkUnitBase* rg0, QuickTransferConstants.WideAddonSearchMaxIndex) && rg0 != null)
                Consider("RetainerGrid0", rg0);
            if (InventoryHelpers.TryGetVisibleAddon("RetainerGrid", out AtkUnitBase* rg, QuickTransferConstants.WideAddonSearchMaxIndex) && rg != null)
                Consider("RetainerGrid", rg);
            if (InventoryHelpers.TryGetVisibleAddon("RetainerSellList", out AtkUnitBase* rsl, QuickTransferConstants.WideAddonSearchMaxIndex) && rsl != null)
                Consider("RetainerSellList", rsl);

            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out AtkUnitBase* fcc, QuickTransferConstants.WideAddonSearchMaxIndex) && fcc != null)
                Consider(QuickTransferConstants.FreeCompanyChestAddonName, fcc);

            foreach(string n in QuickTransferConstants.ArmouryAddonNames)
            {
                if (InventoryHelpers.TryGetVisibleAddon(n, out AtkUnitBase* ab) && ab != null)
                    Consider(n, ab);
            }

            if (best == null || bestId == 0 || string.IsNullOrEmpty(bestName))
                return false;

            lastHoverAddon = (bestName, bestId, now);

            if (Configuration.DebugMode && now - lastCursorHitTestLogMs >= 1000)
            {
                lastCursorHitTestLogMs = now;
                Log.Information($"[QuickTransfer] (MMB) Cursor hit-test picked addon '{bestName}' (addonId={bestId}) at ({x},{y}).");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveTargetFromWeirdPayload(
        ReadOnlySpan<InventoryType> containers,
        int rawInt1,
        int rawInt2,
        short refIdx,
        out InventoryType type,
        out int slot)
    {
        type = default;
        slot = -1;

        try
        {
            if (containers.Length == 0)
                return false;

            InventoryManager* inv = InventoryManager.Instance();
            if (inv == null)
                return false;

            // Try a few plausible slot candidates first (fast path).
            // Observed weird payloads often still include a real slot index in one of these fields.
            List<int> candidates = new(capacity: 4) { rawInt2, rawInt1, refIdx };
            foreach(int s in candidates.Distinct())
            {
                if (s < 0 || s > 500)
                    continue;

                foreach(InventoryType t in containers)
                {
                    InventoryItem* it = inv->GetInventorySlot(t, s);
                    if (it != null && it->ItemId != 0)
                    {
                        type = t;
                        slot = s;
                        return true;
                    }
                }
            }

            // Last resort: pick the first container that has any items,
            // and then pick its first non-empty slot.
            foreach(InventoryType t in containers)
            {
                InventoryContainer* c = inv->GetInventoryContainer(t);
                if (c == null || !c->IsLoaded || c->Size <= 0)
                    continue;

                for(int i = 0; i < c->Size; i++)
                {
                    InventoryItem* it = c->GetInventorySlot(i);
                    if (it != null && it->ItemId != 0)
                    {
                        type = t;
                        slot = i;
                        return true;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private bool TryQueueMiddleClickSortFromVisibleWindows(long now)
    {
        try
        {
            // If multiple inventory windows are open, we can't know which one the cursor is over without a hover DDI.
            // In that case, refuse and require hover capture.
            int visibleCount = 0;

            InventoryType chosenType = default;
            int chosenSlot = -1;
            uint chosenAddonId = 0;

            // ArmouryBoard
            if (InventoryHelpers.TryGetVisibleAddon("ArmouryBoard", out AtkUnitBase* ab, QuickTransferConstants.WideAddonSearchMaxIndex) && ab != null)
            {
                if (TryResolveTargetFromWeirdPayload(DragDropHelpers.ArmouryBoardIndexToType, -1, -1, -1, out InventoryType t, out int s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = ab->Id;
                }
            }

            // Saddlebags
            if (InventoryHelpers.TryGetVisibleAddon("InventoryBuddy", out AtkUnitBase* sb, QuickTransferConstants.WideAddonSearchMaxIndex) && sb != null)
            {
                if (TryResolveTargetFromWeirdPayload(QuickTransferConstants.SaddlebagInventoryTypes, -1, -1, -1, out InventoryType t, out int s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = sb->Id;
                }
            }
            else if (InventoryHelpers.TryGetVisibleAddon("InventoryBuddy2", out AtkUnitBase* sb2, QuickTransferConstants.WideAddonSearchMaxIndex) && sb2 != null)
            {
                if (TryResolveTargetFromWeirdPayload(QuickTransferConstants.SaddlebagInventoryTypes, -1, -1, -1, out InventoryType t, out int s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = sb2->Id;
                }
            }

            // Player inventory
            if (InventoryHelpers.TryGetVisibleAddon("Inventory", out AtkUnitBase* inv, QuickTransferConstants.WideAddonSearchMaxIndex) && inv != null)
            {
                if (TryResolveTargetFromWeirdPayload(QuickTransferConstants.PlayerInventoryTypes, -1, -1, -1, out InventoryType t, out int s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = inv->Id;
                }
            }

            // Retainer inventory
            if (InventoryHelpers.TryGetVisibleAddon("RetainerGrid0", out AtkUnitBase* rg0, QuickTransferConstants.WideAddonSearchMaxIndex) && rg0 != null)
            {
                if (TryResolveTargetFromWeirdPayload(QuickTransferConstants.RetainerInventoryTypes, -1, -1, -1, out InventoryType t, out int s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = rg0->Id;
                }
            }
            else if (InventoryHelpers.TryGetVisibleAddon("RetainerGrid", out AtkUnitBase* rg, QuickTransferConstants.WideAddonSearchMaxIndex) && rg != null)
            {
                if (TryResolveTargetFromWeirdPayload(QuickTransferConstants.RetainerInventoryTypes, -1, -1, -1, out InventoryType t, out int s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = rg->Id;
                }
            }
            else if (InventoryHelpers.TryGetVisibleAddon("RetainerSellList", out AtkUnitBase* rsl, QuickTransferConstants.WideAddonSearchMaxIndex) && rsl != null)
            {
                if (TryResolveTargetFromWeirdPayload(QuickTransferConstants.RetainerInventoryTypes, -1, -1, -1, out InventoryType t, out int s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = rsl->Id;
                }
            }

            // Free Company Chest (no native Sort context menu; MMB triggers our organize pass)
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out AtkUnitBase* fcc, QuickTransferConstants.WideAddonSearchMaxIndex) && fcc != null)
            {
                (InventoryType Page, uint AddonId, long SeenAtMs)? lp = lastHoverCompanyChestPage;
                if (lp != null && lp.Value.AddonId == fcc->Id && now - lp.Value.SeenAtMs <= 20000 && InventoryHelpers.IsCompanyChestType(lp.Value.Page))
                {
                    visibleCount++;
                    chosenType = lp.Value.Page;
                    chosenSlot = 0;
                    chosenAddonId = fcc->Id;
                }
                else
                {
                    (InventoryType Page, uint AddonId, long SeenAtMs)? sp = lastSelectedCompanyChestPage;
                    if (sp != null && sp.Value.AddonId == fcc->Id && now - sp.Value.SeenAtMs <= 20000 && InventoryHelpers.IsCompanyChestType(sp.Value.Page))
                    {
                        visibleCount++;
                        chosenType = sp.Value.Page;
                        chosenSlot = 0;
                        chosenAddonId = fcc->Id;
                    }
                    else
                    {
                        InventoryType[] pages = GetCompanyChestInventoryTypes();
                        if (pages.Length > 0)
                        {
                            visibleCount++;
                            chosenType = pages[0];
                            chosenSlot = 0;
                            chosenAddonId = fcc->Id;
                        }
                    }
                }
            }

            if (visibleCount != 1 || chosenAddonId == 0 || chosenSlot < 0)
                return false;

            int openSlot = DragDropHelpers.PickContextMenuSlot(chosenType, chosenSlot);
            pendingMiddleClickSortRequest = (chosenType, openSlot, chosenAddonId, now);
            pendingMiddleClickSortUntilMs = now + 1500;
            lastMiddleClickSortMs = now;

            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] (MMB) No hover DDI; bootstrapped from visible window: {chosenType} slot={openSlot} addonId={chosenAddonId}");

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryQueueMiddleClickSortFromLastHoverAddon(long now)
    {
        try
        {
            (string AddonName, uint AddonId, long SeenAtMs)? h = lastHoverAddon;
            if (h == null || now - h.Value.SeenAtMs > 20000)
                return false;

            string addonName = h.Value.AddonName;
            uint addonId = h.Value.AddonId;

            ReadOnlySpan<InventoryType> containers = default;
            if (addonName.Equals("Inventory", StringComparison.OrdinalIgnoreCase))
                containers = QuickTransferConstants.PlayerInventoryTypes;
            else if (addonName.Equals("InventoryBuddy", StringComparison.OrdinalIgnoreCase) || addonName.Equals("InventoryBuddy2", StringComparison.OrdinalIgnoreCase))
                containers = QuickTransferConstants.SaddlebagInventoryTypes;
            else if (addonName.Equals("RetainerGrid0", StringComparison.OrdinalIgnoreCase) ||
                     addonName.Equals("RetainerGrid", StringComparison.OrdinalIgnoreCase) ||
                     addonName.Equals("RetainerSellList", StringComparison.OrdinalIgnoreCase))
                containers = QuickTransferConstants.RetainerInventoryTypes;
            else if (addonName.Equals(QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase))
            {
                const long companyChestTabMaxAgeMs = 180000; // 3 minutes

                // FC Chest has no native "Sort"; MMB triggers our organize pass.
                // Run only on the currently selected tab, approximated as the most recently hovered/clicked FreeCompanyPage payload.

                // First preference: read the currently displayed page directly from the addon via a payload probe.
                // This avoids relying on tab ButtonClick params, which vary across clients/builds.
                if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out AtkUnitBase* fcc, QuickTransferConstants.WideAddonSearchMaxIndex) &&
                    fcc != null &&
                    fcc->Id == addonId &&
                    TryResolveCompanyChestPageFromAddon(fcc, out InventoryType curPage) &&
                    InventoryHelpers.IsCompanyChestType(curPage))
                {
                    pendingMiddleClickSortRequest = (curPage, 0, addonId, now);
                    pendingMiddleClickSortUntilMs = now + 1500;
                    lastMiddleClickSortMs = now;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) Resolved active Company Chest tab from payload: {curPage} (addonId={addonId})");
                    return true;
                }
                if (Configuration.DebugMode && InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out AtkUnitBase* fccDbg, QuickTransferConstants.WideAddonSearchMaxIndex) && fccDbg != null && fccDbg->Id == addonId)
                {
                    // Diagnostic: we expected to be able to infer the active page from visible payloads, but couldn't.
                    // This helps identify whether the probe is failing entirely or just returning a non-page payload.
                    Log.Information("[QuickTransfer] (MMB) Company Chest payload tab probe failed; falling back to hover/selected tab.");
                }

                (InventoryType Page, uint AddonId, long SeenAtMs)? lp = lastHoverCompanyChestPage;
                if (lp != null && lp.Value.AddonId == addonId && now - lp.Value.SeenAtMs <= companyChestTabMaxAgeMs && InventoryHelpers.IsCompanyChestType(lp.Value.Page))
                {
                    pendingMiddleClickSortRequest = (lp.Value.Page, 0, addonId, now);
                    pendingMiddleClickSortUntilMs = now + 1500;
                    lastMiddleClickSortMs = now;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) Using last-hovered Company Chest tab: {lp.Value.Page} slot=0 addonId={addonId}");
                    return true;
                }

                (InventoryType Page, uint AddonId, long SeenAtMs)? sp = lastSelectedCompanyChestPage;
                if (sp != null && sp.Value.AddonId == addonId && now - sp.Value.SeenAtMs <= companyChestTabMaxAgeMs && InventoryHelpers.IsCompanyChestType(sp.Value.Page))
                {
                    pendingMiddleClickSortRequest = (sp.Value.Page, 0, addonId, now);
                    pendingMiddleClickSortUntilMs = now + 1500;
                    lastMiddleClickSortMs = now;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) Using selected Company Chest tab: {sp.Value.Page} slot=0 addonId={addonId}");
                    return true;
                }

                if (TryResolveCompanyChestSelectedPageFromAtkValues(addonId, out InventoryType atkPage))
                {
                    pendingMiddleClickSortRequest = (atkPage, 0, addonId, now);
                    pendingMiddleClickSortUntilMs = now + 1500;
                    lastMiddleClickSortMs = now;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) Using Company Chest tab from AtkValues: {atkPage} slot=0 addonId={addonId}");
                    return true;
                }

                // If we couldn't infer the selected tab, do NOT guess (guessing Page1 is what causes "I clicked tab 2 but it sorted tab 1").
                if (Configuration.DebugMode)
                    Log.Information("[QuickTransfer] (MMB) Company Chest tab unknown; no action taken (waiting for a tab click or hover).");
                return false;
            }
            else if (QuickTransferConstants.ArmouryAddonNames.Any(n => addonName.Equals(n, StringComparison.OrdinalIgnoreCase)))
                containers = DragDropHelpers.ArmouryBoardIndexToType;

            if (containers.Length == 0 || addonId == 0)
                return false;

            // Prefer last known-good context target when available (more likely to produce a menu).
            if (lastGoodContextTargetByAddonId.TryGetValue(addonId, out (InventoryType Type, int Slot, int A4) good) &&
                (InventoryHelpers.IsPlayerInventoryType(good.Type) || InventoryHelpers.IsArmouryType(good.Type) || InventoryHelpers.IsSaddlebagType(good.Type) || InventoryHelpers.IsRetainerType(good.Type) || InventoryHelpers.IsCompanyChestType(good.Type)))
            {
                int openSlot = DragDropHelpers.PickContextMenuSlot(good.Type, good.Slot);
                pendingMiddleClickSortRequest = (good.Type, openSlot, addonId, now);
                pendingMiddleClickSortUntilMs = now + 1500;
                lastMiddleClickSortMs = now;
                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] (MMB) Using last-good target for hovered addon '{addonName}': {good.Type} slot={openSlot} addonId={addonId}");
                return true;
            }

            if (!TryResolveTargetFromWeirdPayload(containers, -1, -1, -1, out InventoryType type, out int slot))
                return false;

            int openSlot2 = DragDropHelpers.PickContextMenuSlot(type, slot);
            pendingMiddleClickSortRequest = (type, openSlot2, addonId, now);
            pendingMiddleClickSortUntilMs = now + 1500;
            lastMiddleClickSortMs = now;
            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] (MMB) Bootstrapped from hovered addon '{addonName}': {type} slot={openSlot2} addonId={addonId}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TryQueueMiddleClickSortFromHover(long now)
    {
        if (!Configuration.Enabled || !Configuration.EnableMiddleClickSort)
            return;

        if (now - lastMiddleClickSortMs < 250)
            return;

        (nint DdiPtr, uint AddonId, long SeenAtMs)? hDdi = lastHoverDdi;
        // Rollover events only fire when moving the cursor; keep a generous window so MMB works while stationary.
        if (hDdi == null || now - hDdi.Value.SeenAtMs > 20000)
        {
            // Inventory sometimes does not emit hover events; fall back to a window hit-test at the cursor.
            // This also lets us disambiguate which window is being targeted when multiple are open.
            if (TryUpdateLastHoverAddonFromCursorHitTest(now) && TryQueueMiddleClickSortFromLastHoverAddon(now))
                return;

            if (TryQueueMiddleClickSortFromLastHoverAddon(now))
                return;
            if (TryQueueMiddleClickSortFromVisibleWindows(now))
                return;
            if (Configuration.DebugMode)
                Log.Information("[QuickTransfer] (MMB) No recent hover slot/dragdrop captured; cannot queue sort.");
            return;
        }

        try
        {
            uint ddiAddonId = hDdi.Value.AddonId;

            // Key rule for stability across windows:
            // - A stored hover DDI can be stale if the UI doesn't emit MouseOut/RollOut events (common for Inventory/Saddlebags).
            // - Therefore, if the DDI wasn't updated very recently, prefer a live hit-test (collision manager) to determine
            //   which window is actually under the cursor right now.
            //
            // Armoury remains stable because the collision manager typically also reports it correctly, and we no longer
            // let stale "lastHoverAddon" from other windows override a fresh cursor hit-test.
            bool ddiFresh = now - hDdi.Value.SeenAtMs <= 250;
            if (!ddiFresh)
            {
                if (TryUpdateLastHoverAddonFromCursorHitTest(now) && TryQueueMiddleClickSortFromLastHoverAddon(now))
                    return;
            }

            // Otherwise, use the DDI's addon id and cached addon name as the target.
            if (!string.IsNullOrWhiteSpace(lastHoverAddonName))
            {
                lastHoverAddon = (lastHoverAddonName, ddiAddonId, now);
                if (TryQueueMiddleClickSortFromLastHoverAddon(now))
                    return;
            }

            // As a fallback, still allow using the last-good target for this addon id.
            if (lastGoodContextTargetByAddonId.TryGetValue(ddiAddonId, out (InventoryType Type, int Slot, int A4) good2))
            {
                int openSlot = DragDropHelpers.PickContextMenuSlot(good2.Type, good2.Slot);
                pendingMiddleClickSortRequest = (good2.Type, openSlot, ddiAddonId, now);
                pendingMiddleClickSortUntilMs = now + 1500;
                lastMiddleClickSortMs = now;
                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] (MMB) Used last-good target by addonId (no hover metadata): {good2.Type} slot={openSlot} addonId={ddiAddonId}");
                return;
            }

            // If we can't decide safely, do nothing.
            pendingMiddleClickSortUntilMs = now + 1500;
            lastMiddleClickSortMs = now;
        }
        catch(Exception ex)
        {
            // Best-effort only; avoid crashing the client if the hovered pointer becomes invalid.
            Log.Warning(ex, "[QuickTransfer] (MMB) Failed to queue sort from hover dragdrop.");
        }
    }

    private static void TryGetSlotSnapshot(InventoryManager* inv,
        InventoryType type,
        uint slot,
        out uint itemId,
        out int qty)
    {
        itemId = 0;
        qty = 0;
        try
        {
            if (inv == null)
                return;
            InventoryItem* it = inv->GetInventorySlot(type, (int)slot);
            if (it == null)
                return;
            itemId = it->ItemId;
            qty = it->Quantity;
        }
        catch
        {
            // ignored
        }
    }

    private void ArmSuppressContextMenu(long now, int durationMs = 250)
        => suppressContextMenuUntilMs = Math.Max(suppressContextMenuUntilMs, now + durationMs);

    private void ArmSuppressInputNumeric(long now, int durationMs = 1500)
        => suppressInputNumericUntilMs = Math.Max(suppressInputNumericUntilMs, now + durationMs);

    private InventoryType[] GetCompanyChestInventoryTypes()
    {
        // Don't hardcode enum names; discover them by name at runtime so we don't break across patches/structs.
        // Limit to the configured number of item compartments (default 3; can be upgraded to 5).
        int max = Math.Clamp(Configuration.CompanyChestCompartments, 3, 5);
        return Enum.GetValues<InventoryType>()
            .Where(InventoryHelpers.IsCompanyChestType)
            .OrderBy(v => (int)v)
            .Take(max)
            .ToArray();
    }

    private static InventoryType[] GetAllCompanyChestItemPages()
        => Enum.GetValues<InventoryType>()
            .Where(InventoryHelpers.IsCompanyChestType)
            .OrderBy(v => (int)v)
            .Take(5)
            .ToArray();

    private static AtkDragDropInterface* TryGetDdiFromListIndex(AtkComponentList* list, int idx)
    {
        if (list == null)
            return null;
        if (idx < 0 || idx > 512)
            return null;
        try
        {
            AtkComponentListItemRenderer* r = list->GetItemRenderer(idx);
            return r != null ? &r->AtkDragDropInterface : null;
        }
        catch
        {
            return null;
        }
    }

    private static AtkDragDropInterface* TryGetDdiFromComponent(AtkComponentBase* component, int preferredListIndex = 0)
    {
        if (component == null)
            return null;

        try
        {
            ComponentType t = component->GetComponentType();
            return t switch
            {
                ComponentType.DragDrop => &((AtkComponentDragDrop*)component)->AtkDragDropInterface,
                ComponentType.ListItemRenderer => &((AtkComponentListItemRenderer*)component)->AtkDragDropInterface,
                ComponentType.List => TryGetDdiFromListIndex((AtkComponentList*)component, preferredListIndex),
                var _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private bool TryResolveCompanyChestPageFromAddon(AtkUnitBase* addon, out InventoryType page)
    {
        page = default;
        try
        {
            if (addon == null)
                return false;

            // Scan component nodes for any DragDrop/List that yields a FreeCompanyPageX payload.
            ushort nodeCount = addon->UldManager.NodeListCount;
            if (nodeCount <= 0)
                return false;

            int maxNodes = Math.Min((int)nodeCount, 2000);
            InventoryType bestPage = default;
            int bestHits = 0;

            // Track the most frequently observed FreeCompanyPageX among *visible* nodes.
            // Rationale: the FC chest addon often keeps nodes for other tabs alive but hidden; a "first match wins"
            // scan can return the wrong tab (observed off-by-one behavior).
            Dictionary<InventoryType, int> hitsByPage = new(8);
            for(int i = 0; i < maxNodes; i++)
            {
                AtkResNode* n = addon->UldManager.NodeList[i];
                if (n == null)
                    continue;

                // Skip hidden nodes (inactive tabs commonly force alpha to 0).
                try
                {
                    if (n->Alpha_2 == 0 || n->Color.A == 0)
                        continue;
                }
                catch
                {
                    // ignore; continue scanning
                }

                AtkComponentNode* compNode;
                try { compNode = n->GetAsAtkComponentNode(); }
                catch { continue; }
                if (compNode == null || compNode->Component == null)
                    continue;

                AtkComponentBase* component = compNode->Component;
                ComponentType ct = component->GetComponentType();
                if (ct == ComponentType.List)
                {
                    AtkComponentList* list = (AtkComponentList*)component;
                    // Try a few indices; FC chest lists usually expose items here.
                    int observed = 0;
                    for(int li = 0; li < 30; li++)
                    {
                        AtkDragDropInterface* ddi = TryGetDdiFromListIndex(list, li);
                        if (ddi == null || (nint)ddi < QuickTransferConstants.MinLikelyPointer)
                            continue;

                        if (DragDropHelpers.TryGetSlotFromDragDropInterface(ddi, out InventoryType invType, out int _))
                        {
                            if (InventoryHelpers.IsCompanyChestType(invType))
                            {
                                hitsByPage.TryGetValue(invType, out int cur);
                                cur++;
                                hitsByPage[invType] = cur;
                                if (cur > bestHits)
                                {
                                    bestHits = cur;
                                    bestPage = invType;
                                }

                                // Don't over-scan; we just need enough evidence to pick the visible page.
                                observed++;
                                if (observed >= 6)
                                    break;
                            }
                        }
                    }
                }
                else
                {
                    AtkDragDropInterface* ddi = TryGetDdiFromComponent(component);
                    if (ddi == null || (nint)ddi < QuickTransferConstants.MinLikelyPointer)
                        continue;

                    if (DragDropHelpers.TryGetSlotFromDragDropInterface(ddi, out InventoryType invType, out int _))
                    {
                        if (InventoryHelpers.IsCompanyChestType(invType))
                        {
                            hitsByPage.TryGetValue(invType, out int cur);
                            cur++;
                            hitsByPage[invType] = cur;
                            if (cur > bestHits)
                            {
                                bestHits = cur;
                                bestPage = invType;
                            }
                        }
                    }
                }
            }

            if (bestHits > 0 && InventoryHelpers.IsCompanyChestType(bestPage))
            {
                page = bestPage;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private void ObserveCompanyChestTabFromAtkValues(AtkUnitBase* addon, InventoryType selectedPage)
    {
        try
        {
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount <= 0)
                return;

            AtkValue* values = addon->AtkValues;
            int count = addon->AtkValuesCount;
            int max = Math.Min(count, 80);

            for(int i = 0; i < max; i++)
            {
                if (!AtkValueHelpers.TryGetAtkValueInt(values, max, i, out int n))
                    continue;

                // Only small integers are plausible "tab indices".
                if (n < 0 || n > 10)
                    continue;

                if (!companyChestSelectedTabCandidates.TryGetValue(i, out Dictionary<int, InventoryType>? map))
                {
                    map = new(8);
                    companyChestSelectedTabCandidates[i] = map;
                }

                // If we see conflicting mappings for the same (index,value), drop this candidate index.
                if (map.TryGetValue(n, out InventoryType existing) && existing != selectedPage)
                {
                    companyChestSelectedTabCandidates.Remove(i);
                    continue;
                }

                map[n] = selectedPage;
            }

            // Pick the best candidate index (most distinct pages mapped).
            int bestIdx = -1;
            int bestDistinct = 0;
            foreach(KeyValuePair<int, Dictionary<int, InventoryType>> kv in companyChestSelectedTabCandidates)
            {
                int distinct = kv.Value.Values.Distinct().Count();
                if (distinct > bestDistinct)
                {
                    bestDistinct = distinct;
                    bestIdx = kv.Key;
                }
            }

            if (bestIdx >= 0 && bestDistinct >= 2)
            {
                if (companyChestSelectedTabAtkValueIndex != bestIdx)
                {
                    companyChestSelectedTabAtkValueIndex = bestIdx;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] FC Chest AtkValues selected-tab index inferred: idx={bestIdx} (mappedPages={bestDistinct}).");
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private bool TryResolveCompanyChestSelectedPageFromAtkValues(uint addonId, out InventoryType page)
    {
        page = default;
        try
        {
            if (companyChestSelectedTabAtkValueIndex < 0)
                return false;

            if (!InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out AtkUnitBase* addon, QuickTransferConstants.WideAddonSearchMaxIndex) || addon == null || addon->Id != addonId)
                return false;

            if (addon->AtkValues == null || addon->AtkValuesCount <= 0)
                return false;

            if (!companyChestSelectedTabCandidates.TryGetValue(companyChestSelectedTabAtkValueIndex, out Dictionary<int, InventoryType>? map) || map.Count == 0)
                return false;

            if (!AtkValueHelpers.TryGetAtkValueInt(addon->AtkValues, addon->AtkValuesCount, companyChestSelectedTabAtkValueIndex, out int n))
                return false;

            if (!map.TryGetValue(n, out InventoryType p))
                return false;

            if (!InventoryHelpers.IsCompanyChestType(p))
                return false;

            page = p;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryResolveCompanyChestActivePage(long now, out InventoryType page)
    {
        page = default;
        try
        {
            if (!InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out AtkUnitBase* fcc, QuickTransferConstants.WideAddonSearchMaxIndex) || fcc == null)
                return false;

            uint addonId = fcc->Id;
            const long companyChestTabMaxAgeMs = 180000; // 3 minutes

            // Prefer the page currently displayed in the addon (visible drag-drop payloads).
            if (TryResolveCompanyChestPageFromAddon(fcc, out InventoryType curPage) && InventoryHelpers.IsCompanyChestType(curPage))
            {
                page = curPage;
                return true;
            }

            (InventoryType Page, uint AddonId, long SeenAtMs)? lp = lastHoverCompanyChestPage;
            if (lp != null && lp.Value.AddonId == addonId && now - lp.Value.SeenAtMs <= companyChestTabMaxAgeMs && InventoryHelpers.IsCompanyChestType(lp.Value.Page))
            {
                page = lp.Value.Page;
                return true;
            }

            (InventoryType Page, uint AddonId, long SeenAtMs)? sp = lastSelectedCompanyChestPage;
            if (sp != null && sp.Value.AddonId == addonId && now - sp.Value.SeenAtMs <= companyChestTabMaxAgeMs && InventoryHelpers.IsCompanyChestType(sp.Value.Page))
            {
                page = sp.Value.Page;
                return true;
            }

            if (TryResolveCompanyChestSelectedPageFromAtkValues(addonId, out InventoryType atkPage) && InventoryHelpers.IsCompanyChestType(atkPage))
            {
                page = atkPage;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private void OnCommand(string command, string args) => OpenConfigUi();

    private void OpenConfigUi() => configWindow.IsOpen = true;

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            if (!Configuration.EnableCompanyChest)
                return;

            // Only care while FC chest features are active; avoid doing extra work on every chat line.
            if (!companyChestOrganize.Active && !companyChestDeposit.Active)
                return;

            string text = message.Sender.TextValue;
            if (text.Length == 0)
                return;

            // These strings appear as system error toasts and (typically) also in the log/chat.
            // If we see them, stop the state machine and back off for a few seconds.
            if (text.Contains("Another player is using the chest", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Unable to store item", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Unable to complete company chest action", StringComparison.OrdinalIgnoreCase))
            {
                long now = Environment.TickCount64;
                companyChestBusyHits = Math.Min(companyChestBusyHits + 1, 10);
                long backoffMs = Math.Min(60000, 5000 * (1 << Math.Min(companyChestBusyHits - 1, 4))); // 5s,10s,20s,40s,60s cap
                companyChestBusyUntilMs = Math.Max(companyChestBusyUntilMs, now + backoffMs);

                // If the chest is busy repeatedly, stop the run and let the user try later.
                if (companyChestOrganize.Active && companyChestBusyHits >= 3)
                {
                    companyChestOrganize.Active = false;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) FC Chest busy hit {companyChestBusyHits}; stopping organize run. msg='{text}'");
                }
                else if (companyChestOrganize.Active)
                {
                    // Pause and retry later.
                    companyChestOrganize.WaitingForApply = false;
                    companyChestOrganize.WaitObservedChangeAtMs = 0;
                    companyChestOrganize.NextAttemptAtMs = Math.Max(companyChestOrganize.NextAttemptAtMs, companyChestBusyUntilMs + 750);
                    companyChestOrganize.ExpiresAtMs = Math.Max(companyChestOrganize.ExpiresAtMs, companyChestBusyUntilMs + 20000);
                    companyChestOrganize.WaitStuckCount = 0;
                }

                // Deposit is interactive; stop it outright on busy.
                companyChestDeposit.Active = false;
                pendingCompanyChestNumericConfirmUntilMs = 0;
                pendingCompanyChestNumericArmed = false;
                pendingNumericKind = PendingNumericKind.None;
                pendingCompanyChestNumericDesired = 0;
                pendingCompanyChestNumericHalf = false;
                pendingCompanyChestNumericValueSet = false;
                pendingCompanyChestNumericValueSetAtMs = 0;

                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] (MMB) FC Chest busy detected from chat; backoff={backoffMs}ms (hit {companyChestBusyHits}). msg='{text}'");
            }
        }
        catch
        {
            // ignore
        }
    }

    private void OpenForItemSlotDetour(
        AgentInventoryContext* agent,
        InventoryType inventoryType,
        int slot,
        int a4,
        uint addonId)
    {
        openForItemSlotHook?.Original(agent, inventoryType, slot, a4, addonId);

        if (!Configuration.Enabled)
            return;

        // Record observed a4 values so we can reuse them for MMB-driven opens.
        try
        {
            observedContextA4[(addonId, (uint)inventoryType)] = a4;

            // If this call actually produced a context menu, remember it as a safe fallback for MMB sorting.
            if (agent != null && agent->ContextItemCount > 0)
                lastGoodContextTargetByAddonId[addonId] = (inventoryType, slot, a4);

            if (Configuration.DebugMode && Environment.TickCount64 - lastObservedA4LogMs >= 1000)
            {
                lastObservedA4LogMs = Environment.TickCount64;
                Log.Information($"[QuickTransfer] Observed OpenForItemSlot: type={inventoryType} slot={slot} a4={a4} addonId={addonId} ctxCount={(agent != null ? agent->ContextItemCount : -1)}");
            }
        }
        catch
        {
            // ignore
        }

        // Modifier: Ctrl+RClick (special) or Shift+RClick (default).
        // Ctrl takes priority if both are held. Use a short "latch" so quick taps still work.
        ModifierMode? mode = GetModifierModeLatched(Environment.TickCount64);

        if (mode == null)
            return;

        bool saddlebagOpen = InventoryHelpers.IsSaddlebagOpen();
        bool retainerOpen = InventoryHelpers.IsRetainerOpen();
        bool companyChestOpen = InventoryHelpers.IsCompanyChestOpen();
        bool specialOpen = saddlebagOpen || retainerOpen || companyChestOpen;

        // Ctrl is only enabled while a "special" container is open (Saddlebag or Retainer),
        // so Shift/Ctrl can be used to disambiguate behaviors.
        if (mode == ModifierMode.Ctrl && !specialOpen)
            return;

        // Never run Ctrl-mode from saddlebag slots.
        if (mode == ModifierMode.Ctrl && InventoryHelpers.IsSaddlebagType(inventoryType))
            return;

        // Never run Ctrl-mode from retainer slots.
        if (mode == ModifierMode.Ctrl && InventoryHelpers.IsRetainerType(inventoryType))
            return;

        // Never run Ctrl-mode from Company Chest slots.
        if (mode == ModifierMode.Ctrl && InventoryHelpers.IsCompanyChestType(inventoryType))
            return;

        long now = Environment.TickCount64;
        if (now - lastActionTickMs < Configuration.TransferCooldownMs)
            return;

        // For Alt (Split), prefer the deferred OnMenuOpened path (more reliable than firing callbacks during OpenForItemSlot).
        if (mode == ModifierMode.Alt)
            return;

        if (mode == ModifierMode.Shift && companyChestOpen && Configuration.EnableCompanyChest)
        {
            // If a quantity dialog is already open, don't start another move.
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out AtkUnitBase* _))
                return;

            // Deposit: Inventory/Armoury -> Company Chest (UI-driven move).
            // This is handled as a small state machine so stacks can top-off existing stacks and spill into new stacks.
            if (InventoryHelpers.IsCompanyChestDepositSourceType(inventoryType) && StartCompanyChestDeposit(inventoryType, (uint)slot))
            {
                lastActionTickMs = now;
                ContextMenuHandler.TryCloseCurrentContextMenu(agent);
                return;
            }
        }

        if (ContextMenuHandler.TryAutoSelectFromAgent(agent, mode.Value, Configuration, out string chosenText, out int chosenIndex, ref pendingCloseContextMenuAtMs))
        {
            lastActionTickMs = now;
            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] ({mode} + RClick) Selected context action '{chosenText}' (idx={chosenIndex}) via OpenForItemSlot.");

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
            Log.Information("[QuickTransfer] (Ctrl + RClick) No matching armoury action found in context menu.");
            DebugDumpContextMenu(agent, 24);
        }
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (!Configuration.Enabled)
            return;

        long now = Environment.TickCount64;
        bool middleSortActive = pendingMiddleClickSortUntilMs > 0 && now <= pendingMiddleClickSortUntilMs;
        ModifierMode? mode = middleSortActive ? null : GetModifierModeLatched(now);

        if (!middleSortActive && mode == null)
            return;

        bool saddlebagOpen = InventoryHelpers.IsSaddlebagOpen();
        bool retainerOpen = InventoryHelpers.IsRetainerOpen();
        bool companyChestOpen = InventoryHelpers.IsCompanyChestOpen();
        bool specialOpen = saddlebagOpen || retainerOpen || companyChestOpen;

        if (!middleSortActive && mode == ModifierMode.Ctrl && !specialOpen)
            return;

        if (Configuration.DebugMode)
            Log.Information($"[QuickTransfer] OnMenuOpened: AddonName='{args.AddonName}', MenuType={args.MenuType}, AgentPtr=0x{args.AgentPtr.ToInt64():X}, AddonPtr=0x{args.AddonPtr.ToInt64():X}");

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
            return;

        if (args.AgentPtr == nint.Zero || args.AddonPtr == nint.Zero)
            return;

        if (mode == null)
            return;

        // IMPORTANT: Do not click inside the open event (re-entrancy risk).
        pendingDeferredMenuClick = (args.AgentPtr, args.AddonPtr, Environment.TickCount64, mode.Value);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!Configuration.Enabled)
            return;

        long now = Environment.TickCount64;

        // Poll mouse button state from Win32 and log transitions in DebugMode.
        // This helps diagnose cases where the game doesn't emit click events for MMB.
        bool lDown = IsMouseButtonDown(0x01); // VK_LBUTTON
        bool rDown = IsMouseButtonDown(0x02); // VK_RBUTTON
        bool mDown = IsMouseButtonDown(0x04); // VK_MBUTTON
        bool x1Down = IsMouseButtonDown(0x05); // VK_XBUTTON1
        bool x2Down = IsMouseButtonDown(0x06); // VK_XBUTTON2

        bool prevL = lastVkLButtonDown;
        bool prevR = lastVkRButtonDown;
        bool prevM = lastVkMButtonDown;
        bool prevX1 = lastVkX1ButtonDown;
        bool prevX2 = lastVkX2ButtonDown;

        if (Configuration.DebugMode && (lDown != prevL || rDown != prevR || mDown != prevM || x1Down != prevX1 || x2Down != prevX2))
            Log.Information($"[QuickTransfer] Win32 mouse state: L={(lDown ? 1 : 0)} R={(rDown ? 1 : 0)} M={(mDown ? 1 : 0)} X1={(x1Down ? 1 : 0)} X2={(x2Down ? 1 : 0)}");

        lastVkLButtonDown = lDown;
        lastVkRButtonDown = rDown;
        lastVkMButtonDown = mDown;
        lastVkX1ButtonDown = x1Down;
        lastVkX2ButtonDown = x2Down;

        // If a "middle-ish" button is pressed (rising edge), queue a sort using the last hovered slot.
        // This works even if the client doesn't generate a distinct UI click event on this build.
        bool middleEdge = (mDown && !prevM) || (x1Down && !prevX1) || (x2Down && !prevX2);
        if (middleEdge)
        {
            if (Configuration.EnableMiddleClickSort)
                TryQueueMiddleClickSortFromHover(now);
            else if (Configuration.DebugMode)
                Log.Information("[QuickTransfer] (MMB) Press detected, but EnableMiddleClickSort is disabled.");
        }

        // Modifier latch (helps cases where the user taps Shift/Ctrl quickly).
        if (KeyState[VirtualKey.SHIFT])
            lastShiftSeenMs = now;
        if (KeyState[VirtualKey.CONTROL])
            lastCtrlSeenMs = now;
        if (KeyState[VirtualKey.MENU])
            lastAltSeenMs = now;

        // Quantity prompt auto-confirm (best effort).
        // Trade and Split always auto-confirm; Company Chest and Vendor Sell respect their config settings.
        bool shouldAutoConfirm = pendingNumericKind == PendingNumericKind.Trade ||
                                 pendingNumericKind == PendingNumericKind.Split ||
                                 (Configuration.AutoConfirmVendorSell && pendingNumericKind == PendingNumericKind.Sell) ||
                                 (Configuration.AutoConfirmCompanyChestQuantity && pendingNumericKind != PendingNumericKind.None);

        if (shouldAutoConfirm &&
            pendingNumericKind != PendingNumericKind.None &&
            pendingCompanyChestNumericConfirmUntilMs > 0 &&
            now <= pendingCompanyChestNumericConfirmUntilMs)
        {
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out AtkUnitBase* inputNumeric))
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
                                AtkValue* promptVal = inputNumeric->AtkValues + 6;
                                string prompt = promptVal->Type is AtkValueType.String or AtkValueType.ManagedString ? AtkValueHelpers.ReadAtkValueString(*promptVal) : string.Empty;
                                AtkValue* minVal = inputNumeric->AtkValues + 2;
                                AtkValue* maxVal = inputNumeric->AtkValues + 3;
                                uint min = minVal->Type == AtkValueType.UInt ? minVal->UInt : 0U;
                                uint max = maxVal->Type == AtkValueType.UInt ? maxVal->UInt : 0U;
                                Log.Information($"[QuickTransfer] Auto-confirm InputNumeric skipped (kind={pendingNumericKind}, prompt='{prompt}', min={min}, max={max}, expectedSplitMax={pendingSplitExpectedMax}).");
                            }
                            catch
                            {
                                Log.Information($"[QuickTransfer] Auto-confirm InputNumeric skipped (kind={pendingNumericKind}).");
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
                    return;

                // Re-check prompt + re-apply max right before confirming (cheap + safer).
                if (!TrySetInputNumericToMax(inputNumeric, pendingNumericKind))
                {
                    if (Configuration.DebugMode)
                    {
                        try
                        {
                            AtkValue* promptVal = inputNumeric->AtkValues + 6;
                            string prompt = promptVal->Type is AtkValueType.String or AtkValueType.ManagedString ? AtkValueHelpers.ReadAtkValueString(*promptVal) : string.Empty;
                            AtkValue* minVal = inputNumeric->AtkValues + 2;
                            AtkValue* maxVal = inputNumeric->AtkValues + 3;
                            uint min = minVal->Type == AtkValueType.UInt ? minVal->UInt : 0U;
                            uint max = maxVal->Type == AtkValueType.UInt ? maxVal->UInt : 0U;
                            Log.Information($"[QuickTransfer] Auto-confirm InputNumeric aborted (kind={pendingNumericKind}, prompt='{prompt}', min={min}, max={max}, expectedSplitMax={pendingSplitExpectedMax}).");
                        }
                        catch
                        {
                            Log.Information($"[QuickTransfer] Auto-confirm InputNumeric aborted (kind={pendingNumericKind}).");
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
                        uint toConfirm = pendingCompanyChestNumericDesired;
                        if (toConfirm == 0)
                        {
                            // Default: confirm max (we already set the numeric input to max above).
                            try
                            {
                                AtkValue* maxVal = inputNumeric->AtkValues + 3;
                                if (maxVal->Type == AtkValueType.UInt)
                                    toConfirm = maxVal->UInt;
                                else if (maxVal->Type == AtkValueType.Int)
                                    toConfirm = (uint)Math.Max(0, maxVal->Int);
                            }
                            catch
                            {
                                // ignore
                            }
                            if (toConfirm == 0)
                                toConfirm = 1;
                        }
                        inputNumeric->FireCallbackInt((int)toConfirm);
                        pendingCompanyChestNumericConfirmAttempts = 1;
                        if (Configuration.DebugMode)
                            Log.Information($"[QuickTransfer] Auto-confirmed InputNumeric attempt 1 (kind={pendingNumericKind}, FireCallbackInt={toConfirm}).");

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
                catch(Exception ex)
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
                    Log.Warning(ex, "[QuickTransfer] Failed to auto-confirm InputNumeric.");
                }
            }
            else if (Configuration.AutoConfirmVendorSell && pendingNumericKind == PendingNumericKind.Sell && InventoryHelpers.IsVendorOpen() &&
                     GenericHelpers.TryGetAddonMaster<AddonMaster.SelectYesno>(QuickTransferConstants.SelectYesnoAddonName, out AddonMaster.SelectYesno selectYesno))
            {
                try
                {
                    selectYesno.Yes();
                    if (Configuration.DebugMode)
                        Log.Information("[QuickTransfer] Auto-confirmed vendor sell Yes/No dialog (SelectYesno).");
                }
                catch(Exception ex)
                {
                    if (Configuration.DebugMode)
                        Log.Warning(ex, "[QuickTransfer] Failed to auto-confirm SelectYesno.");
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
            bool inputVisible = InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out AtkUnitBase* _);
            if (inputVisible)
                pendingMoveSawInputNumeric = true;

            // Important: InputNumeric often appears on a subsequent frame.
            // Do NOT free the buffers immediately just because it's not visible yet.
            bool graceExpired = pendingMoveCreatedAtMs > 0 && now - pendingMoveCreatedAtMs >= 1500;
            if ((pendingMoveSawInputNumeric && !inputVisible) || now >= pendingMoveOutValueFreeAtMs || (!inputVisible && graceExpired))
            {
                try
                {
                    if (pendingMoveOutValuePtr != 0) Marshal.FreeHGlobal(pendingMoveOutValuePtr);
                }
                catch
                {
                    /* ignore */
                }
                try
                {
                    if (pendingMoveAtkValuesPtr != 0) Marshal.FreeHGlobal(pendingMoveAtkValuesPtr);
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
            ProcessCompanyChestDeposit(now);

        // Company Chest organize (MMB): auto-stack + compact items within FC chest pages.
        if (Configuration is { EnableCompanyChest: true, EnableCompanyChestMiddleClickOrganize: true })
            ProcessCompanyChestOrganize(now);

        // Middle-click sort: open the context menu on the clicked slot, then auto-select "Sort".
        (InventoryType Type, int Slot, uint AddonId, long EnqueuedAtMs)? mmb = pendingMiddleClickSortRequest;
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
                        Log.Information($"[QuickTransfer] (MMB) Refusing to call OpenForItemSlot for unrecognized inventory type={mmb.Value.Type} slot={mmb.Value.Slot} addonId={mmb.Value.AddonId} (crash-prevention).");
                    pendingMiddleClickSortRequest = null;
                    pendingMiddleClickSortUntilMs = 0;
                    return;
                }

                // Open context menu for that slot. Our OnMenuOpened handler will enqueue the deferred sort selection.
                AgentModule* agentModule = AgentModule.Instance();
                if (agentModule != null)
                {
                    AgentInterface* agent = agentModule->GetAgentByInternalId(AgentId.InventoryContext);
                    AgentInventoryContext* invCtx = (AgentInventoryContext*)agent;
                    if (invCtx != null)
                    {
                        try
                        {
                            ArmSuppressContextMenu(now);
                            if (Configuration.DebugMode)
                                Log.Information($"[QuickTransfer] (MMB) Calling OpenForItemSlot: type={mmb.Value.Type} slot={mmb.Value.Slot} addonId={mmb.Value.AddonId}");

                            // Try to open the inventory context menu using the same mysterious "a4" value the game uses.
                            // If we don't have a recorded value yet, try a small set of common candidates.
                            int[] candidates;
                            if (!observedContextA4.TryGetValue((mmb.Value.AddonId, (uint)mmb.Value.Type), out int observedA4))
                            {
                                // Heuristic: armoury boards often need a non-zero a4; try 1 first.
                                candidates = InventoryHelpers.IsArmouryType(mmb.Value.Type) ? [1, 0, 2] : [0, 1, 2];
                            }
                            else
                            {
                                candidates = [observedA4, 0, 1, 2];
                            }

                            bool opened = false;
                            int usedA4 = 0;
                            foreach(int a4 in candidates.Distinct())
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
                                AtkUnitBase* cm = AddonHelpers.GetAddonByName("ContextMenu");
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
                                    Log.Information(
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
                AtkUnitBase* cm = AddonHelpers.GetAddonByName("ContextMenu");
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
        (string AddonName, long EnqueuedAtMs, ModifierMode Mode)? pendingDefault = pendingDeferredDefaultMenu;
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

        (nint AgentPtr, nint AddonPtr, long EnqueuedAtMs, ModifierMode Mode)? pending = pendingDeferredMenuClick;
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
            return;

        // Consume (only try once after the short delay).
        pendingDeferredMenuClick = null;

        // If we already acted this tick/window via OpenForItemSlot, don't deref pointers.
        if (now - lastActionTickMs < Configuration.TransferCooldownMs)
            return;

        try
        {
            AgentInventoryContext* agent = (AgentInventoryContext*)pending.Value.AgentPtr;
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
                addon = (AtkUnitBase*)pending.Value.AddonPtr;

            if (ContextMenuHandler.TryAutoSelectAndClose(
                    agent,
                    addon,
                    pending.Value.Mode,
                    Configuration,
                    out string chosenText,
                    out int chosenIndex,
                    ref pendingCloseContextMenuAtMs))
            {
                lastActionTickMs = now;
                // Split is finicky: keep the menu suppressed longer so it can't be cancelled by an early close/visibility change.
                int suppressMs = (pending.Value.Mode == ModifierMode.Alt && chosenText.Length > 0 && ContextMenuHandler.ContextLabelMatches(AutoContextAction.Split, chosenText))
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
                        InventoryType srcType = agent->TargetInventoryId;
                        int srcSlot = agent->TargetInventorySlotId;
                        if (InventoryHelpers.TryGetItemInfo(srcType, srcSlot, out uint _, out bool _, out uint qty) && qty > 1)
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
                    Log.Information($"[QuickTransfer] ({pending.Value.Mode} + RClick) Selected context action '{chosenText}' (idx={chosenIndex}) via deferred OnMenuOpened.");
            }
            else if (Configuration.DebugMode && pending.Value.Mode == ModifierMode.Ctrl)
            {
                Log.Information("[QuickTransfer] (Ctrl + RClick) Deferred menu opened but no matching 'Place in Armoury Chest' action was found.");
                DebugDumpContextMenu(agent, 24);
            }
        }
        catch(Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Deferred menu select failed.");
        }

        // Also process a pending sort click (if any) after normal transfers.
        ProcessDeferredSortMenuClick(now);
    }

    private void ProcessDeferredSortMenuClick(long now)
    {
        (nint AgentPtr, nint AddonPtr, long EnqueuedAtMs)? pendingSort = pendingDeferredSortMenuClick;
        if (pendingSort == null)
            return;

        // Give the context menu a moment to populate after OpenForItemSlot.
        if (now - pendingSort.Value.EnqueuedAtMs < 50)
            return;

        if (now - pendingSort.Value.EnqueuedAtMs > 1500)
        {
            if (Configuration.DebugMode)
            {
                try
                {
                    AgentInventoryContext* agent = (AgentInventoryContext*)pendingSort.Value.AgentPtr;
                    int count = agent != null ? agent->ContextItemCount : -1;
                    Log.Information($"[QuickTransfer] (MMB) Deferred sort timed out (ContextItemCount={count}).");
                }
                catch
                {
                    Log.Information("[QuickTransfer] (MMB) Deferred sort timed out.");
                }
            }
            pendingDeferredSortMenuClick = null;
            pendingMiddleClickSortUntilMs = 0;
            return;
        }

        try
        {
            AgentInventoryContext* agent = (AgentInventoryContext*)pendingSort.Value.AgentPtr;
            AtkUnitBase* addon = (AtkUnitBase*)pendingSort.Value.AddonPtr;

            // If we didn't have the ContextMenu addon pointer yet, try to resolve it now.
            if (addon == null)
            {
                try
                {
                    AtkUnitBase* cm = AddonHelpers.GetAddonByName("ContextMenu");
                    if (cm != null)
                    {
                        addon = cm;
                        pendingDeferredSortMenuClick = (pendingSort.Value.AgentPtr, (nint)addon, pendingSort.Value.EnqueuedAtMs);
                        pendingSort = pendingDeferredSortMenuClick;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            // If the menu hasn't populated yet, keep waiting.
            // AgentInventoryContext::ContextItemCount tends to remain 0 for a frame or two after OpenForItemSlot.
            if (agent == null || agent->ContextItemCount <= 0)
                return;

            if (addon == null)
                return;

            if (ContextMenuHandler.TrySelectSortAndClose(agent, addon, out string chosenText, out int chosenIndex))
            {
                pendingDeferredSortMenuClick = null;
                pendingMiddleClickSortUntilMs = 0;
                lastActionTickMs = now;
                ArmSuppressContextMenu(now, 500);
                if (Configuration.DebugMode)
                {
                    Log.Information(chosenIndex >= 0 ? $"[QuickTransfer] (MMB) Selected context action '{chosenText}' (idx={chosenIndex}) via deferred OnMenuOpened." : "[QuickTransfer] (MMB) Already sorted (Undo Sort present); no action taken.");
                }
            }
            else
            {
                // If we opened a menu but didn't find Sort, wait briefly in case the menu is still updating.
                // After ~300ms, give up and close it to avoid leaving a hidden menu behind.
                if (now - pendingSort.Value.EnqueuedAtMs < 300)
                    return;

                if (Configuration.DebugMode)
                {
                    Log.Information($"[QuickTransfer] (MMB) Context menu opened but no 'Sort' entry was found (count={agent->ContextItemCount}).");
                    DebugDumpContextMenu(agent, 32);
                }

                pendingDeferredSortMenuClick = null;
                pendingMiddleClickSortUntilMs = 0;
                try
                {
                    ContextMenuHandler.CloseContextMenuAddon(agent, addon);
                }
                catch
                {
                    /* ignore */
                }
            }
        }
        catch(Exception ex)
        {
            pendingDeferredSortMenuClick = null;
            pendingMiddleClickSortUntilMs = 0;
            Log.Warning(ex, "[QuickTransfer] Deferred sort select failed.");
        }
    }

    private void OnAddonPreDraw(AddonEvent type, AddonArgs args)
    {
        try
        {
            string name = args.AddonName;
            long now = Environment.TickCount64;

            if (string.Equals(name, QuickTransferConstants.ContextMenuAddonName, StringComparison.OrdinalIgnoreCase))
            {
                AtkUnitBase* addon = (AtkUnitBase*)args.Addon.Address;
                if (now <= suppressContextMenuUntilMs)
                    AtkValueHelpers.MakeAddonInvisible(addon);
                else
                    AtkValueHelpers.MakeAddonVisible(addon);
            }

            if (string.Equals(name, QuickTransferConstants.InputNumericAddonName, StringComparison.OrdinalIgnoreCase))
            {
                AtkUnitBase* addon = (AtkUnitBase*)args.Addon.Address;
                if (now <= suppressInputNumericUntilMs)
                    AtkValueHelpers.MakeAddonInvisible(addon);
                else
                    AtkValueHelpers.MakeAddonVisible(addon);
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

            long now = Environment.TickCount64;
            if (Configuration.DebugMode && !debugPrintedReceiveEventHook)
            {
                debugPrintedReceiveEventHook = true;
                try { ChatGui.Print("[QuickTransfer] ReceiveEvent hook active (MMB debug)."); }
                catch
                {
                    /* ignore */
                }
                Log.Information("[QuickTransfer] ReceiveEvent hook active (MMB debug).");
            }

            AtkEventType eventType = (AtkEventType)recv.AtkEventType;
            AtkEventData* eventData = (AtkEventData*)recv.AtkEventData;
            byte mouseButtonId = eventData != null ? eventData->MouseData.ButtonId : (byte)255;
            byte dragDropMouseButtonId = eventData != null ? eventData->DragDropData.MouseButtonId : (byte)255;

            // Track last-hovered dragdrop (for polling-based triggers).
            // IMPORTANT:
            // - For ArmouryBoard, only capture from drag-drop rollover/click (avoids bad union reads on some builds).
            // - For Inventory/Saddlebags, we also allow MouseOver by resolving the DDI from atkEvent->Node (safe path).
            string addonName = args.AddonName;
            bool allowMouseOverCapture =
                addonName.Equals("Inventory", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("InventoryBuddy", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("InventoryBuddy2", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("RetainerGrid0", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("RetainerGrid", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("RetainerSellList", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals(QuickTransferConstants.FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase);

            // Always track which addon the cursor is currently interacting with, even if we can't resolve a DDI.
            // This enables a safe MMB "Sort" path that doesn't dereference drag/drop pointers.
            if (eventType is AtkEventType.MouseOver or AtkEventType.MouseOut or AtkEventType.DragDropRollOver or AtkEventType.DragDropRollOut ||
                eventType is AtkEventType.ListItemRollOver or AtkEventType.ListItemRollOut)
            {
                try
                {
                    AtkUnitBase* ab = (AtkUnitBase*)args.Addon.Address;
                    uint id = ab != null ? ab->Id : 0u;
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
                    AtkUnitBase* ab = (AtkUnitBase*)args.Addon.Address;
                    uint id = ab != null ? ab->Id : 0u;
                    if (id != 0 && TryMapCompanyChestTabParamToPage(recv.EventParam, out InventoryType selectedPage))
                    {
                        lastSelectedCompanyChestPage = (selectedPage, id, now);
                        ObserveCompanyChestTabFromAtkValues(ab, selectedPage);
                        if (Configuration.DebugMode && now - lastReceiveEventDebugLogMs >= 250)
                            Log.Information($"[QuickTransfer] FC Chest selected tab: param={recv.EventParam} -> {selectedPage} (addonId={id})");

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
                                Log.Information($"[QuickTransfer] (MMB) Company Chest tab changed to {selectedPage}; stopping previous organize run.");
                        }

                        if (companyChestDeposit.Active &&
                            companyChestDeposit.DestPage != default &&
                            companyChestDeposit.DestPage != selectedPage)
                        {
                            companyChestDeposit.Active = false;
                            if (Configuration.DebugMode)
                                Log.Information($"[QuickTransfer] (Shift+RClick) Company Chest tab changed to {selectedPage}; stopping deposit run.");
                        }
                    }
                    else if (Configuration.DebugMode && id != 0 && now - lastFcChestTabUnmappedLogMs >= 250)
                    {
                        lastFcChestTabUnmappedLogMs = now;
                        Log.Information($"[QuickTransfer] FC Chest tab param unmapped: param={recv.EventParam} (addonId={id})");
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (eventType is AtkEventType.DragDropRollOut || (allowMouseOverCapture && eventType is AtkEventType.MouseOut))
            {
                lastHoverDdi = null;
                lastHoverAddonName = string.Empty;
            }
            else if (eventType is AtkEventType.DragDropRollOver or AtkEventType.DragDropClick ||
                     (allowMouseOverCapture && eventType is AtkEventType.MouseOver))
            {
                if (DragDropHelpers.TryGetDragDropInterfaceFromReceiveEvent(args, recv, eventType, eventData, out uint hAddonId, out AtkDragDropInterface* hDdi) && hDdi != null)
                {
                    nint ptr = (nint)hDdi;
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
                            if (DragDropHelpers.TryGetSlotFromDragDropInterface(hDdi, out InventoryType hoverInvType, out int _))
                            {
                                if (InventoryHelpers.IsCompanyChestType(hoverInvType))
                                    lastHoverCompanyChestPage = (hoverInvType, hAddonId, now);
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
                        Log.Information($"[QuickTransfer] HoverCapture: Addon='{args.AddonName}', EventType={eventType}, Param={recv.EventParam}, DDI=0x{((nint)hDdi):X}");
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
                middleDown = KeyState[vkMButton];
            }
            catch
            {
                // ignore
            }

            bool asyncMiddleDown = IsMouseButtonDown(0x04); // VK_MBUTTON
            bool isMiddleByMask = ((mouseButtonId & 0x04) != 0) || ((dragDropMouseButtonId & 0x04) != 0);
            bool isMiddle = asyncMiddleDown || isMiddleByMask || middleDown == true;

            // Always log (rate-limited) in DebugMode so we can see which event types fire on MMB for this client.
            if (Configuration.DebugMode && now - lastReceiveEventDebugLogMs >= 250)
            {
                lastReceiveEventDebugLogMs = now;
                Log.Information(
                    $"[QuickTransfer] PreReceiveEvent: Addon='{args.AddonName}', Type={eventType}, Param={recv.EventParam}, " +
                    $"MouseBtn={mouseButtonId} (0x{mouseButtonId:X2}), DragBtn={dragDropMouseButtonId} (0x{dragDropMouseButtonId:X2}), " +
                    $"MaskMiddle={(isMiddleByMask ? "1" : "0")}, AsyncMiddle={(asyncMiddleDown ? "1" : "0")}, KeyStateMiddle={(middleDown?.ToString() ?? "n/a")}");
            }

            if (now - lastMiddleClickSortMs < 250)
                return;

            // Only proceed on click events; other events can be noisy and don't carry slot payloads.
            if (eventType != AtkEventType.DragDropClick &&
                eventType != AtkEventType.MouseClick &&
                eventType != AtkEventType.MouseDown)
                return;

            if (!isMiddle)
                return;

            if (!DragDropHelpers.TryGetDragDropInterfaceFromReceiveEvent(args, recv, eventType, eventData, out uint addonId, out AtkDragDropInterface* ddi))
                return;
            if (!DragDropHelpers.TryGetSlotFromDragDropInterface(ddi, out InventoryType invType, out int slot))
                return;

            // Do not require a non-empty slot; "Sort" can be invoked from empty slots/spaces.

            pendingMiddleClickSortRequest = (invType, slot, addonId, now);
            pendingMiddleClickSortUntilMs = now + 1500;
            lastMiddleClickSortMs = now;

            // Prevent the underlying UI from processing the click further.
            AtkEvent* atkEvent2 = (AtkEvent*)recv.AtkEvent;
            if (atkEvent2 != null)
                atkEvent2->SetEventIsHandled();
        }
        catch
        {
            // ignore
        }
    }

    // Use AtkValueHelpers (implementation moved to AtkValueHelpers.cs)

    private void OnInputNumericPreSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!Configuration.EnableCompanyChest)
                return;

            if (!pendingCompanyChestNumericArmed)
                return;

            if (!string.Equals(args.AddonName, QuickTransferConstants.InputNumericAddonName, StringComparison.OrdinalIgnoreCase))
                return;

            // Only touch this dialog if the Company Chest is open (avoid affecting unrelated InputNumeric uses).
            if (!InventoryHelpers.IsCompanyChestOpen())
                return;

            if (args is not AddonSetupArgs setup)
                return;

            AtkValue* values = (AtkValue*)setup.AtkValues;
            int count = (int)setup.AtkValueCount;
            if (values == null || count < 7)
                return;

            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] InputNumeric PreSetup (armed): AtkValueCount={count}");

            // Guard against cross-confirmation: only touch the prompt we intended (store/remove/sell).
            if (pendingNumericKind != PendingNumericKind.None)
            {
                string prompt = values[6].Type is AtkValueType.String or AtkValueType.ManagedString ? AtkValueHelpers.ReadAtkValueString(values[6]) : string.Empty;
                if (pendingNumericKind == PendingNumericKind.Store && !prompt.Contains("store", StringComparison.OrdinalIgnoreCase))
                    return;
                if (pendingNumericKind == PendingNumericKind.Remove && !prompt.Contains("remove", StringComparison.OrdinalIgnoreCase))
                    return;
                if (pendingNumericKind == PendingNumericKind.Sell && !prompt.Contains("sell", StringComparison.OrdinalIgnoreCase))
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

            uint min = values[2].UInt;
            uint max = values[3].UInt;
            uint desired = max < min ? min : max;

            // Log current/default if present.
            if (Configuration.DebugMode)
            {
                string curStr = values[5].Type == AtkValueType.UInt ? values[5].UInt.ToString() : "n/a";
                Log.Information($"[QuickTransfer] InputNumeric PreSetup: min={min}, max={max}, default={values[4].UInt}, current={curStr}");
            }

            values[4].UInt = desired; // default
            switch (values[5].Type)
            {
                case AtkValueType.UInt:
                    values[5].UInt = desired; // some layouts have current (UInt)
                    break;
                case AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString:
                    AtkValueHelpers.WriteUtf8InPlace(values[5].String, desired.ToString()); // some builds use String current
                    break;
            }

            if (Configuration.DebugMode)
            {
                string prompt = values[6].Type is AtkValueType.String or AtkValueType.ManagedString ? AtkValueHelpers.ReadAtkValueString(values[6]) : string.Empty;
                Log.Information($"[QuickTransfer] InputNumeric PreSetup: prompt='{prompt}', min={min}, max={max}, setDefault={desired}");
            }
        }
        catch(Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] InputNumeric PreSetup failed.");
        }
    }

    private bool StartCompanyChestDeposit(InventoryType sourceType, uint sourceSlot)
    {
        try
        {
            if (!Configuration.EnableCompanyChest)
                return false;
            if (RaptureAtkModule.Instance() == null)
                return false;
            if (!InventoryHelpers.IsCompanyChestOpen())
                return false;
            if (!InventoryHelpers.IsCompanyChestDepositSourceType(sourceType))
                return false;

            if (!InventoryHelpers.TryGetItemInfo(sourceType, (int)sourceSlot, out uint itemId, out bool isHq, out uint qty))
                return false;

            long now = Environment.TickCount64;
            if (!TryResolveCompanyChestActivePage(now, out InventoryType destPage))
            {
                if (Configuration.DebugMode)
                    Log.Information("[QuickTransfer] (Shift+RClick) Company Chest deposit skipped: could not determine active tab.");
                return false;
            }

            companyChestDeposit = new()
            {
                Active = true,
                SourceType = sourceType,
                SourceSlot = sourceSlot,
                ItemId = itemId,
                IsHq = isHq,
                DestPage = destPage,
                NextAttemptAtMs = now,
                ExpiresAtMs = now + 12000,
                Steps = 0,
                LastQty = qty,
                WaitForQtyChangeUntilMs = 0
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
        if (!Configuration.EnableCompanyChest || RaptureAtkModule.Instance() == null || !InventoryHelpers.IsCompanyChestOpen())
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
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out AtkUnitBase* _))
                return;

            if (InventoryHelpers.TryGetItemInfo(companyChestDeposit.SourceType, (int)companyChestDeposit.SourceSlot, out uint _, out bool _, out uint qNow) &&
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
        if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out AtkUnitBase* _))
            return;

        if (now < companyChestDeposit.NextAttemptAtMs)
            return;

        if (!InventoryHelpers.TryGetItemInfo(companyChestDeposit.SourceType, (int)companyChestDeposit.SourceSlot, out uint itemId, out bool isHq, out uint qty) ||
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

        InventoryType[] pages = companyChestDeposit.DestPage != default && InventoryHelpers.IsCompanyChestType(companyChestDeposit.DestPage)
            ? [companyChestDeposit.DestPage]
            : TryResolveCompanyChestActivePage(now, out InventoryType activePage) && InventoryHelpers.IsCompanyChestType(activePage)
                ? [companyChestDeposit.DestPage = activePage]
                : [];
        if (pages.Length == 0)
        {
            companyChestDeposit.Active = false;
            return;
        }

        uint maxStack = InventoryHelpers.GetItemStackSize(itemId);
        bool needsQuantityConfirm = qty > 1 && maxStack > 1;

        // Prefer stacking into an existing stack; otherwise use the first empty slot.
        if (!TryFindCompanyChestBestStackSlot(pages, itemId, isHq, maxStack, out InventoryType destType, out uint destSlot) &&
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
            pendingCompanyChestNumericHalf = false;
        }

        if (Configuration.DebugMode)
            Log.Information($"[QuickTransfer] (Shift+RClick) Company Chest deposit step {companyChestDeposit.Steps}: {companyChestDeposit.SourceType} slot={companyChestDeposit.SourceSlot} -> {destType} slot={destSlot} (page={companyChestDeposit.DestPage}, qty={qty}, stackMax={maxStack}).");
    }

    private void StartCompanyChestOrganize(long now)
    {
        if (!Configuration.EnableCompanyChest || !InventoryHelpers.IsCompanyChestOpen() || RaptureAtkModule.Instance() == null)
            return;

        if (now <= companyChestBusyUntilMs)
            return;

        if (companyChestOrganize.Active && now < companyChestOrganize.ExpiresAtMs)
        {
            // Already running; don't reset progress on repeated MMB presses.
            companyChestOrganize.ExpiresAtMs = Math.Max(companyChestOrganize.ExpiresAtMs, now + 20000);
            if (Configuration.DebugMode)
                Log.Information("[QuickTransfer] (MMB) Company Chest organize already running; ignoring restart.");
            return;
        }

        companyChestBusyHits = 0;

        uint ownerAddonId = 0u;
        try
        {
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out AtkUnitBase* fcc, QuickTransferConstants.WideAddonSearchMaxIndex) && fcc != null)
                ownerAddonId = fcc->Id;
        }
        catch
        {
            // ignore
        }

        InventoryType[] pages = GetCompanyChestInventoryTypes();
        if (pages.Length == 0)
            return;

        companyChestOrganize = new()
        {
            Active = true,
            OwnerAddonId = ownerAddonId,
            NextAttemptAtMs = now,
            ExpiresAtMs = now + 60000,
            Steps = 0,
            Phase = 0, // Stack merge -> compact -> sort
            Pages = pages,
            WaitingForApply = false,
            WaitUntilMs = 0,
            WaitStuckCount = 0,
            WaitObservedChangeAtMs = 0
        };

        if (Configuration.DebugMode)
            Log.Information($"[QuickTransfer] (MMB) Company Chest organize started (pages=[{string.Join(", ", pages)}]).");
    }

    private void StartCompanyChestOrganize(long now, InventoryType selectedPage)
    {
        if (!InventoryHelpers.IsCompanyChestType(selectedPage))
        {
            StartCompanyChestOrganize(now);
            return;
        }

        if (!Configuration.EnableCompanyChest || !InventoryHelpers.IsCompanyChestOpen() || RaptureAtkModule.Instance() == null)
            return;

        if (now <= companyChestBusyUntilMs)
            return;

        if (companyChestOrganize.Active && now < companyChestOrganize.ExpiresAtMs)
        {
            // If a different tab is requested, stop the old run and restart on the new tab.
            if (companyChestOrganize.Pages is { Length: 1 } && companyChestOrganize.Pages[0] != selectedPage)
            {
                companyChestOrganize.Active = false;
            }
            else
            {
                // Same tab: extend expiry but don't reset progress.
                companyChestOrganize.ExpiresAtMs = Math.Max(companyChestOrganize.ExpiresAtMs, now + 20000);
                if (Configuration.DebugMode)
                    Log.Information("[QuickTransfer] (MMB) Company Chest organize already running; ignoring restart.");
                return;
            }
        }

        companyChestBusyHits = 0;

        uint ownerAddonId = 0u;
        try
        {
            if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.FreeCompanyChestAddonName, out AtkUnitBase* fcc, QuickTransferConstants.WideAddonSearchMaxIndex) && fcc != null)
                ownerAddonId = fcc->Id;
        }
        catch
        {
            // ignore
        }

        companyChestOrganize = new()
        {
            Active = true,
            OwnerAddonId = ownerAddonId,
            NextAttemptAtMs = now,
            ExpiresAtMs = now + 60000,
            Steps = 0,
            Phase = 0, // Stack merge -> compact -> sort
            Pages = [selectedPage],
            WaitingForApply = false,
            WaitUntilMs = 0,
            WaitStuckCount = 0,
            WaitObservedChangeAtMs = 0
        };

        if (Configuration.DebugMode)
            Log.Information($"[QuickTransfer] (MMB) Company Chest organize started (selectedPage={selectedPage}).");
    }

    private void ProcessCompanyChestOrganize(long now)
    {
        void LogSkip(string reason)
        {
            if (!Configuration.DebugMode)
                return;

            // Rate-limit skip logs; only log when the reason changes or every 2s.
            if (!string.Equals(lastCompanyChestOrganizeSkipReason, reason, StringComparison.Ordinal) ||
                now - lastCompanyChestOrganizeSkipLogMs >= 2000)
            {
                lastCompanyChestOrganizeSkipReason = reason;
                lastCompanyChestOrganizeSkipLogMs = now;
                Log.Information($"[QuickTransfer] (MMB) Company Chest organize waiting: {reason}");
            }
        }

        (int stepDelayMs, int stabilizeMs, int applyTimeoutMs, int noApplyBackoffMs, int pageRetryMs, int numericStepDelayMs, int numericApplyTimeoutMs) GetTimings()
        {
            // Start fast, but if the server begins rejecting actions (busyHits>0), automatically slow down.
            int tier = Math.Clamp(companyChestBusyHits, 0, 2);
            return tier switch
            {
                0 => (750, 300, 1300, 650, 350, 1500, 3200),
                1 => (1000, 450, 1800, 900, 500, 2200, 4500),
                var _ => (1300, 650, 2500, 1200, 750, 3000, 6000)
            };
        }

        if (!companyChestOrganize.Active)
            return;

        if (now <= companyChestBusyUntilMs)
        {
            LogSkip("busy backoff");
            return;
        }

        if (!Configuration.EnableCompanyChest || RaptureAtkModule.Instance() == null || !InventoryHelpers.IsCompanyChestOpen())
        {
            companyChestOrganize.Active = false;
            return;
        }

        if (now >= companyChestOrganize.ExpiresAtMs || companyChestOrganize.Steps >= 140)
        {
            companyChestOrganize.Active = false;
            return;
        }

        if (InventoryHelpers.TryGetVisibleAddon(QuickTransferConstants.InputNumericAddonName, out AtkUnitBase* _))
        {
            LogSkip("InputNumeric visible");
            return;
        }

        // If the selected page isn't loaded yet (loading spinner), wait.
        try
        {
            InventoryType[] pages0 = companyChestOrganize.Pages;
            InventoryManager* inv0 = InventoryManager.Instance();
            if (inv0 != null && pages0.Length > 0)
            {
                bool allLoaded = true;
                foreach(InventoryType p in pages0)
                {
                    if (!InventoryHelpers.IsContainerLoaded(inv0, p))
                    {
                        allLoaded = false;
                        break;
                    }

                    // Extra readiness guard: even if the container reports loaded, slot pointers can be null for a bit.
                    // If we treat that as "no moves", the organizer will instantly finish without doing anything.
                    if (inv0->GetInventorySlot(p, 0) == null)
                    {
                        allLoaded = false;
                        break;
                    }
                }

                if (!allLoaded)
                {
                    (int stepDelayMs, int stabilizeMs, int applyTimeoutMs, int noApplyBackoffMs, int pageRetryMs, int numericStepDelayMs, int numericApplyTimeoutMs) t = GetTimings();
                    companyChestOrganize.NextAttemptAtMs = now + t.pageRetryMs;
                    LogSkip($"pages not ready yet; waiting. pages=[{string.Join(", ", pages0)}]");
                    return;
                }
            }
        }
        catch
        {
            // ignore
        }

        // Wait for the previous move to apply (Company Chest actions can lag and will fail/spam errors if we spam moves).
        if (companyChestOrganize.WaitingForApply)
        {
            try
            {
                InventoryManager* inv = InventoryManager.Instance();
                if (inv != null)
                {
                    InventoryItem* s = inv->GetInventorySlot(companyChestOrganize.WaitSrcType, (int)companyChestOrganize.WaitSrcSlot);
                    InventoryItem* d = inv->GetInventorySlot(companyChestOrganize.WaitDstType, (int)companyChestOrganize.WaitDstSlot);

                    uint sId = s != null ? s->ItemId : 0u;
                    int sQty = s != null ? s->Quantity : 0;
                    uint dId = d != null ? d->ItemId : 0u;
                    int dQty = d != null ? d->Quantity : 0;

                    bool applied =
                        sId != companyChestOrganize.WaitSrcItemId ||
                        sQty != companyChestOrganize.WaitSrcQty ||
                        dId != companyChestOrganize.WaitDstItemId ||
                        dQty != companyChestOrganize.WaitDstQty;

                    if (applied)
                    {
                        (int stepDelayMs, int stabilizeMs, int applyTimeoutMs, int noApplyBackoffMs, int pageRetryMs, int numericStepDelayMs, int numericApplyTimeoutMs) t = GetTimings();
                        // We saw a change; wait a short stabilization window in case the server rejects and rolls back.
                        if (companyChestOrganize.WaitObservedChangeAtMs == 0)
                        {
                            companyChestOrganize.WaitObservedChangeAtMs = now;
                            LogSkip("waiting for apply (stabilize)");
                            return;
                        }

                        if (now - companyChestOrganize.WaitObservedChangeAtMs < t.stabilizeMs)
                        {
                            LogSkip("waiting for apply (stabilize)");
                            return;
                        }

                        // Stable: allow next move.
                        companyChestOrganize.WaitingForApply = false;
                        companyChestOrganize.WaitUntilMs = 0;
                        companyChestOrganize.WaitStuckCount = 0;
                        companyChestOrganize.WaitObservedChangeAtMs = 0;
                    }
                    else if (companyChestOrganize.WaitObservedChangeAtMs != 0)
                    {
                        // We previously saw a change, but now we're back to the pre-snapshot: likely a server rejection rollback.
                        companyChestBusyHits = Math.Min(companyChestBusyHits + 1, 10);
                        long backoffMs = Math.Min(60000, 5000 * (1 << Math.Min(companyChestBusyHits - 1, 4)));
                        companyChestBusyUntilMs = Math.Max(companyChestBusyUntilMs, now + backoffMs);
                        companyChestOrganize.WaitingForApply = false;
                        companyChestOrganize.WaitObservedChangeAtMs = 0;

                        if (Configuration.DebugMode)
                            Log.Information($"[QuickTransfer] (MMB) Company Chest move rolled back; treating as busy. backoff={backoffMs}ms (hit {companyChestBusyHits}).");

                        if (companyChestBusyHits >= 3)
                            companyChestOrganize.Active = false;
                        return;
                    }
                    else if (now <= companyChestOrganize.WaitUntilMs)
                    {
                        LogSkip("waiting for apply");
                        return;
                    }
                    else
                    {
                        companyChestOrganize.WaitStuckCount++;
                        companyChestOrganize.WaitingForApply = false;
                        companyChestOrganize.WaitObservedChangeAtMs = 0;
                        if (companyChestOrganize.WaitStuckCount >= 3)
                        {
                            companyChestOrganize.Active = false;
                            if (Configuration.DebugMode)
                            {
                                Log.Information("[QuickTransfer] (MMB) Company Chest organize stalled (no inventory change observed); stopping to avoid spam.");
                                Log.Information(
                                    $"[QuickTransfer] (MMB) Stall snapshot: src={companyChestOrganize.WaitSrcType} slot={companyChestOrganize.WaitSrcSlot} " +
                                    $"was(id={companyChestOrganize.WaitSrcItemId},qty={companyChestOrganize.WaitSrcQty}) now(id={sId},qty={sQty}); " +
                                    $"dst={companyChestOrganize.WaitDstType} slot={companyChestOrganize.WaitDstSlot} " +
                                    $"was(id={companyChestOrganize.WaitDstItemId},qty={companyChestOrganize.WaitDstQty}) now(id={dId},qty={dQty});");
                            }
                            return;
                        }

                        // Back off a bit and retry.
                        (int stepDelayMs, int stabilizeMs, int applyTimeoutMs, int noApplyBackoffMs, int pageRetryMs, int numericStepDelayMs, int numericApplyTimeoutMs) t = GetTimings();
                        companyChestOrganize.NextAttemptAtMs = now + t.noApplyBackoffMs;
                        LogSkip("no apply observed; backoff");
                        return;
                    }
                }
            }
            catch
            {
                // ignore; fall through
            }
        }

        if (now < companyChestOrganize.NextAttemptAtMs)
        {
            LogSkip("cooldown");
            return;
        }

        InventoryType[] pages = companyChestOrganize.Pages;
        if (pages.Length == 0)
        {
            companyChestOrganize.Active = false;
            return;
        }

        // Phase 0: merge stacks where possible. (Disabled for FC chest by starting at Phase=1.)
        if (companyChestOrganize.Phase == 0)
        {
            if (TryFindCompanyChestMergeMove(pages, out InventoryType srcType, out uint srcSlot, out InventoryType dstType, out uint dstSlot, out bool needsNumeric))
            {
                // Snapshot BEFORE issuing the move (so we can detect when it applies).
                uint preSrcId = 0u;
                uint preDstId = 0u;
                int preSrcQty = 0;
                int preDstQty = 0;
                try
                {
                    InventoryManager* inv = InventoryManager.Instance();
                    if (inv != null)
                    {
                        TryGetSlotSnapshot(inv, srcType, srcSlot, out preSrcId, out preSrcQty);
                        TryGetSlotSnapshot(inv, dstType, dstSlot, out preDstId, out preDstQty);
                    }
                }
                catch
                {
                    // ignore
                }

                if (!TryCompanyChestMoveItem(srcType, srcSlot, dstType, dstSlot, needsNumeric))
                {
                    companyChestOrganize.Active = false;
                    return;
                }

                companyChestOrganize.WaitingForApply = true;
                companyChestOrganize.WaitSrcType = srcType;
                companyChestOrganize.WaitSrcSlot = srcSlot;
                companyChestOrganize.WaitSrcItemId = preSrcId;
                companyChestOrganize.WaitSrcQty = preSrcQty;
                companyChestOrganize.WaitDstType = dstType;
                companyChestOrganize.WaitDstSlot = dstSlot;
                companyChestOrganize.WaitDstItemId = preDstId;
                companyChestOrganize.WaitDstQty = preDstQty;
                (int stepDelayMs, int stabilizeMs, int applyTimeoutMs, int noApplyBackoffMs, int pageRetryMs, int numericStepDelayMs, int numericApplyTimeoutMs) t = GetTimings();
                companyChestOrganize.WaitUntilMs = now + (needsNumeric ? t.numericApplyTimeoutMs : t.applyTimeoutMs);
                companyChestOrganize.WaitObservedChangeAtMs = 0;

                companyChestOrganize.Steps++;
                // Even after a move applies, add a small delay; Company Chest actions are more latency-sensitive.
                companyChestOrganize.NextAttemptAtMs = now + (needsNumeric ? t.numericStepDelayMs : t.stepDelayMs);

                if (Configuration.AutoConfirmCompanyChestQuantity && needsNumeric)
                {
                    pendingCompanyChestNumericConfirmUntilMs = now + 1500;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Move;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    pendingCompanyChestNumericHalf = false;
                    ArmSuppressInputNumeric(now);
                }

                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] (MMB) Company Chest organize step {companyChestOrganize.Steps}: {srcType} slot={srcSlot} -> {dstType} slot={dstSlot} (phase=stack, numeric={needsNumeric}).");
                return;
            }

            // No more merges; move on to compaction.
            companyChestOrganize.Phase = 1;
        }

        // Phase 1: compact items to fill empty slots from the start.
        if (TryFindCompanyChestCompactionMove(pages, out InventoryType cSrcType, out uint cSrcSlot, out InventoryType cDstType, out uint cDstSlot))
        {
            // Snapshot BEFORE issuing the move (so we can detect when it applies).
            uint preSrcId = 0u;
            uint preDstId = 0u;
            int preSrcQty = 0;
            int preDstQty = 0;
            try
            {
                InventoryManager* inv = InventoryManager.Instance();
                if (inv != null)
                {
                    TryGetSlotSnapshot(inv, cSrcType, cSrcSlot, out preSrcId, out preSrcQty);
                    TryGetSlotSnapshot(inv, cDstType, cDstSlot, out preDstId, out preDstQty);
                }
            }
            catch
            {
                // ignore
            }

            if (!TryCompanyChestMoveItem(cSrcType, cSrcSlot, cDstType, cDstSlot, false))
            {
                companyChestOrganize.Active = false;
                return;
            }

            companyChestOrganize.WaitingForApply = true;
            companyChestOrganize.WaitSrcType = cSrcType;
            companyChestOrganize.WaitSrcSlot = cSrcSlot;
            companyChestOrganize.WaitSrcItemId = preSrcId;
            companyChestOrganize.WaitSrcQty = preSrcQty;
            companyChestOrganize.WaitDstType = cDstType;
            companyChestOrganize.WaitDstSlot = cDstSlot;
            companyChestOrganize.WaitDstItemId = preDstId;
            companyChestOrganize.WaitDstQty = preDstQty;
            (int stepDelayMs, int stabilizeMs, int applyTimeoutMs, int noApplyBackoffMs, int pageRetryMs, int numericStepDelayMs, int numericApplyTimeoutMs) t = GetTimings();
            companyChestOrganize.WaitUntilMs = now + t.applyTimeoutMs;
            companyChestOrganize.WaitObservedChangeAtMs = 0;

            companyChestOrganize.Steps++;
            companyChestOrganize.NextAttemptAtMs = now + t.stepDelayMs;

            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] (MMB) Company Chest organize step {companyChestOrganize.Steps}: {cSrcType} slot={cSrcSlot} -> {cDstType} slot={cDstSlot} (phase=compact).");
            return;
        }

        // No more compaction moves; proceed to sorting.
        if (companyChestOrganize.Phase == 1)
            companyChestOrganize.Phase = 2;

        // Phase 2: reorder stacks by (UI category, itemId, HQ), mimicking the feel of Sort/itemsort.
        if (companyChestOrganize.Phase == 2)
        {
            if (TryFindCompanyChestSortMove(pages, out InventoryType sSrcType, out uint sSrcSlot, out InventoryType sDstType, out uint sDstSlot))
            {
                // Snapshot BEFORE issuing the move (so we can detect when it applies).
                uint preSrcId = 0u;
                uint preDstId = 0u;
                int preSrcQty = 0;
                int preDstQty = 0;
                try
                {
                    InventoryManager* inv = InventoryManager.Instance();
                    if (inv != null)
                    {
                        TryGetSlotSnapshot(inv, sSrcType, sSrcSlot, out preSrcId, out preSrcQty);
                        TryGetSlotSnapshot(inv, sDstType, sDstSlot, out preDstId, out preDstQty);
                    }
                }
                catch
                {
                    // ignore
                }

                if (!TryCompanyChestMoveItem(sSrcType, sSrcSlot, sDstType, sDstSlot, false))
                {
                    companyChestOrganize.Active = false;
                    return;
                }

                companyChestOrganize.WaitingForApply = true;
                companyChestOrganize.WaitSrcType = sSrcType;
                companyChestOrganize.WaitSrcSlot = sSrcSlot;
                companyChestOrganize.WaitSrcItemId = preSrcId;
                companyChestOrganize.WaitSrcQty = preSrcQty;
                companyChestOrganize.WaitDstType = sDstType;
                companyChestOrganize.WaitDstSlot = sDstSlot;
                companyChestOrganize.WaitDstItemId = preDstId;
                companyChestOrganize.WaitDstQty = preDstQty;
                (int stepDelayMs, int stabilizeMs, int applyTimeoutMs, int noApplyBackoffMs, int pageRetryMs, int numericStepDelayMs, int numericApplyTimeoutMs) t = GetTimings();
                companyChestOrganize.WaitUntilMs = now + t.applyTimeoutMs;
                companyChestOrganize.WaitObservedChangeAtMs = 0;

                companyChestOrganize.Steps++;
                companyChestOrganize.NextAttemptAtMs = now + t.stepDelayMs;

                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] (MMB) Company Chest organize step {companyChestOrganize.Steps}: {sSrcType} slot={sSrcSlot} -> {sDstType} slot={sDstSlot} (phase=sort).");
                return;
            }
        }

        // Done (no more moves).
        if (Configuration.DebugMode)
            Log.Information($"[QuickTransfer] (MMB) Company Chest organize done; no moves found. pages=[{string.Join(", ", pages)}]");
        companyChestOrganize.Active = false;
    }

    private bool TryFindCompanyChestMergeMove(
        InventoryType[] pages,
        out InventoryType srcType,
        out uint srcSlot,
        out InventoryType dstType,
        out uint dstSlot,
        out bool needsNumeric)
    {
        srcType = default;
        srcSlot = 0;
        dstType = default;
        dstSlot = 0;
        needsNumeric = false;

        InventoryManager* inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        const int slotCap = 80;

        // Find a destination stack with free space, then a later source stack of same item to merge.
        foreach(InventoryType dt in pages)
        {
            for(int di = 0; di < slotCap; di++)
            {
                InventoryItem* d = inv->GetInventorySlot(dt, di);
                if (d == null)
                    break;
                if (d->ItemId == 0 || d->Quantity <= 0)
                    continue;

                uint itemId = d->ItemId;
                bool isHq = d->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                uint maxStack = InventoryHelpers.GetItemStackSize(itemId);
                if (maxStack <= 1)
                    continue;

                int free = (int)maxStack - d->Quantity;
                if (free <= 0)
                    continue;

                // Find a later stack to merge into this one.
                bool foundDest = false;
                int destGlobalIndex = 0;
                for(int pi = 0; pi < pages.Length; pi++)
                {
                    if (pages[pi] != dt) continue;
                    destGlobalIndex = pi * slotCap + di;
                    foundDest = true;
                    break;
                }
                if (!foundDest)
                    continue;

                for(int p = 0; p < pages.Length; p++)
                {
                    InventoryType st = pages[p];
                    for(int si = 0; si < slotCap; si++)
                    {
                        InventoryItem* s = inv->GetInventorySlot(st, si);
                        if (s == null)
                            break;
                        if (s->ItemId == 0 || s->Quantity <= 0)
                            continue;
                        if (s->ItemId != itemId)
                            continue;
                        bool sHq = s->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                        if (sHq != isHq)
                            continue;

                        int srcGlobalIndex = p * slotCap + si;
                        if (srcGlobalIndex <= destGlobalIndex)
                            continue;
                        if (st == dt && si == di)
                            continue;

                        // Merging stacks usually prompts for quantity.
                        srcType = st;
                        srcSlot = (uint)si;
                        dstType = dt;
                        dstSlot = (uint)di;
                        // Be conservative: if the client shows InputNumeric for this move, we must keep the move state alive.
                        // We auto-confirm max, so this will stack as much as possible.
                        needsNumeric = true;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryFindCompanyChestCompactionMove(
        InventoryType[] pages,
        out InventoryType srcType,
        out uint srcSlot,
        out InventoryType dstType,
        out uint dstSlot)
    {
        srcType = default;
        srcSlot = 0;
        dstType = default;
        dstSlot = 0;

        InventoryManager* inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        const int slotCap = 80;

        // Find first empty, then next non-empty after it.
        for(int dp = 0; dp < pages.Length; dp++)
        {
            InventoryType dt = pages[dp];
            for(int di = 0; di < slotCap; di++)
            {
                InventoryItem* d = inv->GetInventorySlot(dt, di);
                if (d == null)
                    break;
                if (d->ItemId != 0)
                    continue;

                // Found empty destination.
                for(int sp = dp; sp < pages.Length; sp++)
                {
                    InventoryType st = pages[sp];
                    int start = sp == dp ? di + 1 : 0;
                    for(int si = start; si < slotCap; si++)
                    {
                        InventoryItem* s = inv->GetInventorySlot(st, si);
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

    private static bool TryFindCompanyChestSortMove(
        InventoryType[] pages,
        out InventoryType srcType,
        out uint srcSlot,
        out InventoryType dstType,
        out uint dstSlot)
    {
        srcType = default;
        srcSlot = 0;
        dstType = default;
        dstSlot = 0;

        if (pages.Length != 1)
            return false;

        InventoryType page = pages[0];
        InventoryManager* inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        InventoryContainer* c;
        try { c = inv->GetInventoryContainer(page); }
        catch { return false; }
        if (c == null || !c->IsLoaded || c->Size <= 0)
            return false;

        int size = c->Size;
        if (size <= 1)
            return false;

        // Build keys for current slots.
        ChestSortKey[] keys = new ChestSortKey[size];
        bool[] empty = new bool[size];
        for(int i = 0; i < size; i++)
        {
            InventoryItem* it = c->GetInventorySlot(i);
            if (it == null || it->ItemId == 0 || it->Quantity <= 0)
            {
                empty[i] = true;
                keys[i] = default;
                continue;
            }

            uint id = it->ItemId;
            bool hq = it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
            uint cat = InventoryHelpers.GetItemUiCategory(id);
            keys[i] = new(cat, id, hq);
        }

        // Ensure empties are at the end (safety; compaction phase should mostly handle this).
        for(int i = 0; i < size; i++)
        {
            if (!empty[i]) continue;
            for(int j = i + 1; j < size; j++)
            {
                if (!empty[j])
                {
                    srcType = page;
                    srcSlot = (uint)j;
                    dstType = page;
                    dstSlot = (uint)i;
                    return true;
                }
            }
            break;
        }

        // Selection-sort step: for first index i, if there is a smaller key later, swap/move it into i.
        // This uses HandleItemMove's swap behavior for occupied destinations.
        for(int i = 0; i < size; i++)
        {
            if (empty[i])
                break;

            int best = i;
            for(int j = i + 1; j < size; j++)
            {
                if (empty[j]) break; // empties at end
                if (keys[j].CompareTo(keys[best]) < 0)
                    best = j;
            }

            if (best != i && keys[best].CompareTo(keys[i]) < 0)
            {
                srcType = page;
                srcSlot = (uint)best;
                dstType = page;
                dstSlot = (uint)i;
                return true;
            }
        }

        return false;
    }

    private bool TryCompanyChestMoveItem(
        InventoryType sourceType,
        uint sourceSlot,
        InventoryType destType,
        uint destSlot,
        bool keepAliveForInputNumeric)
    {
        RaptureAtkModule* module = RaptureAtkModule.Instance();
        if (module == null)
            return false;

        // IMPORTANT:
        // HandleItemMove expects InventoryType values (e.g. Inventory1=0, FreeCompanyPage1=20000),
        // not "container ids" like 48/57.
        uint srcInvType = (uint)sourceType;
        uint dstInvType = (uint)destType;

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
                    try { Marshal.FreeHGlobal(pendingMoveOutValuePtr); }
                    catch
                    {
                        /* ignore */
                    }
                    pendingMoveOutValuePtr = 0;
                }
                if (pendingMoveAtkValuesPtr != 0)
                {
                    try { Marshal.FreeHGlobal(pendingMoveAtkValuesPtr); }
                    catch
                    {
                        /* ignore */
                    }
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

            for(int i = 0; i < 4; i++) values[i].Type = AtkValueType.UInt;
            values[0].UInt = srcInvType;
            values[1].UInt = sourceSlot;
            values[2].UInt = dstInvType;
            values[3].UInt = destSlot;

            module->HandleItemMove(ret, values, 4);

            if (Configuration.DebugMode)
            {
                InventoryManager* inv = InventoryManager.Instance();
                TryGetSlotSnapshot(inv, sourceType, sourceSlot, out uint sId, out int sQty);
                TryGetSlotSnapshot(inv, destType, destSlot, out uint dId, out int dQty);
                Log.Information(
                    $"[QuickTransfer] (MMB) CompanyChest HandleItemMove: retInt={ret->Int}, " +
                    $"src={sourceType} slot={sourceSlot} (id={sId},qty={sQty}) -> dst={destType} slot={destSlot} (id={dId},qty={dQty}), keepAlive={keepAliveForInputNumeric}");
            }
            return true;
        }
        catch(Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Company Chest HandleItemMove failed.");
            return false;
        }
        finally
        {
            if (localRetAlloc != 0)
            {
                try { Marshal.FreeHGlobal(localRetAlloc); }
                catch
                {
                    /* ignore */
                }
            }
            if (localValuesAlloc != 0)
            {
                try { Marshal.FreeHGlobal(localValuesAlloc); }
                catch
                {
                    /* ignore */
                }
            }
        }
    }

    private static bool TryFindCompanyChestFirstEmptySlot(
        InventoryType[] pages,
        out InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;

        if (pages.Length == 0)
            return false;

        InventoryManager* inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        const int slotCap = 80;
        foreach(InventoryType t in pages)
        {
            for(int i = 0; i < slotCap; i++)
            {
                InventoryItem* item = inv->GetInventorySlot(t, i);
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
        InventoryType[] pages,
        uint itemId,
        bool isHq,
        uint maxStack,
        out InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;

        if (pages.Length == 0 || itemId == 0 || maxStack <= 1)
            return false;

        InventoryManager* inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        const int slotCap = 80;
        int bestFree = 0;
        foreach(InventoryType t in pages)
        {
            for(int i = 0; i < slotCap; i++)
            {
                InventoryItem* it = inv->GetInventorySlot(t, i);
                if (it == null)
                    break;

                if (it->ItemId != itemId)
                    continue;

                bool hq = it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                if (hq != isHq)
                    continue;

                int qty = it->Quantity;
                if (qty <= 0)
                    continue;

                int free = (int)maxStack - qty;
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

            AtkValue* minValue = inputNumeric->AtkValues + 2;
            AtkValue* maxValue = inputNumeric->AtkValues + 3;
            AtkValue* defaultValue = inputNumeric->AtkValues + 4;
            AtkValue* currentValue = inputNumeric->AtkValuesCount > 5 ? (inputNumeric->AtkValues + 5) : null;
            AtkValue* promptVal = inputNumeric->AtkValues + 6;
            string prompt = promptVal->Type is AtkValueType.String or AtkValueType.ManagedString ? AtkValueHelpers.ReadAtkValueString(*promptVal) : string.Empty;

            // Guard: only confirm prompts we expect.
            if (kind == PendingNumericKind.Store && !prompt.Contains("store", StringComparison.OrdinalIgnoreCase))
                return false;
            if (kind == PendingNumericKind.Remove && !prompt.Contains("remove", StringComparison.OrdinalIgnoreCase))
                return false;
            // Trade dialogs may be localized; if we're in Trade mode and Trade window is open, accept it
            // (similar to how Split works - we trust the context rather than requiring exact prompt text)
            if (kind == PendingNumericKind.Trade && !prompt.Contains("trade", StringComparison.OrdinalIgnoreCase))
            {
                // Fallback: if Trade window is open and we're expecting Trade, accept it anyway
                // (prompt might be localized or say "How many would you like to trade?" etc.)
                if (!InventoryHelpers.IsTradeOpen())
                    return false;
            }
            // Vendor sell dialogs may be localized; accept if prompt contains "sell" or vendor is open.
            if (kind == PendingNumericKind.Sell && !prompt.Contains("sell", StringComparison.OrdinalIgnoreCase))
            {
                if (!InventoryHelpers.IsVendorOpen())
                    return false;
            }

            if (minValue->Type != AtkValueType.UInt || maxValue->Type != AtkValueType.UInt || defaultValue->Type != AtkValueType.UInt)
                return false;

            uint min = minValue->UInt;
            uint max = maxValue->UInt;

            // Split dialogs are localized and can also be emitted by InventoryExpansion without "split" in the prompt.
            // Accept if either:
            // - prompt contains "split" (English), OR
            // - max matches the expected qty-1 we recorded when arming the Split.
            if (kind == PendingNumericKind.Split && !prompt.Contains("split", StringComparison.OrdinalIgnoreCase))
            {
                long nowMs = Environment.TickCount64;
                uint expectedMax = pendingSplitExpectedMax;
                bool okByExpected = expectedMax != 0 && nowMs <= pendingSplitExpectedUntilMs && max == expectedMax;
                if (!okByExpected)
                    return false;
            }
            uint desired;
            if (pendingCompanyChestNumericHalf)
            {
                // Split/remove half as evenly as possible.
                // - Split: max is usually (qty-1), so use (max+1)/2.
                // - Remove: max is usually qty, so use max/2.
                if (kind == PendingNumericKind.Remove && max <= 1)
                    return false;
                if (kind == PendingNumericKind.Split && max == 0)
                    return false;
                desired = kind == PendingNumericKind.Remove ? (max / 2) : ((max + 1) / 2);
                pendingCompanyChestNumericHalf = false;
            }
            else if (pendingCompanyChestNumericDesired != 0)
            {
                desired = pendingCompanyChestNumericDesired;
            }
            else
            {
                // Default: max (clamped).
                desired = max < min ? min : max;
            }

            if (desired < min)
                desired = min;
            if (desired > max)
                desired = max;
            if (desired == 0 && min > 0)
                desired = min;

            pendingCompanyChestNumericDesired = desired;

            uint beforeDefault = defaultValue->UInt;
            uint beforeCurrentUInt = (currentValue != null && currentValue->Type == AtkValueType.UInt) ? currentValue->UInt : 0U;
            string beforeCurrentStr = (currentValue != null && currentValue->Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString)
                ? AtkValueHelpers.ReadAtkValueString(*currentValue)
                : string.Empty;

            // Many InputNumeric uses have both "default" and "current" values; set both so OK uses max.
            defaultValue->UInt = desired;
            if (currentValue != null)
            {
                if (currentValue->Type == AtkValueType.UInt)
                {
                    currentValue->UInt = desired;
                }
                else if (currentValue->Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString)
                {
                    // This dialog uses a String "current quantity" slot on your client build.
                    // Overwrite the existing buffer in-place (max is <= 999 so this is safe).
                    string s = desired.ToString();
                    AtkValueHelpers.WriteUtf8InPlace(currentValue->String, s);
                }
            }

            // Critical: Some builds don't actually use AtkValues for the editable quantity; they use the NumericInput component's Raw/Evaluated strings.
            // Set that too, if present, so the OK action applies "desired" instead of a stale value (e.g. 2).
            TrySetInputNumericComponentValue(inputNumeric, desired);

            if (Configuration.DebugMode)
            {
                string curType = currentValue != null ? currentValue->Type.ToString() : "n/a";
                uint afterCurrentUInt = (currentValue != null && currentValue->Type == AtkValueType.UInt) ? currentValue->UInt : 0U;
                string afterCurrentStr = (currentValue != null && currentValue->Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString)
                    ? AtkValueHelpers.ReadAtkValueString(*currentValue)
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

            string desiredStr = desired.ToString();

            for(int i = 0; i < inputNumeric->UldManager.NodeListCount; i++)
            {
                AtkResNode* node = inputNumeric->UldManager.NodeList[i];
                if (node == null)
                    continue;

                if ((int)node->Type < 1000)
                    continue;

                AtkComponentNode* compNode = (AtkComponentNode*)node;
                AtkComponentBase* comp = compNode->Component;
                if (comp == null)
                    continue;

                if (comp->GetComponentType() != ComponentType.NumericInput)
                    continue;

                AtkComponentNumericInput* ni = (AtkComponentNumericInput*)comp;

                // RawString / EvaluatedString are Utf8String.
                AtkValueHelpers.WriteUtf8StringInPlace(&ni->RawString, desiredStr);
                AtkValueHelpers.WriteUtf8StringInPlace(&ni->EvaluatedString, desiredStr);

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

    private bool TrySelectRemoveFromCompanyChestContextMenu()
    {
        try
        {
            AddonContextMenu* ctxMenu = (AddonContextMenu*)AddonHelpers.GetAddonByName("ContextMenu");
            if (ctxMenu == null)
                return false;

            // Find the list component and pick the row whose label is "Remove".
            // FreeCompanyChest uses a Default context menu, so the AgentInventoryContext index-based selection does not apply.
            for(uint listId = 1; listId <= 6; listId++)
            {
                AtkComponentList* list = ctxMenu->GetComponentListById(listId);
                if (list == null)
                    continue;

                int itemCount = list->GetItemCount();
                if (itemCount <= 0 || itemCount > 64)
                    continue;

                for(int i = 0; i < itemCount; i++)
                {
                    CStringPointer labelPtr = list->GetItemLabel(i);
                    if ((byte*)labelPtr == null)
                        continue;

                    string label = Marshal.PtrToStringUTF8(new(labelPtr))?.TrimEnd('\0') ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(label))
                        continue;

                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] ContextMenu listId={listId} row={i} label='{label}'");

                    if (!label.Equals("Remove", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Trigger via callback payload (matches the inventory context menu pattern).
                    AtkValueHelpers.GenerateCallback((AtkUnitBase*)ctxMenu, 0, i, 0U, 0, 0);

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
        catch(Exception ex)
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

            int count = Math.Min((int)ctxAddon->AtkValuesCount, 128);
            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] ContextMenu AtkValuesCount={ctxAddon->AtkValuesCount} (scanning {count}).");
            for(int i = 0; i < count; i++)
            {
                AtkValue v = ctxAddon->AtkValues[i];
                if (v.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString))
                    continue;

                string s = AtkValueHelpers.ReadAtkValueString(v);
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

    // Company Chest transfers must use InventoryType values directly with RaptureAtkModule.HandleItemMove.

    private ModifierMode? GetModifierModeLatched(long nowMs)
    {
        const int latchWindowMs = 180;
        if (KeyState[VirtualKey.MENU] || nowMs - lastAltSeenMs <= latchWindowMs)
            return ModifierMode.Alt;
        if (KeyState[VirtualKey.CONTROL] || nowMs - lastCtrlSeenMs <= latchWindowMs)
            return ModifierMode.Ctrl;
        if (KeyState[VirtualKey.SHIFT] || nowMs - lastShiftSeenMs <= latchWindowMs)
            return ModifierMode.Shift;
        return null;
    }

    private void DebugDumpContextMenu(AgentInventoryContext* agent, int maxItems)
    {
        try
        {
            int max = Math.Min(Math.Min(agent->ContextItemCount, 64), maxItems);
            for(int i = 0; i < max; i++)
            {
                AtkValue param = agent->EventParams[agent->ContexItemStartIndex + i];
                if (param.Type is not (AtkValueType.String or AtkValueType.ManagedString))
                    continue;

                string text = AtkValueHelpers.ReadAtkValueString(param);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                Log.Information($"[QuickTransfer] Menu idx={i}: '{text}'");
            }
        }
        catch(Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Failed to dump context menu.");
        }
    }

    private bool TryMapCompanyChestTabParamToPage(int eventParam, out InventoryType page)
    {
        page = default;
        try
        {
            // IMPORTANT: this mapping is about tab clicks, not how many compartments we *want* to operate on.
            // So we always consider all possible item pages (up to 5), even if the user configured fewer.
            InventoryType[] pages = GetAllCompanyChestItemPages();
            if (pages.Length == 0)
                return false;

            // Free Company Chest (your UI):
            // param=1 -> Items tab 1 (FreeCompanyPage1)
            // param=2 -> Items tab 2 (FreeCompanyPage2)
            // param=3 -> Items tab 3 (FreeCompanyPage3)
            // param=4 -> Items tab 4 (FreeCompanyPage4) [FC rank unlock]
            // param=5 -> Items tab 5 (FreeCompanyPage5) [FC rank unlock]
            // param=6 -> Crystals (NOT an item page)
            if (eventParam < 1 || eventParam > pages.Length)
                return false;

            page = pages[eventParam - 1];
            return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
