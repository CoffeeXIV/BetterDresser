using System;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Dresser;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    internal static Config Config { get; private set; } = null!;

    // The object index Glamourer dresses: the local player, or in GPose the player's GPose copy, which is always at 201.
    internal static int Actor => ClientState.IsGPosing ? 201 : 0;

    // The gender the game has for the character, male with no character. A change in Glamourer doesn't show in the game object,
    // and the previews aren't meant to switch on their own.
    internal static bool CharacterFemale => ObjectTable[Actor] is ICharacter c && c.Customize[(int)CustomizeIndex.Gender] == 1;

    private static readonly string[] Commands = ["/bdresser", "/betterdresser"];

    private readonly WindowSystem windowSystem = new("Dresser");
    private readonly MainWindow mainWindow;
    private readonly PacksWindow packsWindow;
    private readonly Pins pins;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Config ?? new Config();
        // Sizes saved before the slider clamped typed values, or edited by hand: a size of 0 breaks the grid.
        Config.PreviewWidth = Math.Clamp(Config.PreviewWidth, Config.MinPreviewWidth, Config.MaxPreviewWidth);
        Config.IconSize = Math.Clamp(Config.IconSize, Config.MinIconSize, Config.MaxIconSize);
        pins = new Pins();
        // One history for both: the catalog's arrows step through gear and dyes alike.
        var history = new History(pins);
        var dyesWindow = new DyesWindow(pins, history);
        packsWindow = new PacksWindow();
        mainWindow = new MainWindow(dyesWindow, packsWindow, pins, history);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(dyesWindow);
        windowSystem.AddWindow(packsWindow);

        foreach (var command in Commands)
        {
            CommandManager.AddHandler(command, new CommandInfo((_, _) => mainWindow.Toggle())
            {
                HelpMessage = "Open the gear catalog.",
            });
        }

        // Keep the window when the game UI is hidden with Scroll Lock, and in GPose.
        PluginInterface.UiBuilder.DisableUserUiHide = true;
        PluginInterface.UiBuilder.DisableGposeUiHide = true;
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        windowSystem.RemoveAllWindows();
        // Unloaded with the catalog open: its gear turns temporary as if it were closed.
        pins.Release();
        mainWindow.Dispose();
        packsWindow.Dispose();
        foreach (var command in Commands)
            CommandManager.RemoveHandler(command);
    }
}
