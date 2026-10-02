using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
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
    /// Hands the update to a separate Slate process (<c>--update</c>) that shows download progress,
    /// so the running Slate can quit right away. The caller should exit right after.
    /// </summary>
    public static void StartUpdater(UpdateInfo update)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        psi.ArgumentList.Add("--update");
        psi.ArgumentList.Add(update.Version.ToString(3));
        psi.ArgumentList.Add(update.InstallerUrl);
        psi.ArgumentList.Add(update.Sha256 ?? "-");
        Process.Start(psi)?.Dispose();
    }

    /// <summary>
    /// Removes installers left in %TEMP% by earlier updates (64 MB each). Runs when Slate starts,
    /// which is after the installer has finished; a file still in use is skipped.
    /// </summary>
    public static void DeleteOldInstallers() => Task.Run(() =>
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), "SlateSetup-*.exe"))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // still running or locked: try again next start
            }
        }
    });

    /// <summary>Starts the downloaded installer silently; it closes Slate, replaces it and relaunches it (/RELAUNCH=1).</summary>
    public static void RunInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1")
        {
            UseShellExecute = true,
        })?.Dispose();

    /// <summary>Downloads the installer into <paramref name="directory"/> and checks its SHA-256. Returns its path.</summary>
    /// <param name="progress">Bytes downloaded so far and the total size (null if the server didn't say).</param>
    public static async Task<string> DownloadAsync(UpdateInfo update, string directory,
        IProgress<(long Read, long? Total)>? progress = null, CancellationToken cancel = default)
    {
        string path = Path.Combine(directory, $"SlateSetup-{update.Version}.exe");

        using (var response = await Http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var file = File.Create(path);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), cancel);
                read += n;
                progress?.Report((read, total));
            }
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
