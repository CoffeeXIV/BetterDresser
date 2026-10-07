using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Dresser;

// Screenshot packs from GitHub releases of the zips that publish.cs builds. Every release has each pack in full (<pack>.zip) and
// a delta with what changed since the release before (<pack>-delta.zip). A pack is unpacked into shots/<pack>/,
// with its release tag in shots/<pack>/.version.
public sealed class PacksWindow : Window, IDisposable
{
    // Digest: GitHub's checksum of the upload, "sha256:<hex>".
    private record Asset(string Name, long Size, string BrowserDownloadUrl, string? Digest);
    private record Release(string TagName, bool Prerelease, DateTime PublishedAt, List<Asset> Assets);

    // Packs have a repository of their own, apart from the plugin's code and releases. The same as in publish.cs.
    private const string Repo = "CoffeeXIV/BetterDresser-Packs";

    // The packs offered, in this order, if the newest release has them.
    private static readonly (string Pack, string Label)[] Packs =
    [
        ("male", "Male clothes"),
        ("female", "Female clothes"),
        ("accessories", "Accessories"),
        ("weapons", "Weapons"),
    ];

    // A zip with nothing in it is only its 22-byte end record: publish.cs puts out such a delta for a pack that hasn't changed.
    private const long EmptyZip = 22;

    // HttpClient's timeout ends once the headers are in: a download that stops midway is given up after this long without data.
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);

    // How long a list of releases is reused when the window is opened again.
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    // The only files unpacked: shots as publish.cs packs them, <folder>/<ModelMain>.<jpg|png>.
    private static readonly Regex Shot = new("^[a-z0-9]+/[0-9]+\\.(jpg|png)$", RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly HttpClient http = new() { DefaultRequestHeaders = { { "User-Agent", "BetterDresser" } } };
    private readonly CancellationTokenSource cts = new();
    // The work in the background: the list of releases or a job, never both at once.
    private Task task = Task.CompletedTask;

    // Set by background tasks, read while drawing: the collections are replaced whole, never changed in place.
    // Newest first by publish date: GitHub doesn't promise an order, and its created_at is the date of the tagged commit.
    // Drafts aren't listed without a login; pre-releases only in test builds.
    private List<Release> releases = [];
    // Installed packs: pack -> release tag.
    private Dictionary<string, string> installed = [];
    private bool loading;
    // When the list last came from GitHub, 0 before that.
    private long listedAt;
    private string? error;
    private bool running;
    // Per pack while downloading, then done or failed.
    private readonly ConcurrentDictionary<string, string> progress = [];

    // Packs ticked for download; null until set to the defaults once the releases are in.
    private HashSet<string>? selected;

    public PacksWindow() : base("BetterDresser Screenshots", ImGuiWindowFlags.AlwaysAutoResize)
    {
        Refresh();
    }

    // What the updates of installed packs weigh in total, 0 if there are none. Shown in the main window's status bar.
    public long UpdateSize { get; private set; }

    // Raised after a download or a delete has changed the files under shots/, from a background thread.
    public event EventHandler? ShotsChanged;

    public void Dispose()
    {
        cts.Cancel();
        // A job stops at the next file it unpacks. It is let finish with shots/ first: a reloaded plugin could start on the same folders.
        if (!task.Wait(TimeSpan.FromSeconds(10)))
            Plugin.Log.Warning("A screenshot download is still running as the plugin unloads");
        http.Dispose();
    }

    // Releases go out as pre-releases to be tested before they're opened to everyone: only dev and testing builds see them.
    private static bool Testing => Plugin.PluginInterface.IsDev || Plugin.PluginInterface.IsTesting;

    // Downloads and unpacking happen here first, outside shots/, so Dresser never sees half a pack.
    private static string Staging => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "download");

    internal static string FormatSize(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):F0} MB" : $"{Math.Max(1, bytes >> 10)} KB";

    public override void OnOpen()
    {
        if (running || loading)
            return;
        progress.Clear();
        // Spares GitHub's hourly limit (see Refresh): a list fetched a few minutes ago does for reopening the window.
        if (error == null && listedAt > 0 && Environment.TickCount64 - listedAt < Fresh.TotalMilliseconds)
        {
            LoadInstalled();
            selected = null;
            return;
        }
        Refresh();
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
                // Without a login GitHub answers an IP address 60 times an hour: a VPN or a shared network can use that up.
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
                Plugin.Log.Warning(ex, "Can't list the screenshot releases");
            }
            LoadInstalled();
            selected = null;
            loading = false;
        });
    }

    // Reads the installed tags and sums up the updates they need. Never throws: the tasks that call it clear their flags after it.
    private void LoadInstalled()
    {
        var tags = new Dictionary<string, string>();
        foreach (var (pack, _) in Packs)
        {
            var file = Path.Combine(MainWindow.ShotsRoot, pack, ".version");
            try
            {
                if (File.Exists(file))
                    tags[pack] = File.ReadAllText(file).Trim();
            }
            // Taken as not installed: a download puts the full pack in again.
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"Can't read {file}");
            }
        }
        installed = tags;
        // Empty deltas aren't worth a notice: they come along with the next real update, or from the packs window.
        UpdateSize = tags.Keys.Where(Offered).Sum(p => PlanFor(p).Where(d => d.Asset.Size > EmptyZip).Sum(d => d.Asset.Size));
    }

    private bool Offered(string pack) => releases.Count > 0 && releases[0].Assets.Any(a => a.Name == $"{pack}.zip");

    // Downloads that bring an offered pack to the newest release: the delta of each release since the installed one,
    // oldest first, or the full pack if it isn't installed, its release is gone, a release since has no delta for it,
    // or the deltas weigh as much as the full pack (it was reshot whole, say).
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

    // Ticked at first: the pack of the character's gender, accessories and weapons, and updates.
    private bool Suggested(string pack) => installed.ContainsKey(pack)
        ? PlanFor(pack).Count > 0
        : pack is "accessories" or "weapons" || pack == MainWindow.GenderPack(Plugin.CharacterFemale);

    public override void Draw()
    {
        ImGui.TextUnformatted("Pictures of the gear for the Screenshots mode, downloaded from GitHub.");
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

    // One job at a time, off the game thread. The download folder is cleared after it, failed or not, and the tags reread;
    // the main window is told to rescan the shots.
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
                // A file still in use leaves the folder behind: it is cleared again after the next job.
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, $"Can't clear {Staging}");
                }
                LoadInstalled();
                ShotsChanged?.Invoke(this, EventArgs.Empty);
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
                Plugin.Log.Warning(ex, $"Can't install the {pack} screenshots");
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
            Plugin.Log.Warning(ex, $"Can't delete the {pack} screenshots");
        }
        return Task.CompletedTask;
    });

    // A full pack is unpacked beside shots/ and swapped in whole; deltas are unpacked over the installed pack.
    // The tag is written after each step, so an interrupted update carries on from there next time.
    private async Task Install(string pack, List<(string Tag, Asset Asset)> plan)
    {
        var dir = Path.Combine(MainWindow.ShotsRoot, pack);
        Directory.CreateDirectory(Staging);
        // A full pack is moved into it, which needs it there: it may have been removed by hand since the plugin started.
        Directory.CreateDirectory(MainWindow.ShotsRoot);
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
                // The old pack is back in place if the new one can't go in.
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

    // An installed pack leaves shots/ in one rename, so a file in use can't leave half of it behind: the rename fails whole.
    // It goes into the download folder, cleared after the job. Null if the pack isn't there.
    private static string? MoveAside(string pack)
    {
        var dir = Path.Combine(MainWindow.ShotsRoot, pack);
        if (!Directory.Exists(dir))
            return null;
        var old = Path.Combine(Staging, $"{pack}.old");
        if (Directory.Exists(old))
            Directory.Delete(old, true);
        Directory.CreateDirectory(Staging);
        Directory.Move(dir, old);
        return old;
    }

    // Only the shots, and no more of them in all than the zip itself weighs: publish.cs stores them uncompressed.
    // So a broken or tampered zip can't fill the disk or put other files in. Unpacking stops at the next file once cancelled.
    private void Extract(string zip, string dir)
    {
        using var archive = ZipFile.OpenRead(zip);
        var left = new FileInfo(zip).Length;
        foreach (var entry in archive.Entries)
        {
            cts.Token.ThrowIfCancellationRequested();
            if (!Shot.IsMatch(entry.FullName))
                continue;
            // A file is never unpacked past the length the zip gives it.
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

        // GitHub's checksum of the upload: a download cut short or garbled isn't unpacked. Older uploads may have none.
        if (asset.Digest != null && asset.Digest != $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}")
            throw new InvalidDataException($"{asset.Name} doesn't match GitHub's checksum.");
    }
}
