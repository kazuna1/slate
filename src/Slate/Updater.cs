using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Slate;

internal sealed record UpdateInfo(Version Version, string InstallerUrl, string? Sha256, string PageUrl);

/// <summary>Checks GitHub for a newer release and installs it via the silent installer.</summary>
internal static class Updater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/kazuna1/slate/releases/latest";
    private const string InstallerAsset = "SlateSetup.exe";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Installed via SlateSetup (vs. the portable zip), so we can update in place.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>The newer release, or null if this is the latest.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(LatestReleaseApi));
        var release = doc.RootElement;

        string tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) return null;
        var latest = Normalize(parsed);
        if (latest <= CurrentVersion) return null;

        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != InstallerAsset) continue;

            // GitHub publishes "sha256:<hex>" for each asset; verify against it when present.
            string? sha256 = asset.TryGetProperty("digest", out var digest)
                             && digest.GetString() is string d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? d["sha256:".Length..]
                : null;

            return new UpdateInfo(
                latest,
                asset.GetProperty("browser_download_url").GetString()!,
                sha256,
                release.GetProperty("html_url").GetString()!);
        }
        return null;
    }

    /// <summary>
    /// Downloads and verifies the installer, then starts it silently. The installer closes Slate,
    /// replaces it, and relaunches it (the /RELAUNCH=1 switch). The caller should exit right after.
    /// </summary>
    public static async Task DownloadAndRunAsync(UpdateInfo update)
    {
        string path = await DownloadAsync(update, Path.GetTempPath());
        Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1")
        {
            UseShellExecute = true,
        })?.Dispose();
    }

    /// <summary>Downloads the installer into <paramref name="directory"/> and checks its SHA-256. Returns its path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, string directory)
    {
        string path = Path.Combine(directory, $"SlateSetup-{update.Version}.exe");

        using (var response = await Http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file);
        }

        if (update.Sha256 != null)
        {
            string actual;
            await using (var file = File.OpenRead(path))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file));

            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidDataException("The downloaded installer didn't match its checksum, so it wasn't run.");
            }
        }
        return path;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Slate/{typeof(Updater).Assembly.GetName().Version?.ToString(3)}");
        return client;
    }
}
