using System;
using System.Collections.Generic;
using System.Diagnostics;
using PCL.Core.Utils.Processes;

namespace PCL.Core.Minecraft.Launch;

public static class MinecraftLaunchProcessExecutionService
{
    public static ProcessStartRequest BuildCustomCommandStartRequest(MinecraftLaunchCustomCommandShellPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new ProcessStartRequest(plan.FileName)
        {
            Arguments = plan.Arguments,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = plan.UseShellExecute,
            CreateNoWindow = plan.CreateNoWindow
        };
    }

    public static ProcessStartRequest BuildGameProcessStartRequest(MinecraftLaunchProcessShellPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var environmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatingSystem.IsWindows() ? "Path" : "PATH"] = plan.PathEnvironmentValue,
            ["appdata"] = plan.AppDataEnvironmentValue
        };
        foreach (var environmentVariable in plan.EnvironmentVariables)
        {
            environmentVariables[environmentVariable.Key] = environmentVariable.Value;
        }

        var request = new ProcessStartRequest(plan.FileName)
        {
            Arguments = plan.Arguments,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = plan.UseShellExecute,
            CreateNoWindow = plan.CreateNoWindow,
            RedirectStandardOutput = plan.RedirectStandardOutput,
            RedirectStandardError = plan.RedirectStandardError,
            EnvironmentVariables = environmentVariables
        };

        return request;
    }

    public static bool TryApplyPriority(Process process, MinecraftLaunchProcessPriorityKind priorityKind)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsWindows())
        {
            return TryApplyUnixPriority(process, priorityKind);
        }

        try
        {
            process.PriorityBoostEnabled = true;
            switch (priorityKind)
            {
                case MinecraftLaunchProcessPriorityKind.AboveNormal:
                    process.PriorityClass = ProcessPriorityClass.AboveNormal;
                    break;
                case MinecraftLaunchProcessPriorityKind.BelowNormal:
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                    break;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // Linux/macOS：使用 nice 值映射 PCL 的三档优先级。
    // 提高优先级需要 CAP_SYS_NICE；没有权限时保持默认 nice，不要向用户抛错。
    private static bool TryApplyUnixPriority(Process process, MinecraftLaunchProcessPriorityKind priorityKind)
    {
        var niceValue = priorityKind switch
        {
            MinecraftLaunchProcessPriorityKind.AboveNormal => -5,
            MinecraftLaunchProcessPriorityKind.BelowNormal => 10,
            _ => 0
        };

        if (niceValue == 0)
        {
            return true;
        }

        try
        {
            using var renice = Process.Start(new ProcessStartInfo
            {
                FileName = "renice",
                ArgumentList = { "-n", niceValue.ToString(), "-p", process.Id.ToString() },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (renice is null)
            {
                return false;
            }

            renice.WaitForExit(3000);
            return renice.HasExited && renice.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
