using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using PCL.Core.App;
using PCL.Core.App.Configuration.Storage;
using PCL.Core.App.Essentials;
using PCL.Core.App.I18n;
using PCL.Core.Logging;
using PCL.Core.Minecraft;
using PCL.Core.Minecraft.Java;
using PCL.Core.Minecraft.Launch;
using PCL.Core.Utils;
using PCL.Frontend.Avalonia.Cli;
using PCL.Frontend.Avalonia.Models;

namespace PCL.Frontend.Avalonia.Workflows;

internal static partial class FrontendLaunchCompositionService
{
    private static IReadOnlyDictionary<string, string> BuildLaunchEnvironmentOverrides(
        YamlFileProvider localConfig,
        YamlFileProvider? instanceConfig,
        IKeyValueFileProvider sharedConfig)
    {
        var environmentVariables = ParseLaunchEnvironmentVariables(
            ReadValue(localConfig, "LaunchAdvanceEnvironmentVariables", string.Empty));
        if (instanceConfig is not null)
        {
            MergeEnvironmentVariables(
                environmentVariables,
                ParseLaunchEnvironmentVariables(ReadValue(instanceConfig, "VersionAdvanceEnvironmentVariables", string.Empty)));
        }

        if (ShouldForceX11OnWayland(localConfig, instanceConfig))
        {
            environmentVariables["XDG_SESSION_TYPE"] = "x11";
            environmentVariables["WAYLAND_DISPLAY"] = string.Empty;
        }

        ApplyLinuxGpuPreference(environmentVariables, sharedConfig);

        return environmentVariables;
    }

    // Linux 下 PCL 的“使用独立显卡”不再写 Windows 注册表，而是通过 PRIME 环境变量实现。
    private static void ApplyLinuxGpuPreference(
        IDictionary<string, string> environmentVariables,
        IKeyValueFileProvider sharedConfig)
    {
        if (!OperatingSystem.IsLinux() || !ReadValue(sharedConfig, "LaunchAdvanceGraphicCard", true))
        {
            return;
        }

        // 单显卡设备上设置 DRI_PRIME 会让 Mesa 输出 "Inconsistent value" 警告并回退到 0，
        // 这会在游戏日志里表现为莫名其妙的错误，因此只有确实存在多块 GPU 时才启用 PRIME。
        var hasNvidiaDriver = File.Exists("/proc/driver/nvidia/version");
        if (!hasNvidiaDriver && CountDrmRenderNodes() < 2)
        {
            return;
        }

        environmentVariables["DRI_PRIME"] = "1";
        if (hasNvidiaDriver)
        {
            environmentVariables["__NV_PRIME_RENDER_OFFLOAD"] = "1";
            environmentVariables["__GLX_VENDOR_LIBRARY_NAME"] = "nvidia";
            environmentVariables["__VK_LAYER_NV_optimus"] = "NVIDIA_only";
        }
    }

    private static int CountDrmRenderNodes()
    {
        try
        {
            return Directory.Exists("/dev/dri")
                ? Directory.GetFiles("/dev/dri", "renderD*").Length
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    // 按顺序组合 Linux 启动包装器：gamemoderun -> mangohud -> 用户自定义包装命令。
    private static string BuildLinuxWrapperCommand(
        IKeyValueFileProvider sharedConfig,
        string userWrapperCommand)
    {
        if (!OperatingSystem.IsLinux())
        {
            return userWrapperCommand;
        }

        var wrappers = new List<string>();
        if (ReadValue(sharedConfig, "LaunchAdvanceGameMode", false) &&
            IsExecutableOnPath("gamemoderun"))
        {
            wrappers.Add("gamemoderun");
        }

        if (ReadValue(sharedConfig, "LaunchAdvanceMangoHud", false) &&
            IsExecutableOnPath("mangohud"))
        {
            wrappers.Add("mangohud");
        }

        if (!string.IsNullOrWhiteSpace(userWrapperCommand))
        {
            wrappers.Add(userWrapperCommand);
        }

        return string.Join(' ', wrappers);
    }

    internal static bool IsExecutableOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, string> ParseLaunchEnvironmentVariables(string rawValue)
    {
        var environmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return environmentVariables;
        }

        foreach (var rawLine in rawValue.Replace("\r", string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line[7..].TrimStart();
            }

            var separatorIndex = line.IndexOf('=');
            var key = separatorIndex >= 0
                ? line[..separatorIndex].Trim()
                : line;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = separatorIndex >= 0
                ? line[(separatorIndex + 1)..]
                : string.Empty;
            environmentVariables[key] = value;
        }

        return environmentVariables;
    }

    private static void MergeEnvironmentVariables(
        IDictionary<string, string> target,
        IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var (key, value) in overrides)
        {
            target[key] = value;
        }
    }

    private static bool ShouldForceX11OnWayland(
        YamlFileProvider localConfig,
        YamlFileProvider? instanceConfig)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var isEnabled = ResolveForceX11OnWayland(localConfig, instanceConfig);
        if (!isEnabled)
        {
            return false;
        }

        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        return string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(display);
    }

    private static bool ResolveForceX11OnWayland(
        YamlFileProvider localConfig,
        YamlFileProvider? instanceConfig)
    {
        var globalEnabled = ReadValue(localConfig, "LaunchAdvanceForceX11OnWayland", false);
        if (instanceConfig is null)
        {
            return globalEnabled;
        }

        var instanceMode = Math.Clamp(ReadValue(instanceConfig, "VersionAdvanceForceX11OnWayland", 0), 0, 2);
        return instanceMode switch
        {
            1 => true,
            2 => false,
            _ => globalEnabled
        };
    }

}
