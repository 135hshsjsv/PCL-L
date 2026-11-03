using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace PCL.Frontend.Avalonia.Workflows;

internal sealed record FrontendTerracottaProfile(
    string MachineId,
    string Name,
    string Vendor,
    string Kind);

internal sealed record FrontendTerracottaState(
    long Index,
    string State,
    string? Room,
    int? ProfileIndex,
    IReadOnlyList<FrontendTerracottaProfile> Profiles,
    string? Url,
    string? Difficulty,
    int ErrorType)
{
    public static FrontendTerracottaState Empty { get; } = new(0, "waiting", null, null, [], null, null, 0);
}

/// <summary>
/// Drives a local <see href="https://github.com/burningtnt/Terracotta">Terracotta</see> instance through its
/// HTTP API. Terracotta is a separate AGPL-3.0 program; this class only talks to the unmodified binary via
/// its documented loopback HTTP interface, which the license explicitly permits as long as the copyright
/// notice is shown in the UI.
/// </summary>
internal sealed class TerracottaSecondaryInstanceException : Exception
{
    public TerracottaSecondaryInstanceException()
        : base("Another Terracotta host owns the global mutex.")
    {
    }
}

internal sealed class FrontendTerracottaService : IDisposable
{
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(30);

    public const string Version = "0.4.2";

    private const string ReleaseBaseUrl = "https://github.com/burningtnt/Terracotta/releases/download";
    private const string GiteeReleaseBaseUrl = "https://gitee.com/burningtnt/Terracotta/releases/download";
    private const string GitHubProxyPrefix = "https://ghfast.top/";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly FrontendRuntimePaths _runtimePaths;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _processLock = new();
    private readonly List<string> _recentLogLines = [];
    private Process? _process;
    private HttpClient? _client;
    private bool _attachedToExternalInstance;
    private bool _disposed;

    public FrontendTerracottaService(FrontendRuntimePaths runtimePaths)
    {
        ArgumentNullException.ThrowIfNull(runtimePaths);

        _runtimePaths = runtimePaths;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public string InstallDirectory => Path.Combine(_runtimePaths.LauncherAppDataDirectory, "Terracotta");

    public string ExecutablePath => Path.Combine(
        InstallDirectory,
        $"{ResolveAssetStem()}{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}");

    public string LogDirectory => Path.Combine(InstallDirectory, "logs");

    public bool IsInstalled => File.Exists(ExecutablePath);

    public bool IsRunning
    {
        get
        {
            lock (_processLock)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public IReadOnlyList<string> RecentLogLines
    {
        get
        {
            lock (_recentLogLines)
            {
                return _recentLogLines.ToArray();
            }
        }
    }

    public async Task EnsureInstalledAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (IsInstalled)
        {
            progress?.Report(1d);
            return;
        }

        Directory.CreateDirectory(InstallDirectory);
        var downloadPath = Path.Combine(InstallDirectory, $"terracotta-{Version}.download.tar.gz");
        Exception? lastError = null;

        foreach (var url in BuildDownloadUrls())
        {
            try
            {
                await DownloadAsync(url, downloadPath, progress, cancellationToken).ConfigureAwait(false);
                ExtractExecutable(downloadPath);
                TryDelete(downloadPath);
                progress?.Report(1d);
                return;
            }
            catch (OperationCanceledException)
            {
                TryDelete(downloadPath);
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                TryDelete(downloadPath);
            }
        }

        throw new InvalidOperationException(
            "Terracotta 下载失败，请检查网络后重试。" + (lastError is null ? string.Empty : $"\n{lastError.Message}"),
            lastError);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning && _client is not null)
            {
                return;
            }

            StopProcess();

            // Another launcher (HMCL, PCL2, FCL, ...) may already be hosting Terracotta. Its global mutex
            // publishes the loopback port, so reuse that instance instead of failing on the mutex.
            if (await TryAttachToRunningInstanceAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (!IsInstalled)
            {
                throw new InvalidOperationException("Terracotta 尚未安装。");
            }

            Directory.CreateDirectory(LogDirectory);

            var portFilePath = Path.Combine(InstallDirectory, "port.json");
            TryDelete(portFilePath);

            var startInfo = new ProcessStartInfo(ExecutablePath)
            {
                WorkingDirectory = InstallDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--hmcl");
            startInfo.ArgumentList.Add(portFilePath);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => CaptureLogLine(e.Data);
            process.ErrorDataReceived += (_, e) => CaptureLogLine(e.Data);

            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 Terracotta 进程。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_processLock)
            {
                _process = process;
            }

            long port;
            try
            {
                port = await WaitForPortAsync(process, portFilePath, cancellationToken).ConfigureAwait(false);
            }
            catch (TerracottaSecondaryInstanceException)
            {
                // The mutex was taken between the attach probe and the process start.
                if (!await TryAttachToRunningInstanceAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }

                return;
            }

            _attachedToExternalInstance = false;
            _client?.Dispose();
            _client = CreateApiClient(port);
        }
        catch
        {
            StopProcess();
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<FrontendTerracottaState> GetStateAsync(CancellationToken cancellationToken)
    {
        var payload = await GetJsonAsync("state", cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Terracotta 未返回状态数据。");
        return ParseState(payload);
    }

    /// <summary>Starts hosting: Terracotta scans for a LAN world and exposes it to the room.</summary>
    public Task<bool> StartHostingAsync(string? playerName, CancellationToken cancellationToken)
    {
        var url = "state/scanning";
        if (!string.IsNullOrWhiteSpace(playerName))
        {
            url += $"?player={Uri.EscapeDataString(playerName)}";
        }

        return SendCommandAsync(url, cancellationToken);
    }

    /// <summary>Joins a room; returns <see langword="false" /> when the room code was rejected.</summary>
    public Task<bool> JoinRoomAsync(string roomCode, string? playerName, CancellationToken cancellationToken)
    {
        var url = $"state/guesting?room={Uri.EscapeDataString(roomCode)}";
        if (!string.IsNullOrWhiteSpace(playerName))
        {
            url += $"&player={Uri.EscapeDataString(playerName)}";
        }

        return SendCommandAsync(url, cancellationToken);
    }

    public Task<bool> ResetToIdleAsync(CancellationToken cancellationToken)
    {
        return SendCommandAsync("state/ide", cancellationToken);
    }

    public async Task ShutdownAsync()
    {
        if (_attachedToExternalInstance)
        {
            // The instance belongs to another launcher; detach without stopping it.
            _client?.Dispose();
            _client = null;
            _attachedToExternalInstance = false;
            return;
        }

        var client = _client;
        if (client is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var response = await client
                    .GetAsync("panic?peaceful=true", timeout.Token)
                    .ConfigureAwait(false);
                _ = response.IsSuccessStatusCode;
            }
            catch
            {
                // The process is killed below when a peaceful shutdown does not complete in time.
            }
        }

        StopProcess();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopProcess();
        _client?.Dispose();
        _client = null;
        _lifecycleGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<bool> TryAttachToRunningInstanceAsync(CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(Path.GetTempPath(), "terracotta", "terracotta.lock");
        try
        {
            if (!File.Exists(lockPath))
            {
                return false;
            }

            var bytes = await File.ReadAllBytesAsync(lockPath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 2)
            {
                return false;
            }

            var port = (bytes[0] << 8) | bytes[1];
            if (port is <= 0 or > 65535)
            {
                return false;
            }

            using var probe = CreateApiClient(port);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await probe.GetAsync("state", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            _client?.Dispose();
            _client = CreateApiClient(port);
            _attachedToExternalInstance = true;
            CaptureLogLine($"[Attach] Reusing the Terracotta instance already listening on 127.0.0.1:{port}.");
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task<long> WaitForPortAsync(Process process, string portFilePath, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + StartupTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                if (process.ExitCode == 0 &&
                    _recentLogLines.Any(static line => line.Contains("secondary mode", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new TerracottaSecondaryInstanceException();
                }

                throw new InvalidOperationException(
                    $"Terracotta 启动后立即退出（退出码 {process.ExitCode}）。{DescribeRecentLog()}");
            }

            if (File.Exists(portFilePath))
            {
                try
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(portFilePath, cancellationToken)
                        .ConfigureAwait(false));
                    if (document.RootElement.TryGetProperty("port", out var portElement) &&
                        portElement.TryGetInt64(out var port) &&
                        port is > 0 and <= 65535)
                    {
                        return port;
                    }
                }
                catch (JsonException)
                {
                    // The launcher may still be writing the file; retry on the next tick.
                }
                catch (IOException)
                {
                    // Same as above.
                }
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Terracotta 在 {StartupTimeout.TotalSeconds:0} 秒内没有准备就绪。{DescribeRecentLog()}");
    }

    private static HttpClient CreateApiClient(long port)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = RequestTimeout
        };
    }

    private async Task<JsonDocument?> GetJsonAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("Terracotta 尚未启动。");
        using var response = await client.GetAsync(relativeUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Terracotta 请求 {relativeUrl} 失败：HTTP {(int)response.StatusCode}。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SendCommandAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("Terracotta 尚未启动。");
        using var response = await client.GetAsync(relativeUrl, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Terracotta 请求 {relativeUrl} 失败：HTTP {(int)response.StatusCode}。");
        }

        return true;
    }

    private static FrontendTerracottaState ParseState(JsonDocument document)
    {
        var root = document.RootElement;
        var state = GetString(root, "state") ?? "waiting";
        var profiles = new List<FrontendTerracottaProfile>();
        if (root.TryGetProperty("profiles", out var profilesElement) &&
            profilesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var profile in profilesElement.EnumerateArray())
            {
                profiles.Add(new FrontendTerracottaProfile(
                    GetString(profile, "machine_id") ?? string.Empty,
                    GetString(profile, "name") ?? string.Empty,
                    GetString(profile, "vendor") ?? string.Empty,
                    GetString(profile, "kind") ?? string.Empty));
            }
        }

        return new FrontendTerracottaState(
            root.TryGetProperty("index", out var index) && index.TryGetInt64(out var parsedIndex) ? parsedIndex : 0,
            state,
            GetString(root, "room"),
            root.TryGetProperty("profile_index", out var profileIndex) && profileIndex.TryGetInt32(out var parsedProfileIndex)
                ? parsedProfileIndex
                : null,
            profiles,
            GetString(root, "url"),
            GetString(root, "difficulty"),
            root.TryGetProperty("type", out var errorType) && errorType.TryGetInt32(out var parsedErrorType)
                ? parsedErrorType
                : -1);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private async Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var client = FrontendHttpProxyService.CreateLauncherHttpClient(
            TimeSpan.FromMinutes(10),
            "PCL-ME-Frontend-Avalonia");
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}：{url}");
        }

        var totalBytes = response.Content.Headers.ContentLength;
        var buffer = new byte[81920];
        long writtenBytes = 0;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(destinationPath);

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCts.CancelAfter(DownloadIdleTimeout);

        while (true)
        {
            int current;
            try
            {
                current = await source.ReadAsync(buffer, idleCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"下载停滞超过 {DownloadIdleTimeout.TotalSeconds:0} 秒：{url}");
            }

            if (current <= 0)
            {
                break;
            }

            idleCts.CancelAfter(DownloadIdleTimeout);
            await target.WriteAsync(buffer.AsMemory(0, current), idleCts.Token).ConfigureAwait(false);
            writtenBytes += current;
            if (totalBytes is > 0)
            {
                progress?.Report(Math.Clamp((double)writtenBytes / totalBytes.Value, 0d, 0.99d));
            }
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (writtenBytes == 0)
        {
            throw new InvalidOperationException($"下载内容为空：{url}");
        }
    }

    private void ExtractExecutable(string archivePath)
    {
        Directory.CreateDirectory(InstallDirectory);

        using var archiveStream = File.OpenRead(archivePath);
        using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);

        while (tarReader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                entry.DataStream is null)
            {
                continue;
            }

            entry.ExtractToFile(ExecutablePath, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    ExecutablePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            return;
        }

        throw new InvalidOperationException("Terracotta 压缩包中没有可执行文件。");
    }

    private IEnumerable<string> BuildDownloadUrls()
    {
        var asset = $"{ResolveAssetStem()}-pkg.tar.gz";
        var tag = $"v{Version}";
        var githubUrl = $"{ReleaseBaseUrl}/{tag}/{asset}";

        yield return githubUrl;
        yield return GitHubProxyPrefix + githubUrl;
        yield return $"{GiteeReleaseBaseUrl}/{tag}/{asset}";
    }

    private static string ResolveAssetStem()
    {
        var os = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsMacOS()
                ? "macos"
                : OperatingSystem.IsLinux()
                    ? "linux"
                    : throw new PlatformNotSupportedException("Terracotta 不支持当前操作系统。");

        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.Arm => os == "android" ? "armv7" : "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.LoongArch64 => "loongarch64",
            System.Runtime.InteropServices.Architecture.RiscV64 => "riscv64",
            _ => throw new PlatformNotSupportedException("Terracotta 不支持当前处理器架构。")
        };

        if (os == "macos" && architecture is not ("x86_64" or "arm64"))
        {
            throw new PlatformNotSupportedException("Terracotta 不支持当前 macOS 架构。");
        }

        return $"terracotta-{Version}-{os}-{architecture}";
    }

    private void CaptureLogLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_recentLogLines)
        {
            _recentLogLines.Add(line);
            if (_recentLogLines.Count > 40)
            {
                _recentLogLines.RemoveRange(0, _recentLogLines.Count - 40);
            }
        }
    }

    private string DescribeRecentLog()
    {
        var lines = RecentLogLines;
        return lines.Count == 0
            ? string.Empty
            : $"\n{string.Join('\n', lines.TakeLast(6))}";
    }

    private void StopProcess()
    {
        Process? process;
        lock (_processLock)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // The process already exited or cannot be signalled; nothing else to do here.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
    }
}
