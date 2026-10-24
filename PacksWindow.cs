using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Dresser;

// Preview packs from GitHub releases built by publish.cs: each pack in full (<pack>.zip) and a delta since the release
// before (<pack>-delta.zip). Unpacked into shots/<pack>/, the release tag in .version.
public sealed class PacksWindow : Window, IDisposable
{
    // Digest: GitHub's checksum of the upload, "sha256:<hex>".
    private record Asset(string Name, long Size, string BrowserDownloadUrl, string? Digest);
    private record Release(string TagName, bool Prerelease, DateTime PublishedAt, List<Asset> Assets);

    // Apart from the plugin's repository. Same as in publish.cs.
    private const string Repo = "CoffeeXIV/BetterDresser-Packs";

    // The packs offered, in this order, if the newest release has them.
    private static readonly (string Pack, string Label)[] Packs =
    [
        ("male", "Male clothes"),
        ("female", "Female clothes"),
        ("accessories", "Accessories"),
        ("weapons", "Weapons"),
    ];

    // An empty zip: the delta of a pack that hasn't changed.
    private const long EmptyZip = 22;

    // HttpClient's timeout ends with the headers: a download is given up after this long without data.
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);

    // How long the release list is reused on reopening.
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    // The only files unpacked: <folder>/<ModelMain>.<jpg|png>.
    private static readonly Regex Preview = new("^[a-z0-9]+/[0-9]+\\.(jpg|png)$", RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly HttpClient http = new() { DefaultRequestHeaders = { { "User-Agent", "BetterDresser" } } };
    private readonly CancellationTokenSource cts = new();
    // The release list or a job, never both.
    private Task task = Task.CompletedTask;

    // Set in the background, read while drawing: collections are replaced whole. Newest first by publish date: GitHub
    // promises no order, and created_at is the tagged commit's. Pre-releases only in test builds.
    private List<Release> releases = [];
    // Installed packs: pack -> release tag.
    private Dictionary<string, string> installed = [];
    private bool loading;
    private long listedAt;
    private string? error;
    private bool running;
    // Per pack while downloading, then done or failed.
    private readonly ConcurrentDictionary<string, string> progress = [];

    // Ticked packs; null until the defaults are set.
    private HashSet<string>? selected;

    // The usual border color, given back to the contents.
    private Vector4 border;

    public PacksWindow() : base("BetterDresser Previews", ImGuiWindowFlags.AlwaysAutoResize)
    {
        Refresh();
    }

    // Total size of the installed packs' updates, 0 if none.
    public long UpdateSize { get; private set; }

    // Raised from a background thread after a download or delete.
    public event EventHandler? PreviewsChanged;

    public void Dispose()
    {
        cts.Cancel();
        // Wait for the job to stop: a reloaded plugin could start on the same folders.
        if (!task.Wait(TimeSpan.FromSeconds(10)))
            Plugin.Log.Warning("A preview download is still running as the plugin unloads");
        http.Dispose();
    }

    // Builds that see pre-releases, which are releases under test.
    private static bool Testing => Plugin.PluginInterface.IsDev || Plugin.PluginInterface.IsTesting;

    // Outside shots/, so Dresser never sees half a pack.
    private static string Staging => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "download");

    internal static string FormatSize(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):F0} MB" : $"{Math.Max(1, bytes >> 10)} KB";

    public override void OnOpen()
    {
        if (running || loading)
            return;
        progress.Clear();
        // Spares GitHub's hourly limit.
        if (error == null && listedAt > 0 && Environment.TickCount64 - listedAt < Fresh.TotalMilliseconds)
        {
            LoadInstalled();
            selected = null;
            return;
        }
        Refresh();
    }

    // A thick border in the title's color. Popped in PostDraw, not Draw: Begin draws it, and Dalamud pushes title colors
    // in between. Opens centered.
    public override void PreDraw()
    {
        var colors = ImGui.GetStyle().Colors;
        border = colors[(int)ImGuiCol.Border];
        ImGui.PushStyleColor(ImGuiCol.Border, colors[(int)ImGuiCol.TitleBgActive]);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 3 * ImGuiHelpers.GlobalScale);

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size / 2, ImGuiCond.Appearing, new Vector2(0.5f));
    }

    private void Refresh()
    {
        loading = true;
        error = null;
        task = Task.Run(async () =>
        {
            try
            {
                using var response = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=100", cts.Token);
                // Without a login: 60 requests an hour per IP.
                if (!response.IsSuccessStatusCode
                    && response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.First() == "0"
                    && response.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.First(), out var at))
                {
                    var time = DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime();
                    error = $"GitHub's limit of 60 checks an hour is used up on this network.\nTry again after {time:HH:mm}.";
                    Plugin.Log.Warning($"GitHub's rate limit is used up until {time}");
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    var list = await response.Content.ReadFromJsonAsync<List<Release>>(Json, cts.Token);
                    releases = list!.Where(r => Testing || !r.Prerelease).OrderByDescending(r => r.PublishedAt).ToList();
                    listedAt = Environment.TickCount64;
                }
            }
            catch (Exception ex)
            {
                error = $"Can't reach GitHub: {ex.Message}";
                Plugin.Log.Warning(ex, "Can't list the preview releases");
            }
            LoadInstalled();
            selected = null;
            loading = false;
        });
    }

    // Installed tags and their update size. Never throws: callers clear their flags after it.
    private void LoadInstalled()
    {
        var tags = new Dictionary<string, string>();
        foreach (var (pack, _) in Packs)
        {
            var file = Path.Combine(MainWindow.PreviewsRoot, pack, ".version");
            try
            {
                if (File.Exists(file))
                    tags[pack] = File.ReadAllText(file).Trim();
            }
            // Taken as not installed.
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"Can't read {file}");
            }
        }
        installed = tags;
        // Empty deltas aren't worth a notice.
        UpdateSize = tags.Keys.Where(Offered).Sum(p => PlanFor(p).Where(d => d.Asset.Size > EmptyZip).Sum(d => d.Asset.Size));
    }

    private bool Offered(string pack) => releases.Count > 0 && releases[0].Assets.Any(a => a.Name == $"{pack}.zip");

    // Offered, not installed, and not listed here before. The first list ever only sets what's known.
    internal List<string> NewPacks()
    {
        var offered = Packs.Where(p => Offered(p.Pack)).ToList();
        if (offered.Count == 0)
            return [];

        if (Plugin.Config.KnownPacks == null)
        {
            Plugin.Config.KnownPacks = offered.Select(p => p.Pack).ToHashSet();
            Plugin.Config.Save();
        }
        return offered.Where(p => !installed.ContainsKey(p.Pack) && !Plugin.Config.KnownPacks.Contains(p.Pack)).Select(p => p.Label).ToList();
    }

    // Up to the newest release: the deltas since the installed one, oldest first, or the full pack if a delta is missing
    // or they weigh as much as it.
    private List<(string Tag, Asset Asset)> PlanFor(string pack)
    {
        var index = installed.TryGetValue(pack, out var tag) ? releases.FindIndex(r => r.TagName == tag) : -1;
        if (index == 0)
            return [];
        var full = releases[0].Assets.First(a => a.Name == $"{pack}.zip");
        if (index > 0)
        {
            var deltas = releases.Take(index).Reverse()
                .Select(r => (r.TagName, Asset: r.Assets.FirstOrDefault(a => a.Name == $"{pack}-delta.zip"))).ToList();
            if (deltas.All(d => d.Asset != null) && deltas.Sum(d => d.Asset!.Size) < full.Size)
                return deltas.Select(d => (d.TagName, d.Asset!)).ToList();
        }
        return [(releases[0].TagName, full)];
    }

    // Ticked at first: the character's gender, accessories, weapons, updates.
    private bool Suggested(string pack) => installed.ContainsKey(pack)
        ? PlanFor(pack).Count > 0
        : pack is "accessories" or "weapons" || pack == MainWindow.GenderPack(Plugin.CharacterFemale);

    // Called even when the window is collapsed.
    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    public override void Draw()
    {
        // The thick border is the window's only.
        using var color = ImRaii.PushColor(ImGuiCol.Border, border);
        ImGui.TextUnformatted("Pictures of the gear for the Previews mode, downloaded from GitHub.");
        ImGui.TextDisabled("Game patches bring updates: the status bar of the main window tells when.");
        ImGui.Separator();

        if (loading)
        {
            ImGui.TextUnformatted("Checking GitHub...");
            return;
        }
        if (error != null)
        {
            ImGui.TextUnformatted(error);
            if (ImGui.Button("Retry"))
                Refresh();
            return;
        }

        var offered = Packs.Where(p => Offered(p.Pack)).ToList();
        if (offered.Count == 0)
        {
            ImGui.TextUnformatted("No packs published yet.");
            return;
        }

        // Listed here: no longer new.
        var known = Plugin.Config.KnownPacks ??= [];
        var count = known.Count;
        known.UnionWith(offered.Select(p => p.Pack));
        if (known.Count != count)
            Plugin.Config.Save();

        selected ??= offered.Select(p => p.Pack).Where(Suggested).ToHashSet();
        using (var table = ImRaii.Table("##packs", 4, ImGuiTableFlags.SizingFixedFit))
        {
            if (table)
                foreach (var (pack, label) in offered)
                    DrawPack(pack, label);
        }

        ImGui.Separator();
        var todo = offered.Where(p => selected.Contains(p.Pack)).Select(p => (p.Pack, Plan: PlanFor(p.Pack))).Where(x => x.Plan.Count > 0).ToList();
        var size = todo.Sum(x => x.Plan.Sum(d => d.Asset.Size));
        using (ImRaii.Disabled(running || todo.Count == 0))
        {
            if (ImGui.Button(todo.Count > 0 ? $"Download {FormatSize(size)}###download" : "Download###download"))
                Download(todo);
        }
        ImGui.SameLine();
        if (ImGui.Button("Close"))
            IsOpen = false;
    }

    private void DrawPack(string pack, string label)
    {
        using var id = ImRaii.PushId(pack);
        var plan = PlanFor(pack);
        var tag = installed.GetValueOrDefault(pack);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var tick = plan.Count == 0 || selected!.Contains(pack);
        using (ImRaii.Disabled(running || plan.Count == 0))
        {
            if (ImGui.Checkbox(label, ref tick))
            {
                if (tick)
                    selected!.Add(pack);
                else
                    selected!.Remove(pack);
            }
        }

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(progress.TryGetValue(pack, out var state) ? state
            : tag == null ? "Not installed"
            : plan.Count == 0 ? Version(tag)
            : $"{Version(tag)}, update to {Version(plan[^1].Tag)}");

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        if (plan.Count > 0)
            ImGui.TextUnformatted(FormatSize(plan.Sum(d => d.Asset.Size)));

        ImGui.TableNextColumn();
        if (tag == null)
            return;
        using (ImRaii.Disabled(running))
        {
            if (ImGui.Button("Delete") && ImGui.GetIO().KeyCtrl)
                Delete(pack);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl and click to delete.");
    }

    private string Version(string tag) => releases.Any(r => r.TagName == tag && r.Prerelease) ? $"{tag} (test)" : tag;

    // One job at a time, in the background. After it, failed or not: the download folder is cleared and previews rescanned.
    private void Run(Func<Task> job)
    {
        running = true;
        task = Task.Run(async () =>
        {
            try
            {
                await job();
            }
            finally
            {
                try
                {
                    if (Directory.Exists(Staging))
                        Directory.Delete(Staging, true);
                }
                // A file in use: cleared after the next job.
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, $"Can't clear {Staging}");
                }
                LoadInstalled();
                PreviewsChanged?.Invoke(this, EventArgs.Empty);
                running = false;
            }
        });
    }

    private void Download(List<(string Pack, List<(string Tag, Asset Asset)> Plan)> todo) => Run(async () =>
    {
        foreach (var (pack, plan) in todo)
        {
            try
            {
                await Install(pack, plan);
                progress[pack] = "Done";
            }
            catch (Exception ex)
            {
                progress[pack] = "Failed, see /xllog";
                Plugin.Log.Warning(ex, $"Can't install the {pack} previews");
            }
        }
    });

    private void Delete(string pack) => Run(() =>
    {
        try
        {
            MoveAside(pack);
            progress.TryRemove(pack, out _);
        }
        catch (Exception ex)
        {
            progress[pack] = "Failed, see /xllog";
            Plugin.Log.Warning(ex, $"Can't delete the {pack} previews");
        }
        return Task.CompletedTask;
    });

    // A full pack is unpacked aside and swapped in whole, deltas over the installed pack. The tag after each step,
    // so an interrupted update resumes.
    private async Task Install(string pack, List<(string Tag, Asset Asset)> plan)
    {
        var dir = Path.Combine(MainWindow.PreviewsRoot, pack);
        Directory.CreateDirectory(Staging);
        // It may have been removed by hand.
        Directory.CreateDirectory(MainWindow.PreviewsRoot);
        for (var i = 0; i < plan.Count; i++)
        {
            var (tag, asset) = plan[i];
            var step = plan.Count > 1 ? $" {i + 1}/{plan.Count}" : string.Empty;
            var zip = Path.Combine(Staging, asset.Name);
            await Fetch(asset, zip, pack, $"Downloading{step}");

            progress[pack] = $"Unpacking{step}";
            if (asset.Name == $"{pack}.zip")
            {
                var temp = Path.Combine(Staging, pack);
                if (Directory.Exists(temp))
                    Directory.Delete(temp, true);
                Extract(zip, temp);
                await File.WriteAllTextAsync(Path.Combine(temp, ".version"), tag, cts.Token);
                // The old pack goes back if the new one can't go in.
                var old = MoveAside(pack);
                try
                {
                    Directory.Move(temp, dir);
                }
                catch when (old != null)
                {
                    Directory.Move(old, dir);
                    throw;
                }
            }
            else
            {
                Extract(zip, dir);
                await File.WriteAllTextAsync(Path.Combine(dir, ".version"), tag, cts.Token);
            }
            File.Delete(zip);
        }
    }

    // Out of shots/ in one rename, so a file in use can't leave half a pack. Null if it isn't there.
    private static string? MoveAside(string pack)
    {
        var dir = Path.Combine(MainWindow.PreviewsRoot, pack);
        if (!Directory.Exists(dir))
            return null;
        var old = Path.Combine(Staging, $"{pack}.old");
        if (Directory.Exists(old))
            Directory.Delete(old, true);
        Directory.CreateDirectory(Staging);
        Directory.Move(dir, old);
        return old;
    }

    // Only previews, and no more than the zip weighs (they're stored uncompressed): a bad zip can't fill the disk.
    private void Extract(string zip, string dir)
    {
        using var archive = ZipFile.OpenRead(zip);
        var left = new FileInfo(zip).Length;
        foreach (var entry in archive.Entries)
        {
            cts.Token.ThrowIfCancellationRequested();
            if (!Preview.IsMatch(entry.FullName))
                continue;
            // .NET never writes past an entry's length.
            left -= entry.Length;
            if (left < 0)
                throw new InvalidDataException($"{Path.GetFileName(zip)} unpacks to more than it weighs.");
            var path = Path.Combine(dir, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, true);
        }
    }

    private async Task Fetch(Asset asset, string path, string pack, string label)
    {
        using var response = await http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cts.Token);
        await using var file = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        long done = 0;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        while (true)
        {
            stall.CancelAfter(Stall);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, stall.Token);
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
                throw new TimeoutException($"No data from GitHub for {Stall.TotalSeconds:F0} seconds.");
            }
            if (read == 0)
                break;
            await file.WriteAsync(buffer.AsMemory(0, read), cts.Token);
            hash.AppendData(buffer, 0, read);
            done += read;
            progress[pack] = $"{label}: {100 * done / Math.Max(1, asset.Size)}%";
        }

        // Older uploads may have no digest.
        if (asset.Digest != null && asset.Digest != $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}")
            throw new InvalidDataException($"{asset.Name} doesn't match GitHub's checksum.");
    }
}
