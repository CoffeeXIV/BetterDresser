using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Glamourer.Api.Enums;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace Dresser;

// Dyes of the gear on the character: read from Glamourer's state and written back into it. Each change is a step in the history.
public class DyesWindow : Window
{
    private record Dye(uint Id, string Name, Vector4 Color, byte Shade);
    // An item worn, as Glamourer's state has it: GlamourId is what SetItem takes for it.
    private record Row(ApiEquipSlot Slot, Item Item, string Name, ulong GlamourId, JObject Equip);

    private const float IconSize = 40;
    // Dyes per palette row; a color group longer than that wraps.
    private const int PaletteColumns = 10;
    private const ImGuiColorEditFlags DyeFlags = ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop | ImGuiColorEditFlags.AlphaPreview;
    // Drag and drop between dye squares.
    private const string DragType = "BetterDresserDye";

    // In the catalog's tab order; Glamourer's state keys are their names. No accessories: none of them take dyes
    // (checked over the whole Item sheet).
    private static readonly ApiEquipSlot[] Slots =
    [
        ApiEquipSlot.MainHand, ApiEquipSlot.OffHand, ApiEquipSlot.Head, ApiEquipSlot.Body, ApiEquipSlot.Hands, ApiEquipSlot.Legs,
        ApiEquipSlot.Feet,
    ];

    private readonly Pins pins;
    private readonly History history;
    // Grouped by color like the game's palette.
    private readonly Dye[] dyes;
    private readonly Dictionary<uint, Dye> dyeById;

    // What the last copy or dye change did, or why it didn't go on: under the items. A state read doesn't wipe it out.
    private string status = string.Empty;
    private string dyeSearch = string.Empty;
    // The dyes copied from an item, as many as it takes; null until one is copied. Kept until the plugin reloads.
    private List<byte>? copied;
    // Names cut to fit, by name: the width and font size they were cut for. One entry per item ever shown here.
    private readonly Dictionary<string, (float Width, float FontSize, string Text)> fitted = [];

    public DyesWindow(Pins pins, History history) : base("BetterDresser Dyes")
    {
        this.pins = pins;
        this.history = history;
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

    // The items worn, in the catalog's tab order. Empty slots have Glamourer's own ids that aren't in the Item sheet.
    private List<Row> Rows()
    {
        var rows = new List<Row>();
        if (pins.State?["Equipment"] is not JObject equipment)
            return rows;

        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var slot in Slots)
        {
            if (equipment[slot.ToString()] is not JObject equip || (ulong?)equip["ItemId"] is not { } itemId || itemId > uint.MaxValue
                || sheet.GetRowOrDefault((uint)itemId) is not { } item)
                continue;

            var name = item.Name.ExtractText();
            if (name.Length > 0)
                rows.Add(new Row(slot, item, name, Pins.IdOf(slot, itemId), equip));
        }
        return rows;
    }

    public override void Draw()
    {
        // Shared with the catalog: one read of Glamourer's state for both.
        pins.RefreshState();
        var rows = Rows();

        var keep = Plugin.Config.KeepDyesOnGearChange;
        if (ImGui.Checkbox("Keep dyes on gear change", ref keep))
        {
            Plugin.Config.KeepDyesOnGearChange = keep;
            Plugin.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Items put on from the catalog keep the slot's current dyes.");

        // A gap between the checkbox and the items.
        ImGui.Dummy(new Vector2(0, ImGui.GetFrameHeight()));

        if (pins.StateError.Length > 0)
            ImGui.TextUnformatted(pins.StateError);

        var icon = IconSize * ImGuiHelpers.GlobalScale;
        var button = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        // Between the name, the dye squares and copy and paste.
        var gap = ImGui.GetFrameHeight();
        var draw = ImGui.GetWindowDrawList();

        foreach (var row in rows)
        {
            using var pushId = ImRaii.PushId((int)row.Slot);
            var min = ImGui.GetCursorScreenPos();
            var right = min.X + ImGui.GetContentRegionAvail().X;
            // Clear is pinned to the right edge, copy and paste before it, the dye squares before them. Every name ends at the same
            // place, with room for two dyes, cut short with an ellipsis: an item that takes no dyes has no squares or buttons.
            var clearLeft = right - button;
            var copyLeft = clearLeft - gap - 2 * button - spacing;
            var dyesLeft = copyLeft - gap - row.Item.DyeCount * (button + spacing) + spacing;
            var nameRight = copyLeft - gap - 2 * button - spacing - gap;
            var buttonsTop = min.Y + (icon - button) / 2;

            draw.AddImage(Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(row.Item.Icon)).GetWrapOrEmpty().Handle, min, min + new Vector2(icon));
            var textPos = min + new Vector2(icon + spacing, (icon - ImGui.GetTextLineHeight()) / 2);
            ImGui.PushClipRect(textPos, new Vector2(nameRight, min.Y + icon), true);
            draw.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), Fit(row.Name, nameRight - textPos.X));
            ImGui.PopClipRect();

            if (row.Item.DyeCount > 0)
            {
                for (var channel = 0; channel < row.Item.DyeCount; channel++)
                {
                    ImGui.SetCursorScreenPos(new Vector2(dyesLeft + channel * (button + spacing), buttonsTop));
                    DrawDye(row, channel);
                }
                ImGui.SetCursorScreenPos(new Vector2(copyLeft, buttonsTop));
                DrawCopyPaste(row);
                ImGui.SetCursorScreenPos(new Vector2(clearLeft, buttonsTop));
                DrawClear(row);
            }

            // One item for the whole row, so the next row starts below it.
            ImGui.SetCursorScreenPos(min);
            ImGui.Dummy(new Vector2(right - min.X, icon));
        }

        // A gap below the items, then the buttons for all of them, pinned to the right edge under the rows' buttons:
        // the copied dyes on every item worn that takes dyes, and no dyes on any of them, each as one step.
        ImGui.Dummy(new Vector2(0, ImGui.GetFrameHeight()));
        const string pasteAll = "Paste to all", clearAll = "Clear all";
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X
            - ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.PaintBrush, pasteAll) - spacing
            - ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.Eraser, clearAll)));
        var dyeable = rows.Where(r => r.Item.DyeCount > 0).ToList();
        using (ImRaii.Disabled(copied == null))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.PaintBrush, pasteAll) && copied != null)
                Apply($"Pasted to all: {Names(copied)}", $"{Names(copied)} on all items",
                    dyeable.Select(r => (r.Slot, r.GlamourId, Pasted(r))).ToArray());
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(copied != null ? $"Paste dyes on every item worn: {Names(copied)}" : "Copy an item's dyes first.");

        ImGui.SameLine();
        var dyed = dyeable.Where(r => !Undyed(r)).ToList();
        using (ImRaii.Disabled(dyed.Count == 0))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Eraser, clearAll))
                Apply("Cleared all dyes", "No dyes on all items", dyed.Select(r => (r.Slot, r.GlamourId, new List<byte> { 0, 0 })).ToArray());
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(dyed.Count > 0 ? "Clear the dyes of every item worn." : "No dyes to clear.");

        // As the main window's status, for dyes only, pinned to the right edge under the buttons.
        if (status.Length > 0)
        {
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(status).X));
            ImGui.TextUnformatted(status);
        }

        // Two lines pinned to the bottom of the window, but never over the rest.
        ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(),
            ImGui.GetWindowContentRegionMax().Y - ImGui.GetTextLineHeightWithSpacing() - ImGui.GetTextLineHeight()));
        ImGui.TextDisabled("Drag and drop a dye onto another one to apply it.");
        ImGui.TextDisabled("Right click a dye to clear it.");
    }

    // The text cut short with three dots to fit the width. ImGui's own ellipsis takes the font's, which sits mid-line.
    // Measured a character at a time, so kept per name until the width or the font size changes.
    private string Fit(string text, float width)
    {
        var fontSize = ImGui.GetFontSize();
        if (fitted.TryGetValue(text, out var last) && last.Width == width && last.FontSize == fontSize)
            return last.Text;

        var cut = "...";
        if (ImGui.CalcTextSize(text).X <= width)
            cut = text;
        else
        {
            for (var length = text.Length - 1; length > 0; length--)
            {
                var shorter = text[..length].TrimEnd() + "...";
                if (ImGui.CalcTextSize(shorter).X <= width)
                {
                    cut = shorter;
                    break;
                }
            }
        }
        fitted[text] = (width, fontSize, cut);
        return cut;
    }

    // A square button like copy and paste: no dyes on the item. Off when it has none already.
    private void DrawClear(Row row)
    {
        var none = Undyed(row);
        using (ImRaii.Disabled(none))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Eraser, new Vector2(ImGui.GetFrameHeight())))
                Apply("Cleared dyes", $"No dyes on {row.Name}", [(row.Slot, row.GlamourId, [0, 0])]);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(none ? "No dyes to clear." : "Clear dyes");
    }

    // Square buttons the size of the dye squares, side by side.
    private void DrawCopyPaste(Row row)
    {
        var size = new Vector2(ImGui.GetFrameHeight());
        var own = Pins.Stains(row.Equip).Take(row.Item.DyeCount).ToList();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Copy, size))
        {
            copied = own;
            status = $"Copied: {Names(own)}";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Copy dyes: {Names(own)}");

        ImGui.SameLine();
        using (ImRaii.Disabled(copied == null))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.PaintBrush, size) && copied != null)
                Apply($"Pasted: {Names(copied)}", $"{Names(copied)} on {row.Name}", [(row.Slot, row.GlamourId, Pasted(row))]);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(copied != null ? $"Paste dyes: {Names(copied)}" : "Copy an item's dyes first.");
    }

    private static bool Undyed(Row row) => Pins.Stains(row.Equip).Take(row.Item.DyeCount).All(s => s == 0);

    private string DyeName(uint id) => dyeById.GetValueOrDefault(id)?.Name ?? $"Dye #{id}";

    private string Names(IEnumerable<byte> ids) => string.Join(" / ", ids.Select(id => DyeName(id)));

    // The copied dyes over the item's own, as many as it takes: with one copied, its own second dye stays.
    private List<byte> Pasted(Row row)
    {
        var stains = Pins.Stains(row.Equip);
        for (var i = 0; i < Math.Min(row.Item.DyeCount, copied!.Count); i++)
            stains[i] = copied[i];
        return stains;
    }

    // A square with the current dye; a click opens the palette. Dragged onto another square, it gives that one its dye.
    // ImGui's own color drag is off (NoDragDrop): the payload is the dye id.
    private unsafe void DrawDye(Row row, int channel)
    {
        var current = Pins.Stains(row.Equip)[channel];
        var dye = dyeById.GetValueOrDefault(current);
        using var id = ImRaii.PushId(channel);
        var size = new Vector2(ImGui.GetFrameHeight());
        // A drag let go over the square it started from counts as a click too: no palette then. ImGui keeps the payload
        // through the frame the mouse is let go in, and drops it on the next.
        if (ImGui.ColorButton("##dye", dye?.Color ?? Vector4.Zero, DyeFlags, size) && ImGui.GetDragDropPayload().IsNull)
        {
            dyeSearch = string.Empty;
            ImGui.OpenPopup("##palette");
        }
        var hovered = ImGui.IsItemHovered();
        // A right click takes this dye off.
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && current != 0)
            ClearDye(row, channel);

        using (var target = ImRaii.DragDropTarget())
        {
            if (target)
            {
                var payload = ImGui.AcceptDragDropPayload(DragType);
                if (!payload.IsNull && *(byte*)payload.Data != current)
                    SetDye(row, channel, *(byte*)payload.Data);
            }
        }
        using (var source = ImRaii.DragDropSource())
        {
            if (source)
            {
                ImGui.SetDragDropPayload(DragType, [current], ImGuiCond.None);
                // Follows the cursor in place of the name tooltip: the dye and its name.
                ImGui.ColorButton("##dragged", dye?.Color ?? Vector4.Zero, DyeFlags, size);
                ImGui.SameLine();
                ImGui.TextUnformatted(DyeName(current));
            }
            else if (hovered)
                ImGui.SetTooltip(current != 0 ? $"{DyeName(current)}\nRight click to clear." : DyeName(current));
        }

        using var popup = ImRaii.Popup("##palette");
        if (popup)
            DrawPalette(row, channel, current);
    }

    // Search on top, then a row of squares per color group, the name in a tooltip.
    private void DrawPalette(Row row, int channel, uint current)
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
            // The palette stays open to try other dyes, each one a step; a click outside closes it.
            if (ImGui.ColorButton("##dye", dye.Color, DyeFlags, new Vector2(size)))
                SetDye(row, channel, dye.Id);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(dye.Name);
            if (dye.Id == current)
                ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), 0xFFFFFFFF, 0, ImDrawFlags.None, 2);
        }
    }

    // One dye of an item, picked in the palette or dragged from another square.
    private void SetDye(Row row, int channel, uint dye)
    {
        var stains = Pins.Stains(row.Equip);
        stains[channel] = (byte)dye;
        Apply($"Dyed: {DyeName(dye)}", $"{DyeName(dye)} on {row.Name}", [(row.Slot, row.GlamourId, stains)]);
    }

    private void ClearDye(Row row, int channel)
    {
        var stains = Pins.Stains(row.Equip);
        stains[channel] = 0;
        Apply("Cleared dye", $"No dye on {row.Name}", [(row.Slot, row.GlamourId, stains)]);
    }

    // Glamourer has no IPC for dyes alone: the same items go back on with the new dyes, only in their slots.
    // All of them are one step in the history, named for the main window's arrows; done goes to the status below the items.
    // A step even if some slot was refused: back undoes the ones that went on.
    private void Apply(string done, string name, (ApiEquipSlot Slot, ulong Id, List<byte> Stains)[] changes)
    {
        if (changes.Length == 0)
            return;

        try
        {
            var slots = changes.Select(c => c.Slot).ToArray();
            var before = history.Take(slots);
            var result = GlamourerApiEc.Success;
            var changed = false;
            foreach (var (slot, id, stains) in changes)
            {
                var set = pins.Set(slot, id, stains);
                if (set == GlamourerApiEc.Success)
                    changed = true;
                else
                    result = set;
            }
            if (changed && before != null)
                history.Add(name, slots, before);
            status = result == GlamourerApiEc.Success ? done : $"Glamourer: {result}";
        }
        catch (Exception ex)
        {
            status = Pins.ErrorText(ex, "SetItem failed");
        }

        pins.ReadStateSoon();
    }
}
