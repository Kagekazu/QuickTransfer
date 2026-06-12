using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace QuickTransfer;

public sealed unsafe partial class Plugin
{
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
                Svc.Log.Information($"[QuickTransfer] InputNumeric PreSetup (armed): AtkValueCount={count}");

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
                    Svc.Log.Information($"[QuickTransfer] InputNumeric PreSetup: unexpected types: [2]={values[2].Type}, [3]={values[3].Type}, [4]={values[4].Type}");
                return;
            }

            uint min = values[2].UInt;
            uint max = values[3].UInt;
            uint desired = max < min ? min : max;

            // Log current/default if present.
            if (Configuration.DebugMode)
            {
                string curStr = values[5].Type == AtkValueType.UInt ? values[5].UInt.ToString() : "n/a";
                Svc.Log.Information($"[QuickTransfer] InputNumeric PreSetup: min={min}, max={max}, default={values[4].UInt}, current={curStr}");
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
                Svc.Log.Information($"[QuickTransfer] InputNumeric PreSetup: prompt='{prompt}', min={min}, max={max}, setDefault={desired}");
            }
        }
        catch(Exception ex)
        {
            Svc.Log.Warning(ex, "[QuickTransfer] InputNumeric PreSetup failed.");
        }
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
                Svc.Log.Information($"[QuickTransfer] InputNumeric(Update): prompt='{prompt}', min={min}, max={max}, default {beforeDefault}->{defaultValue->UInt}, currentUInt {beforeCurrentUInt}->{afterCurrentUInt}, currentStr '{beforeCurrentStr}'->'{afterCurrentStr}' (idx5 type {curType})");
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
}
