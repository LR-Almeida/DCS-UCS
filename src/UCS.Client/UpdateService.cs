using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using UCS.Common;

namespace UCS.Client;

public enum UpdateStage
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    WaitingForDcs,
    ClosingDcs,
    Installing,
    Error,
}

public sealed record UpdateState(UpdateStage Stage, ReleaseInfo? Release, string Message, int Progress);

/// <summary>
/// Checks GitHub releases, downloads the installer, verifies its SHA-256 against the release's
/// SHA256SUMS.txt and runs it silently (the installer updates the DCS scripts and restarts the client).
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly string[] DcsProcessNames = { "DCS", "DCS_mt" };
    private const string InstallerPrefix = "DCS-UCS-Setup-";
    private const string ChecksumAsset = "SHA256SUMS.txt";

    private readonly Func<ClientConfig> _config;
    private readonly SemVersion _current = SemVersion.Parse(Protocol.Version);
    private readonly SemaphoreSlim _busy = new(1, 1);
    private CancellationTokenSource? _waitCts;
    private System.Threading.Timer? _autoTimer;
    private string? _downloadedInstaller;

    public UpdateState State { get; private set; } = new(UpdateStage.Idle, null, "Not checked yet.", 0);
    public event Action<UpdateState>? StateChanged;
    /// <summary>Raised by automatic checks when a new, not skipped, version is found.</summary>
    public event Action<ReleaseInfo>? UpdateFound;
    public event Action<string>? Log;
    /// <summary>The installer was started: the application must exit now.</summary>
    public event Action? ExitRequested;

    public UpdateService(Func<ClientConfig> config) => _config = config;

    private const int SwRestore = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static string CurrentVersion => Protocol.Version;

    public static bool IsDcsRunning() => DcsProcesses().Any();

    private static List<Process> DcsProcesses() =>
        DcsProcessNames.SelectMany(n =>
        {
            try { return Process.GetProcessesByName(n); }
            catch { return Array.Empty<Process>(); }
        }).ToList();

    private void Set(UpdateStage stage, string message, ReleaseInfo? release = null, int progress = 0)
    {
        State = new UpdateState(stage, release ?? State.Release, message, progress);
        StateChanged?.Invoke(State);
    }

    /// <summary>Automatic checks: 15 s after start, then every 6 hours (when enabled).</summary>
    public void StartAutomaticChecks()
    {
        _autoTimer = new System.Threading.Timer(async _ =>
        {
            if (!_config().CheckForUpdates) return;
            if (State.Stage is UpdateStage.Downloading or UpdateStage.WaitingForDcs or UpdateStage.ClosingDcs or UpdateStage.Installing) return;
            await CheckAsync(manual: false).ConfigureAwait(false);
        }, null, TimeSpan.FromSeconds(15), TimeSpan.FromHours(6));
    }

    public async Task CheckAsync(bool manual)
    {
        if (!await _busy.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            Set(UpdateStage.Checking, "Checking GitHub for updates...");
            var cfg = _config();
            // While running a pre-release, always look at pre-releases too
            var includePre = cfg.IncludePreReleases || _current.IsPreRelease;
            var newer = await GitHubReleases.FindNewerAsync("DCS-UCS-Client", _current, includePre, CancellationToken.None).ConfigureAwait(false);
            if (newer == null)
            {
                Set(UpdateStage.UpToDate, $"You have the latest version ({CurrentVersion}). Last check: {DateTime.Now:HH:mm}.", null);
                State = State with { Release = null };
                if (manual) Log?.Invoke("Update check: no newer version.");
                return;
            }
            Set(UpdateStage.Available, $"Version {newer.Version} is available (you have {CurrentVersion}).", newer);
            var skipped = string.Equals(cfg.SkippedVersion, newer.Version!.ToString(), StringComparison.OrdinalIgnoreCase);
            Log?.Invoke($"Update available: {newer.Version}" + (skipped && !manual ? " (skipped by the user)" : ""));
            if (!manual && !skipped) UpdateFound?.Invoke(newer);
        }
        catch (Exception ex)
        {
            Set(UpdateStage.Error, "Update check failed: " + ex.Message);
            if (manual) Log?.Invoke("Update check failed: " + ex.Message);
        }
        finally
        {
            _busy.Release();
        }
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Downloads (if needed) and installs immediately. DCS keeps the old scripts until restarted.</summary>
    public async Task UpdateNowAsync()
    {
        var path = await EnsureDownloadedAsync().ConfigureAwait(false);
        if (path == null) return;
        if (IsDcsRunning())
            Log?.Invoke("DCS is running: restart DCS after the update so it loads the new UCS scripts.");
        RunInstaller(path);
    }

    /// <summary>Asks DCS to close (like clicking X), waits, then installs. Never kills DCS.</summary>
    public async Task CloseDcsAndUpdateAsync()
    {
        var path = await EnsureDownloadedAsync().ConfigureAwait(false);
        if (path == null) return;
        var procs = DcsProcesses();
        if (procs.Count == 0)
        {
            RunInstaller(path);
            return;
        }
        // DCS answers the close request with its own in-game confirmation box. It is drawn by DCS
        // (not a Windows dialog) and is never answered automatically: bring DCS to the front and
        // let the user confirm.
        const string confirmMsg = "DCS is asking for confirmation: confirm the exit in the DCS window. Waiting...";
        Set(UpdateStage.ClosingDcs, confirmMsg);
        Log?.Invoke(confirmMsg);
        foreach (var p in procs)
        {
            try
            {
                p.CloseMainWindow();
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, SwRestore);
                    SetForegroundWindow(p.MainWindowHandle);
                }
            }
            catch
            {
                // ignore
            }
        }
        for (var i = 0; i < 120 && IsDcsRunning(); i++) await Task.Delay(1000).ConfigureAwait(false);
        if (IsDcsRunning())
        {
            Log?.Invoke("DCS is still open (exit not confirmed). The update will start as soon as DCS closes.");
            await UpdateWhenDcsClosesAsync().ConfigureAwait(false);
            return;
        }
        RunInstaller(path);
    }

    /// <summary>Downloads now and installs as soon as DCS exits (immediately if DCS is not running).</summary>
    public async Task UpdateWhenDcsClosesAsync()
    {
        var path = await EnsureDownloadedAsync().ConfigureAwait(false);
        if (path == null) return;
        if (!IsDcsRunning())
        {
            RunInstaller(path);
            return;
        }
        _waitCts?.Cancel();
        _waitCts = new CancellationTokenSource();
        var ct = _waitCts.Token;
        Set(UpdateStage.WaitingForDcs, $"Version {State.Release?.Version} will be installed when DCS closes.");
        Log?.Invoke("Update scheduled: it will be installed when DCS closes.");
        try
        {
            while (IsDcsRunning()) await Task.Delay(3000, ct).ConfigureAwait(false);
            await Task.Delay(2000, ct).ConfigureAwait(false); // let DCS release its files
            RunInstaller(path);
        }
        catch (OperationCanceledException)
        {
            Set(UpdateStage.Available, "Scheduled update cancelled.");
        }
    }

    public void CancelScheduled() => _waitCts?.Cancel();

    // ------------------------------------------------------------------ download / install

    private async Task<string?> EnsureDownloadedAsync()
    {
        var release = State.Release;
        if (release == null)
        {
            Set(UpdateStage.Error, "No update selected. Click 'Check now' first.");
            return null;
        }
        if (_downloadedInstaller != null && File.Exists(_downloadedInstaller)) return _downloadedInstaller;

        var installer = release.FindAsset(n => n.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var sums = release.FindAsset(n => n.Equals(ChecksumAsset, StringComparison.OrdinalIgnoreCase));
        if (installer == null || sums == null)
        {
            Set(UpdateStage.Error, $"Release {release.Version} has no installer or no {ChecksumAsset}; automatic update is not possible. Use 'Open release page'.");
            return null;
        }

        await _busy.WaitAsync().ConfigureAwait(false);
        try
        {
            using var http = GitHubReleases.CreateHttpClient("DCS-UCS-Client", TimeSpan.FromMinutes(10));
            var sumsText = await http.GetStringAsync(sums.DownloadUrl).ConfigureAwait(false);
            var expected = sumsText.Split('\n')
                .Select(l => l.Trim().Split(new[] { ' ', '*', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(p => p.Length >= 2 && p[^1].Equals(installer.Name, StringComparison.OrdinalIgnoreCase))?[0];
            if (expected == null)
            {
                Set(UpdateStage.Error, $"{installer.Name} is not listed in {ChecksumAsset}; update aborted.");
                return null;
            }

            var dir = Path.Combine(Path.GetTempPath(), "DCS-UCS-update");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, installer.Name);
            Set(UpdateStage.Downloading, $"Downloading {installer.Name}...", null, 0);
            using (var resp = await http.GetAsync(installer.DownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? installer.Size;
                await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var dst = File.Create(path);
                var buffer = new byte[81920];
                long done = 0;
                int read, lastPct = -1;
                while ((read = await src.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    done += read;
                    var pct = total > 0 ? (int)(done * 100 / total) : 0;
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        Set(UpdateStage.Downloading, $"Downloading {installer.Name}... {pct}%", null, pct);
                    }
                }
            }

            string actual;
            await using (var fs = File.OpenRead(path))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs).ConfigureAwait(false));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                Set(UpdateStage.Error, "Downloaded installer failed the SHA-256 check and was deleted. Try again later.");
                Log?.Invoke($"Update aborted: SHA-256 mismatch (expected {expected}, got {actual}).");
                return null;
            }
            Log?.Invoke($"Update {release.Version} downloaded and verified (SHA-256 OK).");
            _downloadedInstaller = path;
            Set(UpdateStage.Available, $"Version {release.Version} downloaded and verified.", null, 100);
            return path;
        }
        catch (Exception ex)
        {
            Set(UpdateStage.Error, "Download failed: " + ex.Message);
            return null;
        }
        finally
        {
            _busy.Release();
        }
    }

    private void RunInstaller(string path)
    {
        try
        {
            var log = Path.Combine(Path.GetTempPath(), "DCS-UCS-update", "install.log");
            Set(UpdateStage.Installing, "Starting the installer (Windows may ask for permission)...");
            Process.Start(new ProcessStartInfo(path,
                $"/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NOCANCEL /UPDATE=1 /LOG=\"{log}\"")
            {
                UseShellExecute = true, // lets Windows show the UAC prompt
            });
            Log?.Invoke("Installer started; UCS-Client will close and reopen after the update.");
            ExitRequested?.Invoke();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Set(UpdateStage.Available, "Update cancelled (administrator permission was denied).");
        }
        catch (Exception ex)
        {
            Set(UpdateStage.Error, "Could not start the installer: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _autoTimer?.Dispose();
        _waitCts?.Cancel();
    }
}
