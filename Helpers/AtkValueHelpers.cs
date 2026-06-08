using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Runtime.InteropServices;
using System.Text;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace QuickTransfer;

/// <summary>
///     Utility functions for working with AtkValue structures.
/// </summary>
internal static unsafe class AtkValueHelpers
{
    public static bool IsStringValueType(AtkValueType type)
        => type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString;

    public static string ReadAtkValueString(AtkValue v)
    {
        if ((byte*)v.String == null)
            return string.Empty;

        try
        {
            return Marshal.PtrToStringUTF8(new(v.String))?.TrimEnd('\0') ?? string.Empty;
        }
        catch
        {
            return ReadUtf8(v.String);
        }
    }

    public static string ReadUtf8(byte* ptr)
    {
        if (ptr == null)
            return string.Empty;

        var len = 0;
        while (ptr[len] != 0)
            len++;

        return len <= 0 ? string.Empty : Encoding.UTF8.GetString(ptr, len);
    }

    public static void WriteUtf8InPlace(byte* dst, string value)
    {
        if (dst == null || string.IsNullOrEmpty(value))
            return;

        var bytes = Encoding.UTF8.GetBytes(value);
        var max = Math.Min(bytes.Length, 255);
        for (var i = 0; i < max; i++)
            dst[i] = bytes[i];
        dst[max] = 0;
    }

    public static void WriteUtf8StringInPlace(Utf8String* s, string value)
    {
        if (s == null)
            return;

        WriteUtf8InPlace(s->StringPtr, value);
        s->StringLength = value.Length;
        s->BufUsed = value.Length + 1;
    }

    public static AtkValue* CreateAtkValueArray(params object[] values)
    {
        AtkValue* atkValues = (AtkValue*)Marshal.AllocHGlobal(values.Length * sizeof(AtkValue));
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
            Marshal.FreeHGlobal(new(atkValues));
            return null;
        }

        return atkValues;
    }

    public static void GenerateCallback(AtkUnitBase* unitBase, params object[] values)
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
                    Marshal.FreeHGlobal(new(atkValues[i].String));
            }

            Marshal.FreeHGlobal(new(atkValues));
        }
    }

    public static bool TryGetAtkValueInt(AtkValue* values, int count, int idx, out int value)
    {
        value = 0;
        try
        {
            if (values == null || idx < 0 || idx >= count)
                return false;
            var v = values + idx;
            if (v->Type == AtkValueType.Int)
            {
                value = v->Int;
                return true;
            }
            if (v->Type == AtkValueType.UInt)
            {
                value = unchecked((int)v->UInt);
                return true;
            }
        }
        catch
        {
            // ignore
        }
        return false;
    }

    public static void MakeAddonInvisible(AtkUnitBase* addon)
    {
        if (addon == null)
            return;
        var root = addon->RootNode;
        if (root == null)
            return;

        root->Color.A = 0;
        root->Alpha_2 = 0;
    }

    public static void MakeAddonVisible(AtkUnitBase* addon)
    {
        if (addon == null)
            return;
        var root = addon->RootNode;
        if (root == null)
            return;

        root->Color.A = 255;
        root->Alpha_2 = 255;
    }
}
