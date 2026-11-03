using System.Runtime.InteropServices;

namespace PCL.Frontend.Avalonia.Workflows;

internal enum FrontendLinuxPackageManager
{
    Unknown = 0,
    Apt = 1,
    Dnf = 2,
    Pacman = 3,
    Zypper = 4
}

internal sealed record FrontendLinuxDistribution(
    string Id,
    string Name,
    string VersionId,
    FrontendLinuxPackageManager PackageManager)
{
    public bool IsKnown => PackageManager != FrontendLinuxPackageManager.Unknown;
}

/// <summary>
/// 检测当前 Linux 发行版及其包管理器，供百宝箱与设置页展示发行版相关的信息与命令。
/// </summary>
internal static class FrontendLinuxDistributionService
{
    private const string OsReleasePath = "/etc/os-release";
    private static readonly Lazy<FrontendLinuxDistribution> Cached = new(Detect, isThreadSafe: true);

    public static FrontendLinuxDistribution Current => Cached.Value;

    public static string GetPackageManagerName(FrontendLinuxPackageManager packageManager)
    {
        return packageManager switch
        {
            FrontendLinuxPackageManager.Apt => "apt",
            FrontendLinuxPackageManager.Dnf => "dnf",
            FrontendLinuxPackageManager.Pacman => "pacman",
            FrontendLinuxPackageManager.Zypper => "zypper",
            _ => string.Empty
        };
    }

    public static string BuildJavaInstallCommand(FrontendLinuxPackageManager packageManager)
    {
        return packageManager switch
        {
            FrontendLinuxPackageManager.Apt => "sudo apt install openjdk-21-jre",
            FrontendLinuxPackageManager.Dnf => "sudo dnf install java-21-openjdk",
            FrontendLinuxPackageManager.Pacman => "sudo pacman -S jre21-openjdk",
            FrontendLinuxPackageManager.Zypper => "sudo zypper install java-21-openjdk",
            _ => string.Empty
        };
    }

    public static string BuildCacheCleanCommand(FrontendLinuxPackageManager packageManager)
    {
        return packageManager switch
        {
            FrontendLinuxPackageManager.Apt => "sudo apt clean",
            FrontendLinuxPackageManager.Dnf => "sudo dnf clean all",
            FrontendLinuxPackageManager.Pacman => "sudo pacman -Sc",
            FrontendLinuxPackageManager.Zypper => "sudo zypper clean --all",
            _ => string.Empty
        };
    }

    private static FrontendLinuxDistribution Detect()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new FrontendLinuxDistribution("windows", "Windows", string.Empty, FrontendLinuxPackageManager.Unknown);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new FrontendLinuxDistribution("macos", "macOS", string.Empty, FrontendLinuxPackageManager.Unknown);
        }

        var values = ReadOsRelease();
        var id = values.GetValueOrDefault("ID", string.Empty).Trim().Trim('"');
        var like = values.GetValueOrDefault("ID_LIKE", string.Empty).Trim().Trim('"');
        var name = values.GetValueOrDefault("PRETTY_NAME", values.GetValueOrDefault("NAME", id)).Trim().Trim('"');
        var version = values.GetValueOrDefault("VERSION_ID", string.Empty).Trim().Trim('"');
        var packageManager = ResolvePackageManager(id, like);
        return new FrontendLinuxDistribution(
            string.IsNullOrWhiteSpace(id) ? "linux" : id.ToLowerInvariant(),
            string.IsNullOrWhiteSpace(name) ? "Linux" : name,
            version,
            packageManager);
    }

    private static FrontendLinuxPackageManager ResolvePackageManager(string id, string like)
    {
        foreach (var token in new[] { id, like })
        {
            foreach (var part in token.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (part)
                {
                    case "debian":
                    case "ubuntu":
                    case "linuxmint":
                    case "pop":
                    case "elementary":
                    case "kali":
                    case "raspbian":
                    case "deepin":
                    case "uos":
                        return FrontendLinuxPackageManager.Apt;
                    case "fedora":
                    case "rhel":
                    case "centos":
                    case "rocky":
                    case "almalinux":
                    case "ol":
                    case "amzn":
                        return FrontendLinuxPackageManager.Dnf;
                    case "arch":
                    case "archlinux":
                    case "manjaro":
                    case "endeavouros":
                    case "cachyos":
                    case "garuda":
                        return FrontendLinuxPackageManager.Pacman;
                    case "opensuse":
                    case "suse":
                    case "sles":
                    case "opensuse-leap":
                    case "opensuse-tumbleweed":
                        return FrontendLinuxPackageManager.Zypper;
                }
            }
        }

        // 未识别的发行版：按可执行文件兜底探测。
        return File.Exists("/usr/bin/pacman") ? FrontendLinuxPackageManager.Pacman
            : File.Exists("/usr/bin/apt") ? FrontendLinuxPackageManager.Apt
            : File.Exists("/usr/bin/dnf") ? FrontendLinuxPackageManager.Dnf
            : File.Exists("/usr/bin/zypper") ? FrontendLinuxPackageManager.Zypper
            : FrontendLinuxPackageManager.Unknown;
    }

    private static Dictionary<string, string> ReadOsRelease()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(OsReleasePath))
            {
                return result;
            }

            foreach (var line in File.ReadAllLines(OsReleasePath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                result[trimmed[..separator]] = trimmed[(separator + 1)..];
            }
        }
        catch (IOException)
        {
            // 读取失败时退回默认值。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }

        return result;
    }
}
