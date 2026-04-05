using System;
using System.IO;

namespace XTransferTool.Remote;

public static class FfmpegLocator
{
    public static string ResolveFfmpegPath()
    {
        var baseDir = AppContext.BaseDirectory;

        if (OperatingSystem.IsWindows())
        {
            var bundled = Path.Combine(baseDir, "native", "windows", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(bundled))
                return bundled;
        }

        if (OperatingSystem.IsMacOS())
        {
            var bundled = Path.Combine(baseDir, "native", "macos", "ffmpeg", "ffmpeg");
            if (File.Exists(bundled))
                return bundled;
        }

        return "ffmpeg";
    }
}

