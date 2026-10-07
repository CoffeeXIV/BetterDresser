using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Ipc.Exceptions;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace Dresser;

// Dyes of the gear on the character: read from Glamourer's state and written back into it.
public class ColorsWindow : Window
{
    private record Dye(uint Id, string Name, Vector4 Color, byte Shade);

    private const float IconSize = 40;
    // Dyes per palette row; a color group longer than that wraps.
    private const int PaletteColumns = 10;
    private const ImGuiColorEditFlags DyeFlags = ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop | ImGuiColorEditFlags.AlphaPreview;
    // Any error but Glamourer missing: the details are in the log.
    private const string Failed = "Something went wrong, see /xllog.";

    // In the catalog's tab order; Glamourer's state keys are their names. No accessories: none of them take dyes
    // (checked over the whole Item sheet).
    private static readonly ApiEquipSlot[] Slots =
    [
        ApiEquipSlot.MainHand, ApiEquipSlot.OffHand, ApiEquipSlot.Head, ApiEquipSlot.Body, ApiEquipSlot.Hands, ApiEquipSlot.Legs,
        ApiEquipSlot.Feet,
    ];

    private readonly GetState getState = new(Plugin.PluginInterface);
    private readonly Pins pins;
    // Grouped by color like the game's palette.
    private readonly Dye[] dyes;
    private readonly Dictionary<uint, Dye> dyeById;

    private JObject? state;
    private long stateAt;
    // Why the state can't be read, and why the last dye picked didn't go on; empty when fine.
    private string readError = string.Empty;
    private string dyeError = string.Empty;
    private string dyeSearch = string.Empty;

    public ColorsWindow(Pins pins) : base("BetterDresser Colors")
    {
        this.pins = pins;
        Size = new Vector2(360, 560);
        SizeCondition = ImGuiCond.FirstUseEver;

        // Row 0 is "no dye": transparent, so its button shows the checkerboard.
        dyes = Plugin.DataManager.GetExcelSheet<Stain>()
            .OrderBy(s => s.Shade).ThenBy(s => s.SubOrder)
            .Select(s => new Dye(s.RowId, s.Name.ExtractText(), s.RowId == 0 ? Vector4.Zero : ToColor(s.Color), s.Shade))
            .Where(d => d.Id == 0 || d.Name.Length > 0)
            .ToArray();
        dyeById = dyes.ToDictionary(d => d.Id);
    }

    // Stain colors are 0xRRGGBB.
    private static Vector4 ToColor(uint rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1);

    // Twice a second is enough to pick up gear changed from the catalog or elsewhere.
    private void Refresh()
    {
        if (Environment.TickCount64 - stateAt < 500)
            return;

        stateAt = Environment.TickCount64;
        try
        {
            var (result, data) = getState.Invoke(Plugin.Actor);
            state = result == GlamourerApiEc.Success ? data : null;
            readError = result == GlamourerApiEc.Success ? string.Empty : $"Glamourer: {result}";
        }
        catch (IpcNotReadyError)
        {
            state = null;
            readError = "Glamourer is not available.";
        }
        // Logged once, not on each read twice a second.
        catch (Exception ex)
        {
            state = null;
            if (readError != Failed)
                Plugin.Log.Warning(ex, "GetState failed");
            readError = Failed;
        }
    }

    public override void Draw()
    {
        var keep = Plugin.Config.KeepDyes;
        if (ImGui.Checkbox("Keep colors", ref keep))
        {
            Plugin.Config.KeepDyes = keep;
            Plugin.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Items put on from the catalog keep the slot's current colors.");

        Refresh();
        // A dye's error stays until the next dye is picked: the state read right after it would wipe out a shared one.
        var error = readError.Length > 0 ? readError : dyeError;
        if (error.Length > 0)
            ImGui.TextUnformatted(error);
        if (state?["Equipment"] is not JObject equipment)
            return;

        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var icon = IconSize * ImGuiHelpers.GlobalScale;
        var button = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var draw = ImGui.GetWindowDrawList();

        foreach (var slot in Slots)
        {
            // Empty slots have Glamourer's own ids that aren't in the Item sheet.
            if (equipment[slot.ToString()] is not JObject equip || (ulong?)equip["ItemId"] is not { } itemId || itemId > uint.MaxValue
                || sheet.GetRowOrDefault((uint)itemId) is not { } item)
                continue;

            var name = item.Name.ExtractText();
            if (name.Length == 0)
                continue;

            var glamourId = Pins.IdOf(slot, itemId);
            using var pushId = ImRaii.PushId((int)slot);
            var min = ImGui.GetCursorScreenPos();
            var right = min.X + ImGui.GetContentRegionAvail().X;
            // Dye buttons are pinned to the right edge; a long name is clipped before them.
            var buttonsLeft = right - item.DyeCount * (button + spacing) + spacing;

            draw.AddImage(Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.Icon)).GetWrapOrEmpty().Handle, min, min + new Vector2(icon));
            var textPos = min + new Vector2(icon + spacing, (icon - ImGui.GetTextLineHeight()) / 2);
            ImGui.PushClipRect(textPos, new Vector2(buttonsLeft - spacing, min.Y + icon), true);
            draw.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), name);
            ImGui.PopClipRect();

            for (var channel = 0; channel < item.DyeCount; channel++)
            {
                ImGui.SetCursorScreenPos(new Vector2(buttonsLeft + channel * (button + spacing), min.Y + (icon - button) / 2));
                DrawDye(slot, glamourId, equip, channel == 0 ? "Stain" : "Stain2");
            }

            // One item for the whole row, so the next row starts below it.
            ImGui.SetCursorScreenPos(min);
            ImGui.Dummy(new Vector2(right - min.X, icon));
        }
    }

    // A square with the current dye; a click opens the palette.
    private void DrawDye(ApiEquipSlot slot, ulong glamourId, JObject equip, string field)
    {
        var current = (uint?)equip[field] ?? 0;
        var dye = dyeById.GetValueOrDefault(current);
        using var id = ImRaii.PushId(field);
        var size = new Vector2(ImGui.GetFrameHeight());
        if (ImGui.ColorButton("##dye", dye?.Color ?? Vector4.Zero, DyeFlags, size))
        {
            dyeSearch = string.Empty;
            ImGui.OpenPopup("##palette");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(dye?.Name ?? $"Dye #{current}");

        using var popup = ImRaii.Popup("##palette");
        if (popup)
            DrawPalette(slot, glamourId, equip, field, current);
    }

    // Search on top, then a row of squares per color group, the name in a tooltip.
    private void DrawPalette(ApiEquipSlot slot, ulong glamourId, JObject equip, string field, uint current)
    {
        var size = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(PaletteColumns * (size + spacing) - spacing);
        ImGui.InputTextWithHint("##search", "Search", ref dyeSearch, 50);

        byte? shade = null;
        var column = 0;
        foreach (var dye in dyes)
        {
            if (dyeSearch.Length > 0 && !dye.Name.Contains(dyeSearch, StringComparison.OrdinalIgnoreCase))
                continue;

            // A new row for each color group and after every PaletteColumns dyes, a gap between groups.
            if (dye.Shade == shade && column < PaletteColumns)
                ImGui.SameLine();
            else
            {
                if (shade != null && dye.Shade != shade)
                    ImGui.Spacing();
                column = 0;
            }
            shade = dye.Shade;
            column++;

            using var id = ImRaii.PushId((int)dye.Id);
            // The palette stays open to try other dyes; a click outside closes it.
            if (ImGui.ColorButton("##dye", dye.Color, DyeFlags, new Vector2(size)))
                SetDye(slot, glamourId, equip, field, dye.Id);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(dye.Name);
            if (dye.Id == current)
                ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), 0xFFFFFFFF, 0, ImDrawFlags.None, 2);
        }
    }

    // Glamourer has no IPC for dyes alone: the same item goes back on with the new dye, only in this slot.
    private void SetDye(ApiEquipSlot slot, ulong glamourId, JObject equip, string field, uint dye)
    {
        equip[field] = dye;
        try
        {
            var result = pins.Set(slot, glamourId, Pins.Stains(equip));
            dyeError = result == GlamourerApiEc.Success ? string.Empty : $"Glamourer: {result}";
        }
        catch (IpcNotReadyError)
        {
            dyeError = "Glamourer is not available.";
        }
        catch (Exception ex)
        {
            dyeError = Failed;
            Plugin.Log.Warning(ex, "SetItem failed");
        }

        // Read the new state on the next frame.
        stateAt = 0;
    }
}
