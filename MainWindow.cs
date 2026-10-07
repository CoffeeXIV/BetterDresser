using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Ipc.Exceptions;
using Glamourer.Api.Enums;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using ItemFinderModule = FFXIVClientStructs.FFXIV.Client.UI.Misc.ItemFinderModule;
using UIState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState;

namespace Dresser;

public class MainWindow : Window, IDisposable
{
    private record Patch(int ID, string Version, string ExName);
    // Patch is null for items newer than the patch data. Male and Female: who can wear it.
    private record Entry(uint Id, string Name, uint JobCategory, uint Icon, ulong ModelMain, ulong ModelSub, byte Dyes, bool Store, Patch? Patch,
        bool Male, bool Female);

    // The game icon on screenshot tiles; icon mode takes Config.IconSize.
    private const float IconSize = 48;

    private const string MalePack = "male", FemalePack = "female";

    // Screenshots are in shots/<pack>/<folder>/, as DresserPhoto writes them. Pack null: male or female by the gender shown.
    // Both ring tabs share one folder. Weapons are shot sheathed, in back or waist by the job: a model is in one of them,
    // shields are in back.
    // Packs are offered for download in PacksWindow.
    private static readonly (string Label, ApiEquipSlot Slot, string? Pack, string[] Folders)[] Tabs =
    [
        ("Main Hand", ApiEquipSlot.MainHand, "weapons", ["back", "waist"]),
        ("Off Hand", ApiEquipSlot.OffHand, "weapons", ["back"]),
        ("Head", ApiEquipSlot.Head, null, ["head"]),
        ("Body", ApiEquipSlot.Body, null, ["body"]),
        ("Hands", ApiEquipSlot.Hands, null, ["hands"]),
        ("Legs", ApiEquipSlot.Legs, null, ["legs"]),
        ("Feet", ApiEquipSlot.Feet, null, ["feet"]),
        ("Ears", ApiEquipSlot.Ears, "accessories", ["ears"]),
        ("Neck", ApiEquipSlot.Neck, "accessories", ["neck"]),
        ("Wrists", ApiEquipSlot.Wrists, "accessories", ["wrists"]),
        ("Left Ring", ApiEquipSlot.LFinger, "accessories", ["ring"]),
        ("Right Ring", ApiEquipSlot.RFinger, "accessories", ["ring"]),
    ];

    private static readonly GameInventoryType[] Containers =
    [
        GameInventoryType.Inventory1, GameInventoryType.Inventory2, GameInventoryType.Inventory3, GameInventoryType.Inventory4,
        GameInventoryType.EquippedItems,
        GameInventoryType.ArmoryMainHand, GameInventoryType.ArmoryOffHand, GameInventoryType.ArmoryHead, GameInventoryType.ArmoryBody,
        GameInventoryType.ArmoryHands, GameInventoryType.ArmoryLegs, GameInventoryType.ArmoryFeets, GameInventoryType.ArmoryEar,
        GameInventoryType.ArmoryNeck, GameInventoryType.ArmoryWrist, GameInventoryType.ArmoryRings,
    ];

    private readonly Dictionary<ApiEquipSlot, List<Entry>> items = Tabs.ToDictionary(t => t.Slot, _ => new List<Entry>());
    // Screenshot files on disk per pack and folder: ModelMain -> path.
    private readonly Dictionary<(string Pack, string Folder), Dictionary<ulong, string>> shots = [];
    // Height to width of the shots per pack and folder, from the first one loaded.
    private readonly Dictionary<(string Pack, string Folder), float> aspects = [];
    private readonly FileSystemWatcher watcher;
    // When files under shots/ last changed, 0 once rescanned. Set on the watcher's thread and after downloads.
    private long shotsChangedAt;
    private readonly ColorsWindow colorsWindow;
    private readonly PacksWindow packsWindow;
    private readonly Pins pins;
    private readonly History history;
    // Patch filter options, newest first: each expansion's name, then the versions of its patches that added gear.
    private List<(string Name, bool Expansion)> patchFilters = [];
    // The widest of them, measured again only when the font size changes: the list is fixed once loaded.
    private (float FontSize, float Width) patchNamesWidth;

    private string search = string.Empty;
    private string status = string.Empty;
    private uint cachedJob = uint.MaxValue;
    private HashSet<uint> jobCategories = [];
    private readonly HashSet<uint> owned = [];
    private long ownedAt;
    // Gender picked with the button in the status bar, for that character only: not saved, dropped on switching characters.
    private (ulong Character, bool Female)? picked;

    public MainWindow(ColorsWindow colorsWindow, PacksWindow packsWindow, Pins pins) : base("BetterDresser")
    {
        this.colorsWindow = colorsWindow;
        this.packsWindow = packsWindow;
        this.pins = pins;
        history = new History(pins);
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(350, 400), MaximumSize = new Vector2(float.MaxValue) };
        LoadItems();
        LoadShots();

        // Files copied in or removed show up on their own. Too many changes at once overflow the watcher (Error): rescan all the same.
        Directory.CreateDirectory(ShotsRoot);
        watcher = new FileSystemWatcher(ShotsRoot) { IncludeSubdirectories = true };
        watcher.Created += OnShotsChanged;
        watcher.Deleted += OnShotsChanged;
        watcher.Renamed += OnShotsChanged;
        watcher.Error += OnShotsChanged;
        watcher.EnableRaisingEvents = true;
        // Downloads and deletes rescan even if the watcher has missed them: under Wine it may not report changes.
        packsWindow.ShotsChanged += OnShotsChanged;
    }

    public void Dispose()
    {
        packsWindow.ShotsChanged -= OnShotsChanged;
        watcher.Dispose();
    }

    // Gear put on while the catalog is open outlasts job changes. The first time, the screenshot packs are offered.
    public override void OnOpen()
    {
        pins.Active = true;
        if (Plugin.Config.PacksOffered)
            return;
        Plugin.Config.PacksOffered = true;
        Plugin.Config.Save();
        packsWindow.IsOpen = true;
    }

    public override void OnClose()
    {
        pins.Active = false;
        pins.Release();
    }

    internal static string ShotsRoot => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "shots");

    private void OnShotsChanged(object? sender, EventArgs e) => shotsChangedAt = Environment.TickCount64;

    // Same layout as DresserPhoto writes: shots/<pack>/<folder>/<ModelMain>.<jpg|png>.
    // About 10-20 ms for three full packs (23 000 files), fine to do in a frame now and then.
    private void LoadShots()
    {
        shotsChangedAt = 0;
        shots.Clear();
        aspects.Clear();
        var root = ShotsRoot;
        if (!Directory.Exists(root))
            return;

        try
        {
            foreach (var packDir in Directory.EnumerateDirectories(root))
            {
                var pack = Path.GetFileName(packDir);
                foreach (var dir in Directory.EnumerateDirectories(packDir))
                {
                    var files = shots[(pack, Path.GetFileName(dir))] = [];
                    foreach (var path in Directory.EnumerateFiles(dir))
                    {
                        var ext = Path.GetExtension(path).ToLowerInvariant();
                        if (ext is ".jpg" or ".png" && ulong.TryParse(Path.GetFileNameWithoutExtension(path), out var model))
                            files[model] = path;
                    }
                }
            }
        }
        // A folder removed while it was scanned, by a pack being deleted or replaced: scan again in a second.
        // Windows denies access to a folder that is being deleted.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            shotsChangedAt = Environment.TickCount64;
        }
    }

    private bool ShowFemale => picked?.Female ?? Plugin.CharacterFemale;

    internal static string GenderPack(bool female) => female ? FemalePack : MalePack;

    // DresserPhoto crops all shots of a folder with one frame, so any one of them gives the tile shape. 1:2 until one has loaded.
    private float ShotAspect(string pack, string folder)
    {
        if (aspects.TryGetValue((pack, folder), out var aspect))
            return aspect;
        if (!shots.TryGetValue((pack, folder), out var files) || files.Count == 0
            || !Plugin.TextureProvider.GetFromFile(files.Values.First()).TryGetWrap(out var image, out _))
            return 2;
        return aspects[(pack, folder)] = (float)image.Height / image.Width;
    }

    // The folder whose shots give the tab's tile shape. A job's weapons all hang in one place,
    // so it is the first folder with any of the list in it.
    private string ShapeFolder(string pack, string[] folders, List<Entry> list) =>
        folders.Length == 1 ? folders[0]
            : folders.FirstOrDefault(f => shots.TryGetValue((pack, f), out var files) && list.Any(e => files.ContainsKey(e.ModelMain))) ?? folders[0];

    // Gendered tabs: the pack of the gender shown, but items it can't wear were shot on the other gender only.
    // Of several folders the model is in one: the first that has it.
    private string? ShotOf(string? pack, string[] folders, Entry e, bool female)
    {
        pack ??= GenderPack(female ? e.Female : !e.Male);
        foreach (var folder in folders)
            if (shots.TryGetValue((pack, folder), out var files) && files.TryGetValue(e.ModelMain, out var path))
                return path;
        return null;
    }

    private void LoadItems()
    {
        // Online Store items, as listed for the Dream Fitting try-on at inns. Not every store item is there.
        var store = new HashSet<uint>();
        foreach (var set in Plugin.DataManager.GetExcelSheet<FittingShopItemSet>())
            foreach (var item in set.Item)
                if (item.RowId != 0)
                    store.Add(item.RowId);

        var added = LoadPatches();
        foreach (var item in Plugin.DataManager.GetExcelSheet<Item>())
        {
            var name = item.Name.ExtractText();
            if (name.Length == 0 || item.ModelMain == 0 || item.EquipSlotCategory.RowId == 0)
                continue;

            if (SlotOf(item.EquipSlotCategory.Value) is { } slot)
            {
                var race = item.EquipRestriction.Value;
                items[slot].Add(new Entry(item.RowId, name, item.ClassJobCategory.RowId, item.Icon, item.ModelMain, item.ModelSub, item.DyeCount,
                    store.Contains(item.RowId), added.GetValueOrDefault(item.RowId), race.Male, race.Female));
            }
        }

        // Newest first: Item row ids grow with each patch.
        foreach (var list in items.Values)
            list.Sort((a, b) => b.Id.CompareTo(a.Id));

        patchFilters = items.Values.SelectMany(l => l).Select(e => e.Patch).OfType<Patch>().Distinct()
            .OrderByDescending(p => p.ID).GroupBy(p => p.ExName)
            // The patch list has two 2.0 entries.
            .SelectMany(g => g.Select(p => (p.Version, false)).Distinct().Prepend((g.Key, true))).ToList();

        // Rings fit either hand: both ring tabs list the same items.
        items[ApiEquipSlot.LFinger] = items[ApiEquipSlot.RFinger];
    }

    // Item id -> the patch that added it. The game data doesn't say, so it comes from xivapi's
    // ffxiv-datamining-patches, embedded as is: patches/Item.json maps ids to patch list ids.
    private static Dictionary<uint, Patch> LoadPatches()
    {
        var assembly = typeof(MainWindow).Assembly;
        using var list = assembly.GetManifestResourceStream("patchlist.json")!;
        var byId = JsonSerializer.Deserialize<List<Patch>>(list)!.ToDictionary(p => p.ID);
        using var added = assembly.GetManifestResourceStream("Item.json")!;
        // A patch missing from the list (the two files updated apart) leaves its items under All only.
        return JsonSerializer.Deserialize<Dictionary<uint, int>>(added)!.Where(p => byId.ContainsKey(p.Value))
            .ToDictionary(p => p.Key, p => byId[p.Value]);
    }

    private static ApiEquipSlot? SlotOf(EquipSlotCategory c)
    {
        if (c.MainHand == 1) return ApiEquipSlot.MainHand;
        if (c.OffHand == 1) return ApiEquipSlot.OffHand;
        if (c.Head == 1) return ApiEquipSlot.Head;
        if (c.Body == 1) return ApiEquipSlot.Body;
        if (c.Gloves == 1) return ApiEquipSlot.Hands;
        if (c.Legs == 1) return ApiEquipSlot.Legs;
        if (c.Feet == 1) return ApiEquipSlot.Feet;
        if (c.Ears == 1) return ApiEquipSlot.Ears;
        if (c.Neck == 1) return ApiEquipSlot.Neck;
        if (c.Wrists == 1) return ApiEquipSlot.Wrists;
        if (c.FingerR == 1 || c.FingerL == 1) return ApiEquipSlot.RFinger;
        return null;
    }

    // ClassJobCategory: column 0 is Name, then one bool column per job in ClassJob row order.
    // Read raw by index: Lumina's generated sheet lags behind new jobs (BST is still "Unknown0").
    private void UpdateJobCategories()
    {
        var job = Plugin.PlayerState.ClassJob.RowId;
        if (job == cachedJob)
            return;

        cachedJob = job;
        var column = (int)job + 1;
        var sheet = Plugin.DataManager.Excel.GetSheet<RawRow>(null, "ClassJobCategory");
        jobCategories = column < sheet.Columns.Count
            ? sheet.Where(r => r.ReadBoolColumn(column)).Select(r => r.RowId).ToHashSet()
            : [];
    }

    // Bags, armory and equipped gear are read live. Retainers, saddlebags and dresser come from the game's
    // own "Search for Item" cache: it is saved per character and filled once each of them has been opened.
    // The armoire isn't in that cache, so it is kept in Config.
    private unsafe void LoadOwned()
    {
        owned.Clear();
        foreach (var type in Containers)
            foreach (var item in Plugin.GameInventory.GetInventoryItems(type))
                owned.Add(item.BaseItemId);

        LoadArmoire();

        var finder = ItemFinderModule.Instance();
        if (finder == null)
            return;

        AddOwned(finder->SaddleBagItemIds);
        AddOwned(finder->PremiumSaddleBagItemIds);
        foreach (var (_, retainer) in finder->RetainerInventories)
        {
            AddOwned(retainer.Value->ItemIds);
            AddOwned(retainer.Value->EquippedItemIds);
        }

        // An outfit takes one dresser slot. Its bits mark the pieces missing from it, so 0 is the whole set.
        var sets = Plugin.DataManager.GetExcelSheet<MirageStoreSetItem>();
        var dresser = finder->GlamourDresserItemIds;
        for (var i = 0; i < dresser.Length; i++)
        {
            var id = BaseItemId(dresser[i]);
            if (id == 0)
                continue;

            owned.Add(id);
            if (sets.GetRowOrDefault(id) is not { } set)
                continue;

            var bits = finder->GlamourDresserItemSetUnlockBits[i];
            uint[] pieces =
            [
                set.MainHand.RowId, set.OffHand.RowId, set.Head.RowId, set.Body.RowId, set.Hands.RowId, set.Legs.RowId,
                set.Feet.RowId, set.Earrings.RowId, set.Necklace.RowId, set.Bracelets.RowId, set.Ring.RowId,
            ];
            for (var p = 0; p < pieces.Length; p++)
                if ((bits & (1 << p)) == 0)
                    owned.Add(pieces[p]);
        }
    }

    // The game fetches the armoire from the server only when it is opened at an inn,
    // so the last contents seen are saved per character. A switch to another character drops it in the game.
    private unsafe void LoadArmoire()
    {
        var character = Plugin.PlayerState.ContentId;
        if (character == 0)
            return;

        var cabinet = &UIState.Instance()->Cabinet;
        if (cabinet->IsCabinetLoaded())
        {
            var armoire = new HashSet<uint>();
            foreach (var row in Plugin.DataManager.GetExcelSheet<Cabinet>())
                if (cabinet->IsItemInCabinet(row.RowId))
                    armoire.Add(row.Item.RowId);

            if (!Plugin.Config.Armoire.TryGetValue(character, out var saved) || !saved.SetEquals(armoire))
            {
                Plugin.Config.Armoire[character] = armoire;
                Plugin.Config.Save();
            }
        }

        if (Plugin.Config.Armoire.TryGetValue(character, out var items))
            owned.UnionWith(items);
    }

    private void AddOwned(Span<uint> ids)
    {
        foreach (var id in ids)
            owned.Add(BaseItemId(id));
    }

    // Cached ids keep the quality offset: HQ is +1,000,000, collectable +500,000.
    private static uint BaseItemId(uint id) => id % 500_000;

    private static float CheckboxWidth(string label) =>
        ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;

    // As ImGuiComponents.IconButton sizes itself: the glyph in the icon font and the frame padding.
    private static float IconButtonWidth(FontAwesomeIcon icon)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        return ImGui.CalcTextSize(icon.ToIconString()).X + ImGui.GetStyle().FramePadding.X * 2;
    }

    public override void Draw()
    {
        const string storeToggle = "Hide store";
        const string ownedToggle = "Owned";
        const string toggle = "Screenshots";
        ImGui.SetNextItemWidth(-CheckboxWidth(storeToggle) - CheckboxWidth(ownedToggle) - CheckboxWidth(toggle) - ImGui.GetStyle().ItemSpacing.X * 3);
        ImGui.InputTextWithHint("##search", "Search", ref search, 100);
        ImGui.SameLine();
        var hideStore = Plugin.Config.HideStore;
        if (ImGui.Checkbox(storeToggle, ref hideStore))
        {
            Plugin.Config.HideStore = hideStore;
            Plugin.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hide Online Store items. The game's Dream Fitting list is used, and some older store items are missing from it.");

        ImGui.SameLine();
        var ownedOnly = Plugin.Config.OwnedOnly;
        if (ImGui.Checkbox(ownedToggle, ref ownedOnly))
        {
            Plugin.Config.OwnedOnly = ownedOnly;
            Plugin.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only items this character has. The armoire shows up after you open it once at an inn.");
        // Rebuild at most once a second: cheap, and picks up a retainer or the dresser visited while the window is open.
        if (ownedOnly && Environment.TickCount64 - ownedAt > 1000)
        {
            LoadOwned();
            ownedAt = Environment.TickCount64;
        }

        ImGui.SameLine();
        var screenshots = Plugin.Config.Screenshots;
        if (ImGui.Checkbox(toggle, ref screenshots))
        {
            Plugin.Config.Screenshots = screenshots;
            Plugin.Config.Save();
        }

        // Rescan once the files have been left alone for a second: copying or unpacking a pack changes thousands of them.
        if (shotsChangedAt != 0 && Environment.TickCount64 - shotsChangedAt > 1000)
            LoadShots();

        // A frame tall whatever is on it, so the grid below doesn't jump.
        ImGui.AlignTextToFramePadding();
        var tileSize = screenshots ? Plugin.Config.ShotWidth : Plugin.Config.IconSize;
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        // Clamped on Ctrl+click input too: ImGui lets typed values past the bounds otherwise.
        if (ImGui.SliderInt("##size", ref tileSize, screenshots ? Config.MinShotWidth : Config.MinIconSize,
                screenshots ? Config.MaxShotWidth : Config.MaxIconSize, "Image size %d", ImGuiSliderFlags.AlwaysClamp))
        {
            if (screenshots)
                Plugin.Config.ShotWidth = tileSize;
            else
                Plugin.Config.IconSize = tileSize;
        }
        // Save once the drag ends, not every frame.
        if (ImGui.IsItemDeactivatedAfterEdit())
            Plugin.Config.Save();
        ImGui.SameLine();
        ImGui.TextUnformatted(status);
        // Pinned to the right edge, but never over the status text.
        const string hint = "Right click: copy name, Shift+right click: Search for Item";
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(hint).X));
        ImGui.TextDisabled(hint);

        // Colors is a plain button at the right end of the tab row. ImGui's trailing tab buttons follow
        // the last tab and reach the edge only when the tabs fill the bar.
        const string colors = "Colors";
        var colorsWidth = ImGui.CalcTextSize(colors).X + ImGui.GetStyle().FramePadding.X * 2;
        var row = ImGui.GetCursorPos();
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - colorsWidth);
        if (ImGui.Button(colors))
            colorsWindow.Toggle();
        ImGui.SetCursorPos(row);

        if (picked is { } p && p.Character != Plugin.PlayerState.ContentId)
            picked = null;
        var female = ShowFemale;

        // The tab bar takes its width from the work rect as it begins: narrow it to leave room for the button,
        // then restore it for the tab contents.
        var window = ImGuiP.GetCurrentWindow();
        var reserved = colorsWidth + ImGui.GetStyle().ItemSpacing.X;
        window.WorkRect.Max.X -= reserved;
        using (var tabBar = ImRaii.TabBar("##slots"))
        {
            window.WorkRect.Max.X += reserved;
            if (tabBar)
            {
                foreach (var (label, slot, pack, folders) in Tabs)
                {
                    using var tab = ImRaii.TabItem(label);
                    if (tab)
                        DrawList(slot, pack, folders, female);
                }
            }
        }

        DrawStatusBar(female);
    }

    // One row at the bottom of the window: the list above leaves room for it. On the left, in screenshot mode the packs button,
    // the gender switch and the update notice, then the armoire warning. The patch filter is pinned to the right edge,
    // but never over the rest.
    private void DrawStatusBar(bool female)
    {
        ImGui.SetCursorPosY(ImGui.GetWindowContentRegionMax().Y - ImGui.GetFrameHeight());
        ImGui.AlignTextToFramePadding();
        if (Plugin.Config.Screenshots)
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Download))
                packsWindow.Toggle();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Screenshot packs: download, update, delete.");
            ImGui.SameLine();

            // Two buttons side by side: the gender shown lit up, the other one grayed out.
            foreach (var f in new[] { false, true })
            {
                using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), f == female)
                    .Push(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled), f != female))
                {
                    if (ImGuiComponents.IconButton(f ? FontAwesomeIcon.Venus : FontAwesomeIcon.Mars) && f != female)
                        picked = (Plugin.PlayerState.ContentId, f);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(f == female ? $"Showing {GenderPack(f)} models." : $"Show {GenderPack(f)} models.");
                ImGui.SameLine(0, f ? -1 : ImGui.GetStyle().ItemInnerSpacing.X);
            }

            if (packsWindow.UpdateSize > 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, $"Screenshot update: {PacksWindow.FormatSize(packsWindow.UpdateSize)}");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.SetTooltip("Click to open the screenshot packs.");
                }
                if (ImGui.IsItemClicked())
                    packsWindow.IsOpen = true;
                ImGui.SameLine();
            }
        }

        // Other sources are cached by the game itself; only the armoire can be missing.
        var character = Plugin.PlayerState.ContentId;
        if (Plugin.Config.OwnedOnly && character != 0 && !Plugin.Config.Armoire.ContainsKey(character))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Armoire items are missing: open the armoire once at an inn.");
            ImGui.SameLine();
        }

        // Back and forward through the items put on: in the middle of the row, or right after the left side if it reaches past that.
        var style = ImGui.GetStyle();
        var arrows = IconButtonWidth(FontAwesomeIcon.ArrowLeft) + style.ItemInnerSpacing.X + IconButtonWidth(FontAwesomeIcon.ArrowRight);
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), (ImGui.GetWindowContentRegionMin().X + ImGui.GetWindowContentRegionMax().X - arrows) / 2));
        foreach (var forward in new[] { false, true })
        {
            var name = forward ? history.ForwardName : history.BackName;
            using (ImRaii.Disabled(name == null))
            {
                if (ImGuiComponents.IconButton(forward ? FontAwesomeIcon.ArrowRight : FontAwesomeIcon.ArrowLeft))
                    Travel(forward);
            }
            if (name != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(forward ? $"Redo: {name}" : $"Undo: {name}");
            ImGui.SameLine(0, forward ? -1 : style.ItemInnerSpacing.X);
        }

        const string label = "Filter by patch:";
        const string all = "All";
        var fontSize = ImGui.GetFontSize();
        if (patchNamesWidth.FontSize != fontSize)
            patchNamesWidth = (fontSize, patchFilters.Select(f => f.Name).Append(all).Max(v => ImGui.CalcTextSize(v).X));
        var comboWidth = patchNamesWidth.Width + style.FramePadding.X * 2 + ImGui.GetFrameHeight();
        // Kept clear of the window's resize grip in the bottom right corner.
        var width = ImGui.CalcTextSize(label).X + style.ItemSpacing.X + comboWidth + ImGui.GetFrameHeight();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - width));
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(comboWidth);
        using (var combo = ImRaii.Combo("##patch", Plugin.Config.Patch.Length > 0 ? Plugin.Config.Patch : all, ImGuiComboFlags.HeightLarge))
        {
            if (combo)
            {
                // All goes on top, level with the expansions; patches are indented under their expansion.
                foreach (var (name, expansion) in patchFilters.Prepend((string.Empty, true)))
                {
                    if (!expansion)
                        ImGui.Indent();
                    if (ImGui.Selectable(name.Length > 0 ? name : all, name == Plugin.Config.Patch))
                    {
                        Plugin.Config.Patch = name;
                        Plugin.Config.Save();
                    }
                    if (!expansion)
                        ImGui.Unindent();
                }
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only items added in this patch or expansion. Items newer than the plugin's patch data show only under All.");
    }

    private void DrawList(ApiEquipSlot slot, string? pack, string[] folders, bool female)
    {
        IEnumerable<Entry> list = items[slot];
        if (slot is ApiEquipSlot.MainHand or ApiEquipSlot.OffHand)
        {
            UpdateJobCategories();
            list = list.Where(e => jobCategories.Contains(e.JobCategory));
        }

        if (Plugin.Config.OwnedOnly)
            list = list.Where(e => owned.Contains(e.Id));

        if (Plugin.Config.HideStore)
            list = list.Where(e => !e.Store);

        // A patch version or an expansion name: they never collide.
        var patch = Plugin.Config.Patch;
        if (patch.Length > 0)
            list = list.Where(e => e.Patch is { } p && (p.Version == patch || p.ExName == patch));

        if (search.Length > 0)
            list = list.Where(e => e.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        // Leave room for the status bar at the bottom of the window, with a small gap above it.
        using var child = ImRaii.Child("##list", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() - ImGui.GetStyle().ItemSpacing.Y));
        if (!child)
            return;

        var entries = list.ToList();
        var screenshots = Plugin.Config.Screenshots;
        // Every tile in a tab is the same size, so the list can skip the rows out of view.
        var shapePack = pack ?? GenderPack(female);
        var size = screenshots
            ? new Vector2(1, ShotAspect(shapePack, ShapeFolder(shapePack, folders, entries))) * Plugin.Config.ShotWidth * ImGuiHelpers.GlobalScale
            : new Vector2(Plugin.Config.IconSize * ImGuiHelpers.GlobalScale);
        // The same gap across and down: the style's vertical item spacing is smaller than the horizontal one.
        var gap = ImGui.GetStyle().ItemSpacing.X;
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(gap));
        var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + gap) / (size.X + gap)));

        ImGuiClip.ClippedDraw(entries, e => DrawTile(slot, e, size, screenshots, screenshots ? ShotOf(pack, folders, e, female) : null),
            columns, size.Y + gap);
    }

    // Dye channels as the game marks them: small circles down the right edge from the top right corner, the first one on top.
    private static void DrawDyes(ImDrawListPtr draw, Vector2 topRight, float icon, byte count)
    {
        var radius = icon / 8;
        for (var i = 0; i < count; i++)
        {
            var center = topRight + new Vector2(-radius, radius + i * radius * 2.4f);
            draw.AddCircleFilled(center, radius, 0x60000000);
            draw.AddCircle(center, radius, 0xFF000000, 0, Math.Max(1, radius / 4));
        }
    }

    // Online Store mark: a yellow dollar sign with a black outline, flush with the bottom right corner.
    // Under a third of the icon tall, so it stays clear of the two dye circles above it.
    private static unsafe void DrawStore(ImDrawListPtr draw, Vector2 bottomRight, float icon)
    {
        var font = UiBuilder.IconFont;
        var size = icon / 3.5f;
        // Same width as the dye circles' outline.
        var outline = Math.Max(1, icon / 32);
        // Glyph bounds are given at the font's own size: the ink itself, outline included, goes into the corner.
        var glyph = font.FindGlyph(FontAwesomeIcon.DollarSign.ToIconChar());
        var at = bottomRight - new Vector2(glyph->X1, glyph->Y1) * (size / font.FontSize) - new Vector2(outline);

        var text = FontAwesomeIcon.DollarSign.ToIconString();
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    draw.AddText(font, size, at + new Vector2(dx, dy) * outline, 0xFF000000, text);
        draw.AddText(font, size, at, ImGui.GetColorU32(ImGuiColors.DalamudYellow), text);
    }

    // A gray tile with the screenshot fitted inside and the game icon in the top left corner;
    // without a screenshot (missing, still loading or icon mode) the icon sits in the middle.
    // In icon mode the tile is icon-sized, so the icon covers it with no frame around.
    private void DrawTile(ApiEquipSlot slot, Entry e, Vector2 size, bool screenshots, string? shot)
    {
        using var id = ImRaii.PushId((int)e.Id);
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        if (ImGui.InvisibleButton("##tile", size))
            Apply(slot, e);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            if (ImGui.GetIO().KeyShift)
                SearchItem(e);
            else
                CopyName(e);
        }
        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetTooltip(e.Store ? $"{e.Name}\nOnline Store" : e.Name);

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.FrameBg));

        var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(e.Icon)).GetWrapOrEmpty();
        if (shot != null && Plugin.TextureProvider.GetFromFile(shot).TryGetWrap(out var image, out _))
        {
            var scale = Math.Min(size.X / image.Width, size.Y / image.Height);
            var fit = new Vector2(image.Width, image.Height) * scale;
            var at = min + (size - fit) / 2;
            draw.AddImage(image.Handle, at, at + fit);

            var iconSize = new Vector2(Math.Min(IconSize * ImGuiHelpers.GlobalScale, size.X / 3));
            var corner = min + new Vector2(4 * ImGuiHelpers.GlobalScale);
            draw.AddImage(icon.Handle, corner, corner + iconSize);
            DrawDyes(draw, corner + new Vector2(iconSize.X, 0), iconSize.X, e.Dyes);
            if (e.Store)
                DrawStore(draw, corner + iconSize, iconSize.X);
        }
        else
        {
            var iconSize = screenshots ? new Vector2(IconSize * ImGuiHelpers.GlobalScale) : size;
            var at = min + (size - iconSize) / 2;
            draw.AddImage(icon.Handle, at, at + iconSize);
            DrawDyes(draw, at + new Vector2(iconSize.X, 0), iconSize.X, e.Dyes);
            if (e.Store)
                DrawStore(draw, at + iconSize, iconSize.X);
        }

        if (hovered)
            draw.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.ButtonHovered), 0, ImDrawFlags.None, 2);
    }

    private void CopyName(Entry e)
    {
        ImGui.SetClipboardText(e.Name);
        status = $"Copied: {e.Name}";
    }

    // The game's own "Search for Item": opens its list of where the item is kept, HQ and collectable included.
    private unsafe void SearchItem(Entry e)
    {
        var finder = ItemFinderModule.Instance();
        if (finder == null)
            return;

        finder->SearchForItem(e.Id, true);
        status = $"Searching: {e.Name}";
    }

    // Each item put on is a step in the history.
    private void Apply(ApiEquipSlot slot, Entry e)
    {
        try
        {
            // Two-part weapons: Glamourer doesn't fill the off hand over IPC, so it is set from the same item as well.
            ApiEquipSlot[] slots = slot == ApiEquipSlot.MainHand && e.ModelSub != 0 ? [slot, ApiEquipSlot.OffHand] : [slot];
            var before = history.Take(slots);
            List<byte> Dyes(int i) => Plugin.Config.KeepDyes && before != null ? before[i].Stains : [0, 0];

            // Shields and tools go as a custom model id: see IdOf.
            var result = pins.Set(slot, Pins.IdOf(slot, e.Id), Dyes(0));
            // The first slot has changed, so it is a step even if the off hand is refused: back puts the main hand back.
            if (result == GlamourerApiEc.Success)
            {
                if (slots.Length > 1)
                    result = pins.Set(ApiEquipSlot.OffHand, e.Id, Dyes(1));
                if (before != null)
                    history.Add(e.Name, slots, before);
            }
            status = result == GlamourerApiEc.Success ? $"Applied: {e.Name}" : $"Glamourer: {result}";
        }
        catch (IpcNotReadyError)
        {
            status = "Glamourer is not available.";
        }
        catch (Exception ex)
        {
            status = "Something went wrong, see /xllog.";
            Plugin.Log.Warning(ex, "SetItem failed");
        }
    }

    private void Travel(bool forward)
    {
        try
        {
            var name = forward ? history.ForwardName : history.BackName;
            var result = forward ? history.Forward() : history.Back();
            status = result != GlamourerApiEc.Success ? $"Glamourer: {result}" : forward ? $"Redone: {name}" : $"Undone: {name}";
        }
        catch (IpcNotReadyError)
        {
            status = "Glamourer is not available.";
        }
        catch (Exception ex)
        {
            status = "Something went wrong, see /xllog.";
            Plugin.Log.Warning(ex, "SetItem failed");
        }
    }
}
