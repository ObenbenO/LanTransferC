using System;
using System.IO;

namespace XTransferTool;

public static class AppPaths
{
    public static string ExeDir { get; } = ResolveExeDir();

    private static string ResolveExeDir()
    {
        try
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(p))
            {
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir;
            }
        }
        catch
        {
        }

        return AppContext.BaseDirectory;
    }
}

