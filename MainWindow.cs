using System;
using System.Buffers.Binary;
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
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;
using ItemFinderModule = FFXIVClientStructs.FFXIV.Client.UI.Misc.ItemFinderModule;
using UIState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState;

namespace Dresser;

public class MainWindow : Window, IDisposable
{
    private record Patch(int ID, string Version, string ExName);
    // Patch is null for items newer than the patch data. Male and Female: who can wear it.
    private record Entry(uint Id, string Name, uint JobCategory, uint Icon, ulong ModelMain, ulong ModelSub, byte Dyes, bool Store, Patch? Patch,
        bool Male, bool Female);

    // The game icon on preview tiles; icon mode takes Config.IconSize.
    private const float IconSize = 48;
    // The magnifier over a preview with Alt held: its side, and how many times it zooms the preview as shown.
    private const float LensSize = 220, LensZoom = 1.7f;

    private const string MalePack = "male", FemalePack = "female";

    // Previews are in shots/<pack>/<folder>/. Pack null: male or female by the gender shown.
    // Both ring tabs share one folder. Weapon tabs have none: they take every folder of the pack (WeaponFolders).
    // Packs are offered for download in PacksWindow.
    private static readonly (string Label, ApiEquipSlot Slot, string? Pack, string[] Folders)[] Tabs =
    [
        ("Main Hand", ApiEquipSlot.MainHand, "weapons", []),
        ("Off Hand", ApiEquipSlot.OffHand, "weapons", []),
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

    // The revert button, and its step in the history.
    private const string RevertName = "Reset outfit";

    // Every gear slot, in tab order: the main hand goes on before the off hand.
    private static readonly ApiEquipSlot[] Slots = Tabs.Select(t => t.Slot).ToArray();

    // Glamourer's toggles in the row under the search. On is Equipment.<Key>.<Field> in its state.
    private static readonly (MetaFlag Flag, string Key, string Field, FontAwesomeIcon Icon, string Name)[] Toggles =
    [
        (MetaFlag.HatState, "Hat", "Show", FontAwesomeIcon.HatCowboy, "Headgear"),
        (MetaFlag.VisorState, "Visor", "IsToggled", FontAwesomeIcon.Glasses, "Visor"),
        (MetaFlag.EarState, "VieraEars", "Show", FontAwesomeIcon.Deaf, "Viera ears"),
        (MetaFlag.WeaponState, "Weapon", "Show", FontAwesomeIcon.Gun, "Weapon"),
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
    // Preview files on disk per pack and folder: ModelMain -> path.
    private readonly Dictionary<(string Pack, string Folder), Dictionary<ulong, string>> previewFiles = [];
    // Height to width of the previews per pack and folder, from the first one readable.
    private readonly Dictionary<(string Pack, string Folder), float> aspects = [];
    // Weapon tabs' folders for the current job, its own first (WeaponFolders). Cleared on a rescan and a job change.
    private readonly Dictionary<ApiEquipSlot, string[]> weaponFolders = [];
    // Whether a model has a VFX, per model and slot, read as tiles first show them. The game's gimmick parameters, read once.
    private readonly Dictionary<(ulong Model, ApiEquipSlot Slot), bool> vfx = [];
    private byte[]? gimmicks;
    private readonly FileSystemWatcher watcher;
    // When files under shots/ last changed, 0 once rescanned. Set on the watcher's thread and after downloads.
    private long previewsChangedAt;
    private readonly DyesWindow dyesWindow;
    private readonly PacksWindow packsWindow;
    private readonly Pins pins;
    private readonly History history;
    private readonly GetState getState = new(Plugin.PluginInterface);
    private readonly SetMetaState setMetaState = new(Plugin.PluginInterface);
    private readonly RevertState revertState = new(Plugin.PluginInterface);
    private readonly AddDesign addDesign = new(Plugin.PluginInterface);
    private readonly OpenDesign openDesign = new(Plugin.PluginInterface);
    private string presetName = string.Empty;
    // Patch filter options, newest first: each expansion's name, then the versions of its patches that added gear.
    private List<(string Name, bool Expansion)> patchFilters = [];
    // The widest of them, measured again only when the font size changes: the list is fixed once loaded.
    private (float FontSize, float Width) patchNamesWidth;

    private string search = string.Empty;
    private string status = string.Empty;
    private uint cachedJob = uint.MaxValue;
    private HashSet<uint> jobCategories = [];
    private readonly HashSet<uint> owned = [];
    private long ownedAt, armoireAt;
    // Gender picked with the button in the status bar, for that character only: not saved, dropped on switching characters.
    private (ulong Character, bool Female)? picked;
    // Asked about in DrawChoice.
    private (List<Entry> Items, bool Search)? choice;
    private bool openChoice;
    // The tiles of the tab shown, and what they were made of (DrawList).
    private (ApiEquipSlot Slot, uint Job, long OwnedAt, bool HideStore, string Patch, string Search) tilesKey;
    private List<List<Entry>> tiles = [];

    // No wheel scrolling: with previews the list passes the wheel on, so an overflow would scroll the window with it.
    public MainWindow(DyesWindow dyesWindow, PacksWindow packsWindow, Pins pins, History history)
        : base("BetterDresser", ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.dyesWindow = dyesWindow;
        this.packsWindow = packsWindow;
        this.pins = pins;
        this.history = history;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(350, 400), MaximumSize = new Vector2(float.MaxValue) };
        LoadItems();
        LoadPreviews();

        // Any change under shots/ means a rescan: files copied in, over (maybe another shape) or removed, and an overflow (Error).
        Directory.CreateDirectory(PreviewsRoot);
        watcher = new FileSystemWatcher(PreviewsRoot) { IncludeSubdirectories = true };
        watcher.Created += OnPreviewsChanged;
        watcher.Changed += OnPreviewsChanged;
        watcher.Deleted += OnPreviewsChanged;
        watcher.Renamed += OnPreviewsChanged;
        watcher.Error += OnPreviewsChanged;
        watcher.EnableRaisingEvents = true;
        // Downloads and deletes rescan even if the watcher has missed them: under Wine it may not report changes.
        packsWindow.PreviewsChanged += OnPreviewsChanged;
    }

    public void Dispose()
    {
        packsWindow.PreviewsChanged -= OnPreviewsChanged;
        watcher.Dispose();
    }

    // Gear put on while the catalog is open outlasts job changes. The first time, the preview packs are offered.
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

    // The folder keeps its name from before the previews were called so: renamed, the packs already downloaded would be lost.
    internal static string PreviewsRoot => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "shots");

    private void OnPreviewsChanged(object? sender, EventArgs e) => previewsChangedAt = Environment.TickCount64;

    // The layout of the packs: shots/<pack>/<folder>/<ModelMain>.<jpg|png>.
    // About 10-20 ms for three full packs (23 000 files), fine to do in a frame now and then.
    private void LoadPreviews()
    {
        previewsChangedAt = 0;
        previewFiles.Clear();
        aspects.Clear();
        weaponFolders.Clear();
        var root = PreviewsRoot;
        if (!Directory.Exists(root))
            return;

        try
        {
            foreach (var packDir in Directory.EnumerateDirectories(root))
            {
                var pack = Path.GetFileName(packDir);
                foreach (var dir in Directory.EnumerateDirectories(packDir))
                {
                    var files = previewFiles[(pack, Path.GetFileName(dir))] = [];
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
            previewsChangedAt = Environment.TickCount64;
        }
    }

    private bool ShowFemale => picked?.Female ?? Plugin.CharacterFemale;

    internal static string GenderPack(bool female) => female ? FemalePack : MalePack;

    // A folder's previews share one frame: the first readable file gives the tile shape, 1:2 if none until the next scan.
    // Read from the file, not the texture: Dalamud caches textures by path, so a replaced file would keep its old shape.
    private float PreviewAspect(string pack, string folder)
    {
        if (aspects.TryGetValue((pack, folder), out var aspect))
            return aspect;
        aspect = 2;
        if (previewFiles.TryGetValue((pack, folder), out var files))
            foreach (var path in files.Values)
                if (ImageSize(path) is { X: > 0, Y: > 0 } size)
                {
                    aspect = size.Y / size.X;
                    break;
                }
        return aspects[(pack, folder)] = aspect;
    }

    // Parts of an IMC file in its order: armor shares one file for five slots, and so do accessories.
    private static readonly ApiEquipSlot[] ArmorParts =
        [ApiEquipSlot.Head, ApiEquipSlot.Body, ApiEquipSlot.Hands, ApiEquipSlot.Legs, ApiEquipSlot.Feet];
    private static readonly ApiEquipSlot[] AccessoryParts =
        [ApiEquipSlot.Ears, ApiEquipSlot.Neck, ApiEquipSlot.Wrists, ApiEquipSlot.RFinger, ApiEquipSlot.LFinger];

    // A VFX on the model (relic auras, fire): the VfxId of the variant's entry in its IMC file, which holds a variant count,
    // a part mask, then 6 bytes per part per variant, default first. Read lazily: all weapons at once take half a second.
    private bool HasVfx(ApiEquipSlot slot, ulong model)
    {
        if (vfx.TryGetValue((model, slot), out var has))
            return has;

        var set = model & 0xFFFF;
        var weapon = slot is ApiEquipSlot.MainHand or ApiEquipSlot.OffHand;
        var body = model >> 16 & 0xFFFF;
        var armor = Array.IndexOf(ArmorParts, slot);
        var path = weapon ? $"chara/weapon/w{set:D4}/obj/body/b{body:D4}/b{body:D4}.imc"
            : armor >= 0 ? $"chara/equipment/e{set:D4}/e{set:D4}.imc"
            : $"chara/accessory/a{set:D4}/a{set:D4}.imc";
        var variant = (int)(weapon ? model >> 32 & 0xFFFF : model >> 16 & 0xFF);
        var part = weapon ? 0 : armor >= 0 ? armor : Array.IndexOf(AccessoryParts, slot);

        has = false;
        if (Plugin.DataManager.GetFile(path)?.Data is { Length: >= 4 } data)
        {
            var parts = BitOperations.PopCount(BitConverter.ToUInt16(data, 2));
            var offset = 4 + (variant * parts + part) * 6;
            has = variant <= BitConverter.ToUInt16(data, 0) && part < parts && offset + 6 <= data.Length && data[offset + 4] != 0;
        }
        return vfx[(model, slot)] = has;
    }

    // Headgear whose visor can be toggled: bit 0 of its model's entry in gimmickparameter.gmp (Penumbra's Visor/Gimmick edits).
    // Laid out as the EQP: blocks of 160 entries of 8 bytes, only the blocks in use; the first entry is the mask of them.
    private bool HasVisor(ulong model)
    {
        gimmicks ??= Plugin.DataManager.GetFile("chara/xls/equipmentparameter/gimmickparameter.gmp")?.Data ?? [];
        if (gimmicks.Length < 8)
            return false;

        var id = (int)(model & 0xFFFF);
        var block = id / 160;
        var mask = BitConverter.ToUInt64(gimmicks, 0);
        if (id == 0 || block >= 64 || (mask >> block & 1) == 0)
            return false;
        var offset = (BitOperations.PopCount(mask & ((1ul << block) - 1)) * 160 + id % 160) * 8;
        return offset + 8 <= gimmicks.Length && (gimmicks[offset] & 1) != 0;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Width and height from the header of a JPEG or PNG, null if it can't be read. A few small reads, so fine in a frame:
    // it is done once per folder after a scan.
    private static Vector2? ImageSize(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> b = stackalloc byte[24];
            file.ReadExactly(b);
            // PNG: the IHDR chunk comes first, its width and height right after its name.
            if (b[..8].SequenceEqual(PngSignature))
                return new Vector2(BinaryPrimitives.ReadUInt32BigEndian(b[16..]), BinaryPrimitives.ReadUInt32BigEndian(b[20..]));
            if (b[0] != 0xFF || b[1] != 0xD8)
                return null;

            // JPEG: segment by segment up to the start of frame, which has the height and then the width.
            // DHT, JPG and DAC share the range of the start of frame markers.
            file.Position = 2;
            while (true)
            {
                file.ReadExactly(b[..4]);
                var length = BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
                if (b[0] != 0xFF || length < 2)
                    return null;
                if (b[1] is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    file.ReadExactly(b[..5]);
                    return new Vector2(BinaryPrimitives.ReadUInt16BigEndian(b[3..]), BinaryPrimitives.ReadUInt16BigEndian(b[1..]));
                }
                file.Position += length - 2;
            }
        }
        // Cut short, gone or being written: the end of the file comes up as EndOfStreamException, an IOException.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Weapons and primary tools are shot into a folder per job, shared ones (ACN books, shields) into each job's. The job's
    // own folder, with most of its items, goes first: it gives the tile shape and is looked in first. Counted once per job
    // and rescan. No folders: [""], so the tab still has a shape.
    private string[] WeaponFolders(ApiEquipSlot slot, string pack, IEnumerable<Entry> list)
    {
        if (weaponFolders.TryGetValue(slot, out var folders))
            return folders;

        var models = list.Select(e => e.ModelMain).ToHashSet();
        folders = previewFiles.Where(p => p.Key.Pack == pack).OrderByDescending(p => p.Value.Keys.Count(models.Contains))
            .Select(p => p.Key.Folder).ToArray();
        return weaponFolders[slot] = folders.Length > 0 ? folders : [""];
    }

    // Gendered tabs: the pack of the gender given. Of several folders (weapons), the first that has the model.
    private string? PreviewOf(string? pack, string[] folders, ulong model, bool female)
    {
        pack ??= GenderPack(female);
        foreach (var folder in folders)
            if (previewFiles.TryGetValue((pack, folder), out var files) && files.TryGetValue(model, out var path))
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
                // Main hand: a second part only for items that take both hands (two-part weapons). Primary tools have
                // a ModelSub too, but leave the off hand to the secondary tool, and Glamourer has no off-hand item for it.
                var sub = slot == ApiEquipSlot.MainHand && item.EquipSlotCategory.Value.OffHand != -1 ? 0 : item.ModelSub;
                items[slot].Add(new Entry(item.RowId, name, item.ClassJobCategory.RowId, item.Icon, item.ModelMain, sub, item.DyeCount,
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
        weaponFolders.Clear();
        var column = (int)job + 1;
        var sheet = Plugin.DataManager.Excel.GetSheet<RawRow>(null, "ClassJobCategory");
        jobCategories = column < sheet.Columns.Count
            ? sheet.Where(r => r.ReadBoolColumn(column)).Select(r => r.RowId).ToHashSet()
            : [];
    }

    // Bags, armory and equipped gear live; retainers, saddlebags and the dresser from the game's "Search for Item" cache,
    // saved per character once each has been opened. The armoire isn't in it: it is kept in Config (SaveArmoire).
    private unsafe void LoadOwned()
    {
        owned.Clear();
        foreach (var type in Containers)
            foreach (var item in Plugin.GameInventory.GetInventoryItems(type))
                owned.Add(item.BaseItemId);

        if (Plugin.Config.Armoire.TryGetValue(Plugin.PlayerState.ContentId, out var armoire))
            owned.UnionWith(armoire);

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
    private static unsafe void SaveArmoire()
    {
        var character = Plugin.PlayerState.ContentId;
        var cabinet = &UIState.Instance()->Cabinet;
        if (character == 0 || !cabinet->IsCabinetLoaded())
            return;

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

    private void AddOwned(Span<uint> ids)
    {
        foreach (var id in ids)
            owned.Add(BaseItemId(id));
    }

    // Cached ids keep the quality offset: HQ is +1,000,000, collectable +500,000.
    private static uint BaseItemId(uint id) => id % 500_000;

    // A Font Awesome icon centered in a box on whole pixels: across by its ink (Dalamud's icon buttons sit it left of center),
    // down as a line of text (by its ink it looks high beside a field's text).
    private static unsafe void DrawIconCentered(ImDrawListPtr draw, FontAwesomeIcon icon, Vector2 min, Vector2 max, uint color)
    {
        var font = UiBuilder.IconFont;
        var glyph = font.FindGlyph(icon.ToIconChar());
        var at = new Vector2(min.X + max.X - glyph->X0 - glyph->X1, min.Y + max.Y - font.FontSize) / 2;
        draw.AddText(font, font.FontSize, new Vector2(MathF.Round(at.X), MathF.Round(at.Y)), color, icon.ToIconString());
    }

    private static float CheckboxWidth(string label) =>
        ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;

    public override void Draw()
    {
        const string storeToggle = "Hide store";
        const string ownedToggle = "Owned";
        const string toggle = "Previews";
        // A square clear button flush with the search and in the field's colors, so the two read as one field. Nothing to clear:
        // the cross grayed out, no hover. Not Disabled, which would fade its background apart from the field's.
        var clear = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(-clear - CheckboxWidth(storeToggle) - CheckboxWidth(ownedToggle) - CheckboxWidth(toggle)
            - ImGui.GetStyle().ItemSpacing.X * 3);
        ImGui.InputTextWithHint("##search", "Search", ref search, 100);
        ImGui.SameLine(0, 0);
        var empty = search.Length == 0;
        using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.FrameBg))
            .Push(ImGuiCol.ButtonHovered, ImGui.GetColorU32(empty ? ImGuiCol.FrameBg : ImGuiCol.FrameBgHovered))
            .Push(ImGuiCol.ButtonActive, ImGui.GetColorU32(empty ? ImGuiCol.FrameBg : ImGuiCol.FrameBgActive)))
        {
            if (ImGui.Button("##clear", new Vector2(clear)))
                search = string.Empty;
        }
        DrawIconCentered(ImGui.GetWindowDrawList(), FontAwesomeIcon.Times, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
            ImGui.GetColorU32(empty ? ImGuiCol.TextDisabled : ImGuiCol.Text));
        if (!empty && ImGui.IsItemHovered())
            ImGui.SetTooltip("Clear search");
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
            ImGui.SetTooltip("Only items this character has. The armoire item list is synced with BetterDresser if you open the plugin any time after opening the armoire, before logging out.");
        // Once a second, cheap. The armoire is saved with the filter off too: it may go on later, away from the inn.
        // With it on, the list is rebuilt and picks up a retainer or the dresser visited while the window is open.
        if (Environment.TickCount64 - armoireAt > 1000)
        {
            SaveArmoire();
            armoireAt = Environment.TickCount64;
        }
        if (ownedOnly && Environment.TickCount64 - ownedAt > 1000)
        {
            LoadOwned();
            ownedAt = Environment.TickCount64;
        }

        ImGui.SameLine();
        var previews = Plugin.Config.Previews;
        if (ImGui.Checkbox(toggle, ref previews))
        {
            Plugin.Config.Previews = previews;
            Plugin.Config.Save();
        }

        // Rescan once the files have been left alone for a second: copying or unpacking a pack changes thousands of them.
        if (previewsChangedAt != 0 && Environment.TickCount64 - previewsChangedAt > 1000)
            LoadPreviews();

        // A frame tall whatever is on it, so the grid below doesn't jump.
        ImGui.AlignTextToFramePadding();
        var tileSize = previews ? Plugin.Config.PreviewWidth : Plugin.Config.IconSize;
        ImGui.SetNextItemWidth(350 * ImGuiHelpers.GlobalScale);
        // Clamped on Ctrl+click input too: ImGui lets typed values past the bounds otherwise.
        if (ImGui.SliderInt("##size", ref tileSize, previews ? Config.MinPreviewWidth : Config.MinIconSize,
                previews ? Config.MaxPreviewWidth : Config.MaxIconSize, "Image size %d", ImGuiSliderFlags.AlwaysClamp))
        {
            if (previews)
                Plugin.Config.PreviewWidth = tileSize;
            else
                Plugin.Config.IconSize = tileSize;
        }
        // Save once the drag ends, not every frame.
        if (ImGui.IsItemDeactivatedAfterEdit())
            Plugin.Config.Save();
        // Pinned to the right edge, but never over the slider. The magnifier is only on previews.
        const string clicks = "Right click: copy name, Shift+right click: Search for Item";
        var hint = previews ? clicks + ", Alt: zoom" : clicks;
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(hint).X));
        ImGui.TextDisabled(hint);

        DrawControls();

        if (picked is { } p && p.Character != Plugin.PlayerState.ContentId)
            picked = null;
        var female = ShowFemale;

        using (var tabBar = ImRaii.TabBar("##slots"))
        {
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

        DrawChoice();
        DrawHistory();
        DrawStatusBar(female);
    }

    // The row above the status bar: the last action's status on the left; on the right, never over it, the revert button and
    // the history arrows, wider than other icon buttons.
    private void DrawHistory()
    {
        var style = ImGui.GetStyle();
        ImGui.SetCursorPosY(ImGui.GetWindowContentRegionMax().Y - ImGui.GetFrameHeight() * 2 - style.ItemSpacing.Y * 2);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(status);

        const string label = "History:";
        var button = new Vector2(ImGui.GetFrameHeight() * 2.5f, ImGui.GetFrameHeight());
        // As far from the right edge as the patch filter below.
        var width = ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.UndoAlt, RevertName) + ImGui.GetFrameHeight()
            + ImGui.CalcTextSize(label).X + style.ItemSpacing.X + button.X * 2 + style.ItemInnerSpacing.X + ImGui.GetFrameHeight();
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - width));
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.UndoAlt, RevertName))
            Revert();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Put the game's own gear back on. The back arrow puts this outfit on again.");
        ImGui.SameLine(0, ImGui.GetFrameHeight());
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        foreach (var forward in new[] { false, true })
        {
            var name = forward ? history.ForwardName : history.BackName;
            using (ImRaii.Disabled(name == null))
            {
                if (ImGuiComponents.IconButton(forward ? FontAwesomeIcon.ArrowRight : FontAwesomeIcon.ArrowLeft, button))
                    Travel(forward);
            }
            if (name != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(forward ? $"Redo: {name}" : $"Undo: {name}");
            if (!forward)
                ImGui.SameLine(0, style.ItemInnerSpacing.X);
        }
    }

    // The row above the tabs: Glamourer's toggles (lit up when on), the dyes button, and saving a preset on the right.
    // Ears only for Viera, by the race in Glamourer's state: a race it changed doesn't show in the game object's customize.
    private void DrawControls()
    {
        // Shared with the dyes window: one read of Glamourer's state for both.
        pins.RefreshState();
        var state = pins.State;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Toggles:");
        ImGui.SameLine();
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        // Viera is row 8 of the Race sheet.
        var viera = (int?)state?["Customize"]?["Race"]?["Value"] == 8;
        foreach (var (flag, key, field, icon, name) in Toggles)
        {
            if (flag == MetaFlag.EarState && !viera)
                continue;

            var on = (bool?)state?["Equipment"]?[key]?[field];
            using (ImRaii.Disabled(on == null))
            using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), on == true)
                .Push(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled), on == false))
            {
                if (ImGuiComponents.IconButton(icon) && on != null)
                    SetMeta(flag, name, !on.Value);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(on != null ? OnOff(name, on.Value) : pins.StateError.Length > 0 ? $"{name}\n{pins.StateError}" : name);
            ImGui.SameLine(0, spacing);
        }

        ImGui.SameLine(0, ImGui.GetFrameHeight());
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Palette, "Manage dyes"))
            dyesWindow.Toggle();

        const string save = "Save preset to Glamourer";
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(),
            ImGui.GetWindowContentRegionMax().X - ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.Save, save)));
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, save))
        {
            presetName = $"BetterDresser {DateTime.Now:yyyy-MM-dd HH:mm}";
            ImGui.OpenPopup("##preset");
        }
        DrawPresetPopup();
    }

    // The name for the new design, filled in and selected: typing replaces it. Enter saves, a click outside closes.
    private void DrawPresetPopup()
    {
        using var popup = ImRaii.Popup("##preset");
        if (!popup)
            return;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Name:");
        ImGui.SameLine();
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        var enter = ImGui.InputText("##name", ref presetName, 100, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        var name = presetName.Trim();
        ImGui.SameLine();
        using (ImRaii.Disabled(name.Length == 0))
        {
            if ((ImGui.Button("Save") || enter) && name.Length > 0)
            {
                SavePreset(name);
                ImGui.CloseCurrentPopup();
            }
        }
    }

    private static string OnOff(string name, bool on) => $"{name}: {(on ? "on" : "off")}";

    // The bottom row: in preview mode the packs button, the gender switch and the pack notices, then the armoire warning;
    // the patch filter on the right, never over the rest.
    private void DrawStatusBar(bool female)
    {
        ImGui.SetCursorPosY(ImGui.GetWindowContentRegionMax().Y - ImGui.GetFrameHeight());
        ImGui.AlignTextToFramePadding();
        if (Plugin.Config.Previews)
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Download, "Download previews"))
                packsWindow.Toggle();
            // A yellow outline, as the other notices are yellow, while there are no previews on disk at all.
            var none = previewFiles.Values.All(f => f.Count == 0);
            if (none)
                ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(ImGuiColors.DalamudYellow),
                    ImGui.GetStyle().FrameRounding, ImDrawFlags.None, 2);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(none ? "No previews yet: download the packs here." : "Preview packs: download, update, delete.");

            // Two buttons side by side after their label, set apart from the packs button by a wider gap:
            // the gender shown lit up, the other one grayed out.
            ImGui.SameLine(0, ImGui.GetFrameHeight());
            ImGui.TextUnformatted("Change previews:");
            ImGui.SameLine();
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
                DrawPacksNotice($"Preview update: {PacksWindow.FormatSize(packsWindow.UpdateSize)}");
            if (packsWindow.NewPacks() is { Count: > 0 } added)
                DrawPacksNotice($"New preview packs: {string.Join(", ", added)}");
        }

        // Other sources are cached by the game itself; only the armoire can be missing.
        var character = Plugin.PlayerState.ContentId;
        if (Plugin.Config.OwnedOnly && character != 0 && !Plugin.Config.Armoire.ContainsKey(character))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Armoire items are missing: open the armoire at an inn to sync it with BetterDresser.");
            ImGui.SameLine();
        }

        var style = ImGui.GetStyle();
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

    private void DrawPacksNotice(string text)
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, text);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("Click to open the preview packs.");
        }
        if (ImGui.IsItemClicked())
            packsWindow.IsOpen = true;
        ImGui.SameLine();
    }

    private void DrawList(ApiEquipSlot slot, string? pack, string[] folders, bool female)
    {
        IEnumerable<Entry> list = items[slot];
        if (slot is ApiEquipSlot.MainHand or ApiEquipSlot.OffHand)
        {
            UpdateJobCategories();
            list = list.Where(e => jobCategories.Contains(e.JobCategory));
            folders = WeaponFolders(slot, pack!, list);
        }

        if (Plugin.Config.OwnedOnly)
            list = list.Where(e => owned.Contains(e.Id));

        if (Plugin.Config.HideStore)
            list = list.Where(e => !e.Store);

        var previews = Plugin.Config.Previews;

        // A patch version or an expansion name: they never collide.
        var patch = Plugin.Config.Patch;
        if (patch.Length > 0)
            list = list.Where(e => e.Patch is { } p && (p.Version == patch || p.ExName == patch));

        if (search.Length > 0)
            list = list.Where(e => e.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        // Leave room for the history row and the status bar at the bottom of the window, with a small gap above them.
        // With previews the wheel is handled below: ImGui's own step is a few lines of text, whatever the size of the tiles.
        using var child = ImRaii.Child("##list", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() * 2 - ImGui.GetStyle().ItemSpacing.Y * 2),
            false, previews ? ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None);
        if (!child)
            return;

        // Every tile in a tab is the same size, so the list can skip the rows out of view.
        var shapePack = pack ?? GenderPack(female);
        var size = previews
            ? new Vector2(1, PreviewAspect(shapePack, folders[0])) * Plugin.Config.PreviewWidth * ImGuiHelpers.GlobalScale
            : new Vector2(Plugin.Config.IconSize * ImGuiHelpers.GlobalScale);
        // The same gap across and down: the style's vertical item spacing is smaller than the horizontal one.
        var gap = ImGui.GetStyle().ItemSpacing.X;
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(gap));
        var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + gap) / (size.X + gap)));

        // With previews a notch of the wheel scrolls a quarter of a row of tiles, so the speed keeps up with the tile size.
        var wheel = ImGui.GetIO().MouseWheel;
        if (previews && wheel != 0 && ImGui.IsWindowHovered())
            ImGui.SetScrollY(ImGui.GetScrollY() - wheel * (size.Y + gap) / 4);

        // One tile per model, where its first item is in the list, showing the first item the gender shown can wear (else the
        // first, faded, with the other gender's shot). Regrouped only when the key changes, so a new filter goes into it too.
        var key = (slot, cachedJob, Plugin.Config.OwnedOnly ? ownedAt : 0, Plugin.Config.HideStore, Plugin.Config.Patch, search);
        if (key != tilesKey)
        {
            tilesKey = key;
            tiles = list.GroupBy(e => (e.ModelMain, e.ModelSub)).Select(g => g.ToList()).ToList();
        }
        ImGuiClip.ClippedDraw(tiles, t =>
        {
            var fit = t.Find(e => female ? e.Female : e.Male);
            DrawTile(slot, t, fit ?? t[0], size, previews,
                previews ? PreviewOf(pack, folders, t[0].ModelMain, fit != null ? female : !female) : null, previews && fit == null);
        }, columns, size.Y + gap);
    }

    // Asked on a right click on a tile of several items: the one to copy the name of, or with Shift to search for.
    // Here rather than in the list, so it stays open when its tile scrolls out of view.
    private void DrawChoice()
    {
        if (openChoice)
        {
            ImGui.OpenPopup("##choice");
            openChoice = false;
        }

        using var popup = ImRaii.Popup("##choice");
        if (!popup || choice is not { } c)
            return;

        ImGui.TextDisabled(c.Search ? "Search for Item:" : "Copy name:");
        foreach (var e in c.Items)
        {
            using var id = ImRaii.PushId((int)e.Id);
            if (!ImGui.Selectable(e.Name))
                continue;

            if (c.Search)
                SearchItem(e);
            else
                CopyName(e);
        }
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

    private static void DrawStore(ImDrawListPtr draw, Vector2 bottomRight, float icon) =>
        DrawMark(draw, FontAwesomeIcon.DollarSign, bottomRight, true, icon, ImGui.GetColorU32(ImGuiColors.DalamudYellow));

    // Items for one gender only.
    private static void DrawGender(ImDrawListPtr draw, Vector2 topLeft, float icon, bool female) =>
        DrawMark(draw, female ? FontAwesomeIcon.Venus : FontAwesomeIcon.Mars, topLeft, false, icon, 0xFFFFFFFF);

    // A Font Awesome mark with a black outline in a corner of the icon: the top left or the bottom right one.
    // Under a third of the icon tall, so it stays clear of the two dye circles down the right edge.
    private static unsafe void DrawMark(ImDrawListPtr draw, FontAwesomeIcon mark, Vector2 corner, bool bottomRight, float icon, uint color)
    {
        var font = UiBuilder.IconFont;
        var size = icon / 3.5f;
        // Same width as the dye circles' outline.
        var outline = Math.Max(1, icon / 32);
        // Glyph bounds are given at the font's own size: the ink itself, outline included, goes into the corner.
        var glyph = font.FindGlyph(mark.ToIconChar());
        var scale = size / font.FontSize;
        var at = bottomRight
            ? corner - new Vector2(glyph->X1, glyph->Y1) * scale - new Vector2(outline)
            : corner - new Vector2(glyph->X0, glyph->Y0) * scale + new Vector2(outline);

        DrawOutlined(draw, font, size, at, outline, color, mark.ToIconString());
    }

    // Text with a black outline: drawn black a little off in eight directions first, so it shows on any picture.
    private static void DrawOutlined(ImDrawListPtr draw, ImFontPtr font, float size, Vector2 at, float outline, uint color, string text)
    {
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    draw.AddText(font, size, at + new Vector2(dx, dy) * outline, 0xFF000000, text);
        draw.AddText(font, size, at, color, text);
    }

    // The items of one model as a gray tile showing e: its preview fitted in with the game icon top left, or without one
    // (missing, loading, icon mode) the icon in the middle, covering the tile in icon mode. A left click puts e on.
    // Faded: the gender shown can't wear any of the items.
    private void DrawTile(ApiEquipSlot slot, List<Entry> items, Entry e, Vector2 size, bool previews, string? preview, bool faded)
    {
        using var id = ImRaii.PushId((int)e.Id);
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        if (ImGui.InvisibleButton("##tile", size))
            Apply(slot, e);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            var search = ImGui.GetIO().KeyShift;
            if (items.Count > 1)
            {
                choice = (items, search);
                openChoice = true;
            }
            else if (search)
                SearchItem(e);
            else
                CopyName(e);
        }
        var hovered = ImGui.IsItemHovered();
        // Only when every item of the tile is from the store: otherwise the look can be had in the game.
        var store = items.All(x => x.Store);
        // The gender every item of the tile is locked to: true female, false male, null if the look is for both.
        bool? gender = !items.Any(x => x.Male) ? true : !items.Any(x => x.Female) ? false : null;
        // Alt over a preview shows the magnifier instead of the tooltip, which would cover it.
        var lens = false;

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.FrameBg));

        var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(e.Icon)).GetWrapOrEmpty();
        Vector2 corner, iconSize;
        if (preview != null && Plugin.TextureProvider.GetFromFile(preview).TryGetWrap(out var image, out _))
        {
            var scale = Math.Min(size.X / image.Width, size.Y / image.Height);
            var fit = new Vector2(image.Width, image.Height) * scale;
            var at = min + (size - fit) / 2;
            draw.AddImage(image.Handle, at, at + fit);
            lens = hovered && ImGui.GetIO().KeyAlt;
            if (lens)
                DrawLens(image.Handle, at, fit);

            iconSize = new Vector2(Math.Min(IconSize * ImGuiHelpers.GlobalScale, size.X / 3));
            corner = min + new Vector2(4 * ImGuiHelpers.GlobalScale);
        }
        else
        {
            iconSize = previews ? new Vector2(IconSize * ImGuiHelpers.GlobalScale) : size;
            corner = min + (size - iconSize) / 2;
        }
        draw.AddImage(icon.Handle, corner, corner + iconSize);
        DrawDyes(draw, corner + new Vector2(iconSize.X, 0), iconSize.X, e.Dyes);
        if (store)
            DrawStore(draw, corner + iconSize, iconSize.X);
        if (gender is { } f)
            DrawGender(draw, corner, iconSize.X, f);

        if (previews)
            DrawModelMarks(draw, new Vector2(max.X, min.Y), HasVfx(slot, e.ModelMain), slot == ApiEquipSlot.Head && HasVisor(e.ModelMain));

        // Half sunk into the window background, the icon and the marks along with the preview.
        if (faded)
            draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.WindowBg, 0.6f));

        if (hovered)
        {
            if (!lens)
            {
                var names = string.Join("\n", items.Select(x => x.Store ? $"{x.Name} (Online Store)" : x.Name));
                ImGui.SetTooltip(gender switch { true => $"{names}\nFemale only", false => $"{names}\nMale only", null => names });
            }
            draw.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.ButtonHovered), 0, ImDrawFlags.None, 2);
        }
    }

    // What the model does beyond its look, in the top right corner of a preview tile, right to left: a VFX, then a visor
    // to toggle. Gray marks with a black outline: plain gray was lost on the gray backgrounds of the previews.
    private static unsafe void DrawModelMarks(ImDrawListPtr draw, Vector2 topRight, bool vfx, bool visor)
    {
        var font = UiBuilder.IconFont;
        var color = ImGui.GetColorU32(ImGuiColors.DalamudGrey);
        // A whole pixel thin, and the marks on whole pixels: half pixels blur the glyph and smear the outline.
        var outline = Math.Max(1, MathF.Round(ImGuiHelpers.GlobalScale));
        var edge = 8 * ImGuiHelpers.GlobalScale;
        var gap = 4 * ImGuiHelpers.GlobalScale;
        var right = topRight.X - edge;
        foreach (var mark in new[] { vfx ? FontAwesomeIcon.WandMagicSparkles : 0, visor ? FontAwesomeIcon.Glasses : 0 })
        {
            if (mark == 0)
                continue;

            // Glyph bounds, so the ink itself sits off the edges.
            var glyph = font.FindGlyph(mark.ToIconChar());
            var at = new Vector2(MathF.Round(right - glyph->X1), MathF.Round(topRight.Y + edge - glyph->Y0));
            DrawOutlined(draw, font, font.FontSize, at, outline, color, mark.ToIconString());
            right = at.X + glyph->X0 - gap;
        }
    }

    // A square of the preview around the mouse, zoomed in, over every window. Never bigger than the zoomed preview,
    // and kept inside it: by the edges the part shown stops following the mouse rather than run off the picture.
    private static void DrawLens(ImTextureID texture, Vector2 at, Vector2 fit)
    {
        var size = Vector2.Min(new Vector2(LensSize * ImGuiHelpers.GlobalScale), fit * LensZoom);
        var mouse = ImGui.GetMousePos();
        // The part of the preview in the lens, in texture coordinates.
        var span = size / (fit * LensZoom);
        var center = Vector2.Clamp((mouse - at) / fit, span / 2, Vector2.One - span / 2);
        var min = mouse - size / 2;
        var draw = ImGui.GetForegroundDrawList();
        draw.AddImage(texture, min, min + size, center - span / 2, center + span / 2);
        draw.AddRect(min, min + size, ImGui.GetColorU32(ImGuiCol.Border), 0, ImDrawFlags.None, 2);
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
            List<byte> Dyes(int i) => Plugin.Config.KeepDyesOnGearChange && before != null ? before[i].Stains : [0, 0];

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
        catch (Exception ex)
        {
            status = Pins.ErrorText(ex, "SetItem failed");
        }
    }

    // Once, as Glamourer's own toggles: it keeps the value until the same setting is changed in the game.
    // Not pinned like the gear: Glamourer lets the game override it only when the game's own value changes. Not in the history.
    private void SetMeta(MetaFlag flag, string name, bool on)
    {
        try
        {
            var result = setMetaState.Invoke(Plugin.Actor, flag, on, 0, ApplyFlag.Once);
            status = result == GlamourerApiEc.Success ? OnOff(name, on) : $"Glamourer: {result}";
        }
        catch (Exception ex)
        {
            status = Pins.ErrorText(ex, "SetMetaState failed");
        }

        pins.ReadStateSoon();
    }

    // The character as it looks now becomes a new Glamourer design, opened in Glamourer. Gear only: the appearance is saved
    // in it, but not applied, so the preset can go on any character. Glamourer keeps the apply flags it is given.
    private void SavePreset(string name)
    {
        try
        {
            var (result, data) = getState.Invoke(Plugin.Actor);
            if (result != GlamourerApiEc.Success || data == null)
            {
                status = $"Glamourer: {result}";
                return;
            }

            foreach (var group in new[] { "Customize", "Parameters" })
                if (data[group] is JObject values)
                    foreach (var property in values.Properties())
                        if (property.Value is JObject entry)
                            entry["Apply"] = false;

            result = addDesign.Invoke(data.ToString(), name, out var id);
            if (result != GlamourerApiEc.Success)
            {
                status = $"Glamourer: {result}";
                return;
            }

            // The design is saved by now: failing to open it mustn't read as a failed save, or the user saves it again.
            try
            {
                openDesign.Invoke(id);
                status = $"Saved: {name}";
            }
            catch (Exception ex)
            {
                status = $"Saved: {name} (couldn't open Glamourer)";
                Plugin.Log.Warning(ex, "OpenDesign failed");
            }
        }
        catch (Exception ex)
        {
            status = Pins.ErrorText(ex, "Saving a preset failed");
        }
    }

    // The gear as the game has it, the appearance left alone. One step in the history for every slot: back puts the outfit on again.
    private void Revert()
    {
        try
        {
            var before = history.Take(Slots);
            var result = revertState.Invoke(Plugin.Actor, 0, ApplyFlag.Equipment);
            if (result == GlamourerApiEc.Success && before != null)
                history.Add(RevertName, Slots, before);
            status = result switch
            {
                GlamourerApiEc.Success => "Outfit reset",
                GlamourerApiEc.NothingDone => "Nothing to reset",
                _ => $"Glamourer: {result}",
            };
        }
        catch (Exception ex)
        {
            status = Pins.ErrorText(ex, "RevertState failed");
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
        catch (Exception ex)
        {
            status = Pins.ErrorText(ex, "SetItem failed");
        }
    }
}
