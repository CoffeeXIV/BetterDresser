using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc.Exceptions;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace Dresser;

// A slot as SetItem takes it. A list, not byte[]: Dalamud IPC goes through JSON, where byte[] becomes a base64 string.
internal record Look(ulong Id, List<byte> Stains);

// Gear put on while the catalog is open is fixed in Glamourer, so gear changes keep it; on close it turns Once again.
public class Pins
{
    // Glamourer's CustomItemId flags: a model id (primary | secondary << 16 | variant << 32), a bonus item (Glasses row).
    private const ulong CustomItemFlag = 1ul << 48;
    private const ulong BonusItemFlag = 1ul << 49;
    // Any error but Glamourer missing: details in the log.
    private const string Failed = "Something went wrong, see /xllog.";

    // Facewear is a bonus item in Glamourer (SetBonusItem, Bonus.Glasses in the state): a slot of its own here.
    internal const ApiEquipSlot Facewear = (ApiEquipSlot)100;

    private readonly SetItem setItem = new(Plugin.PluginInterface);
    private readonly SetBonusItem setBonusItem = new(Plugin.PluginInterface);
    private readonly GetState getState = new(Plugin.PluginInterface);
    // Fixed slots: the id given and the item id the state shows for it.
    private readonly Dictionary<(int Actor, ApiEquipSlot Slot), (ulong Id, ulong ItemId)> pinned = [];
    private long stateAt;

    // The catalog is open.
    internal bool Active { get; set; }

    // The character's state, one read for all windows; null when it can't be read, and why.
    internal JObject? State { get; private set; }
    internal string StateError { get; private set; } = string.Empty;

    // Twice a second: each read is the whole state.
    internal void RefreshState()
    {
        if (Environment.TickCount64 - stateAt < 500)
            return;

        stateAt = Environment.TickCount64;
        try
        {
            var (result, data) = getState.Invoke(Plugin.Actor);
            State = result == GlamourerApiEc.Success ? data : null;
            StateError = result == GlamourerApiEc.Success ? string.Empty : $"Glamourer: {result}";
        }
        catch (IpcNotReadyError)
        {
            State = null;
            StateError = "Glamourer is not available.";
        }
        // Logged once, not on every read.
        catch (Exception ex)
        {
            State = null;
            if (StateError != Failed)
                Plugin.Log.Warning(ex, "GetState failed");
            StateError = Failed;
        }
    }

    // What the slot has on in the last state read, as SetItem takes it.
    internal ulong? Worn(ApiEquipSlot slot) => SlotIn(State, slot) is { } equip && ItemOf(slot, equip) is { } item ? IdOf(slot, item) : null;

    // After a change: read the state again next frame.
    internal void ReadStateSoon() => stateAt = 0;

    // Status text for a Glamourer call that threw; anything but Glamourer missing goes to the log.
    internal static string ErrorText(Exception ex, string what)
    {
        if (ex is IpcNotReadyError)
            return "Glamourer is not available.";

        Plugin.Log.Warning(ex, what);
        return Failed;
    }

    // Throws when Glamourer is not available.
    internal GlamourerApiEc Set(ApiEquipSlot slot, ulong id, List<byte> stains)
    {
        var actor = Plugin.Actor;
        // Without Once, game gear changes don't take it off.
        var result = Invoke(actor, slot, id, stains, Active ? default : ApplyFlag.Once);
        if (result == GlamourerApiEc.Success && Active && Equipment(actor, slot) is { } equip && ItemOf(slot, equip) is { } item)
            pinned[(actor, slot)] = (id, item);
        return result;
    }

    // Facewear has no dyes.
    private GlamourerApiEc Invoke(int actor, ApiEquipSlot slot, ulong id, List<byte> stains, ApplyFlag flags) =>
        slot == Facewear
            ? setBonusItem.Invoke(actor, ApiBonusSlot.Glasses, id, 0, flags)
            : setItem.Invoke(actor, slot, id, stains, 0, flags);

    // The slots now, from one state read; null without a state or a slot in it. Throws when Glamourer is not available.
    internal Look[]? Get(ApiEquipSlot[] slots)
    {
        var (result, state) = getState.Invoke(Plugin.Actor);
        if (result != GlamourerApiEc.Success)
            return null;

        var looks = new Look[slots.Length];
        for (var i = 0; i < slots.Length; i++)
        {
            if (SlotIn(state, slots[i]) is not { } equip || ItemOf(slots[i], equip) is not { } item)
                return null;
            looks[i] = new Look(IdOf(slots[i], item), Stains(equip));
        }
        return looks;
    }

    // The id SetItem takes for an id from the state. Off-hand-only items (shields, tools) go as a custom model id: Glamourer
    // resolves off-hand item ids only among secondary models. Facewear: the Glasses row without the flag, anything else 0.
    internal static ulong IdOf(ApiEquipSlot slot, ulong item) =>
        slot == Facewear
            ? (item & CustomItemFlag) == 0 && (item & ~BonusItemFlag) is var glasses && glasses <= ushort.MaxValue
                && Plugin.DataManager.GetExcelSheet<Glasses>().HasRow((uint)glasses) ? glasses : 0
        : slot == ApiEquipSlot.OffHand && item <= uint.MaxValue
        && Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault((uint)item) is { } row && row.EquipSlotCategory.Value.OffHand == 1
            ? row.ModelMain | CustomItemFlag
            : item;

    // The same items again with their current dyes, now with Once. Slots changed since are left alone.
    internal void Release()
    {
        foreach (var ((actor, slot), (id, item)) in pinned)
        {
            try
            {
                if (Equipment(actor, slot) is { } equip && ItemOf(slot, equip) == item)
                    Invoke(actor, slot, id, Stains(equip), ApplyFlag.Once);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Releasing a fixed item failed");
            }
        }
        pinned.Clear();
    }

    private JToken? Equipment(int actor, ApiEquipSlot slot)
    {
        var (result, state) = getState.Invoke(actor);
        return result == GlamourerApiEc.Success ? SlotIn(state, slot) : null;
    }

    // State keys for gear slots are ApiEquipSlot names.
    private static JToken? SlotIn(JToken? state, ApiEquipSlot slot) =>
        slot == Facewear ? state?["Bonus"]?["Glasses"] : state?["Equipment"]?[slot.ToString()];

    private static ulong? ItemOf(ApiEquipSlot slot, JToken equip) => (ulong?)equip[slot == Facewear ? "BonusId" : "ItemId"];

    // A slot's dyes, as SetItem takes them.
    internal static List<byte> Stains(JToken equip) => [(byte?)equip["Stain"] ?? 0, (byte?)equip["Stain2"] ?? 0];
}
