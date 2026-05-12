using System;
using System.IO;

namespace ai02;

internal static class BattlefieldReplayStoragePath
{
    public static string ResolveDirectory(string directoryName)
    {
        var normalized = string.IsNullOrWhiteSpace(directoryName)
            ? "ReplayLogs"
            : directoryName.Trim();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncherCN",
            "pluginConfigs",
            "ai02",
            normalized);
    }
}
