using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace ImbotechConsulting.PiExplorer;

/// <summary>Looks for a newer GitHub release and replaces the running exe with it.</summary>
internal static class Updater
{
    private const string Repo = "pimboden/PiExplorer";//I'm hardcoding this here, so I don't need any settings file, I want it to work jsut with the exe, and I don't want to make it configurable for now. If I ever do, I'll add a settings file or something.
    private const string AssetName = "PiExplorer.exe";

    public sealed record Release(Version Version, string Tag, string DownloadUrl);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PiExplorer-Updater");
        return c;
    }

    public static Version Current
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            return ParseVersion(info) ?? Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        }
    }

    private static Version? ParseVersion(string text)
    {
        text = text.TrimStart('v', 'V');
        var end = text.IndexOfAny(new[] { '+', '-' }); // drop "+commit" / "-prerelease"
        if (end >= 0) text = text[..end];
        return Version.TryParse(text, out var v) ? v : null;
    }

    /// <summary>The latest release if it is newer than this exe, else null. Throws if GitHub cannot be reached.</summary>
    public static async Task<Release?> CheckAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        if (version == null || version <= Current) return null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
            if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                return new Release(version, tag, a.GetProperty("browser_download_url").GetString()!);
        return null;
    }

    /// <summary>Downloads the release next to the current exe and swaps it in. Starts the new exe; the caller must exit.</summary>
    public static async Task InstallAsync(Release r)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown exe path");
        var tmp = exe + ".new";
        var old = exe + ".old";
        await using (var src = await Http.GetStreamAsync(r.DownloadUrl))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst);
        if (new FileInfo(tmp).Length < 10_000) { File.Delete(tmp); throw new IOException("Downloaded file is too small"); }

        // a running exe cannot be overwritten, but it can be renamed
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(tmp, exe); }
        catch { File.Move(old, exe); throw; } // put the old one back
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    /// <summary>Call at startup: removes the previous exe left behind by an update.</summary>
    public static void Cleanup()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            foreach (var f in new[] { exe + ".old", exe + ".new" })
                if (File.Exists(f)) File.Delete(f);
        }
        catch { /* still locked by the old process: next start */ }
    }
}
