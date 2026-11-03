using Avalonia;

namespace PCL.Frontend.Avalonia.Workflows;

internal enum FrontendStartupRenderingMode
{
    Default,
    Win32Software,
    X11Software,
    AvaloniaNativeSoftware
}

internal sealed record FrontendStartupRenderingConfiguration(
    bool IsHardwareAccelerationToggleAvailable,
    bool DisableHardwareAcceleration,
    FrontendStartupRenderingMode RenderingMode);

internal static class FrontendStartupRenderingService
{
    internal const string DisableHardwareAccelerationConfigKey = "SystemDisableHardwareAcceleration";

    public static FrontendStartupRenderingConfiguration Resolve(
        FrontendRuntimePaths runtimePaths,
        FrontendDesktopPlatformKind platformKind)
    {
        ArgumentNullException.ThrowIfNull(runtimePaths);

        var softwareRenderingMode = ResolveSoftwareRenderingMode(platformKind);
        var isToggleAvailable = softwareRenderingMode != FrontendStartupRenderingMode.Default;
        var disableHardwareAcceleration = isToggleAvailable && ReadDisableHardwareAcceleration(runtimePaths);

        return new FrontendStartupRenderingConfiguration(
            isToggleAvailable,
            disableHardwareAcceleration,
            disableHardwareAcceleration
                ? softwareRenderingMode
                : FrontendStartupRenderingMode.Default);
    }

    public static AppBuilder Apply(AppBuilder builder, FrontendStartupRenderingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.RenderingMode switch
        {
            FrontendStartupRenderingMode.Win32Software => builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software]
            }),
            FrontendStartupRenderingMode.X11Software => builder.With(new X11PlatformOptions
            {
                RenderingMode = [X11RenderingMode.Software],
                UseDBusMenu = false
            }),
            FrontendStartupRenderingMode.AvaloniaNativeSoftware => builder.With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = [AvaloniaNativeRenderingMode.Software]
            }),
            // 大多数 Linux 桌面（Hyprland/DMS 等）并不提供 com.canonical.AppMenu.Registrar，
            // 关闭 DBus 全局菜单可避免启动时产生 ServiceUnknown 未观察异常。
            _ when OperatingSystem.IsLinux() => builder.With(new X11PlatformOptions
            {
                UseDBusMenu = false
            }),
            _ => builder
        };
    }

    private static bool ReadDisableHardwareAcceleration(FrontendRuntimePaths runtimePaths)
    {
        var sharedConfig = runtimePaths.OpenSharedConfigProvider();
        return sharedConfig.Exists(DisableHardwareAccelerationConfigKey)
            && sharedConfig.Get<bool>(DisableHardwareAccelerationConfigKey);
    }

    private static FrontendStartupRenderingMode ResolveSoftwareRenderingMode(FrontendDesktopPlatformKind platformKind)
    {
        return platformKind switch
        {
            FrontendDesktopPlatformKind.Windows => FrontendStartupRenderingMode.Win32Software,
            FrontendDesktopPlatformKind.Linux => FrontendStartupRenderingMode.X11Software,
            FrontendDesktopPlatformKind.MacOS => FrontendStartupRenderingMode.AvaloniaNativeSoftware,
            _ => FrontendStartupRenderingMode.Default
        };
    }
}
