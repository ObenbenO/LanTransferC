using System;
using System.IO;
using System.Reflection;

namespace XTransferTool.Packaging;

public static class BundledTools
{
    public static void Ensure()
    {
        if (OperatingSystem.IsWindows())
        {
            EnsureResourceToFile("xtransfer.native.windows.ffmpeg.exe", Path.Combine("native", "windows", "ffmpeg", "ffmpeg.exe"), isExecutable: true);
        }

        if (OperatingSystem.IsMacOS())
        {
            EnsureResourceToFile("xtransfer.native.macos.ffmpeg", Path.Combine("native", "macos", "ffmpeg", "ffmpeg"), isExecutable: true);
            EnsureResourceToFile("xtransfer.native.macos.sck_capture", Path.Combine("native", "macos", "sck_capture", "sck_capture"), isExecutable: true);
        }
    }

    private static void EnsureResourceToFile(string resourceName, string relativePath, bool isExecutable)
    {
        var target = Path.Combine(XTransferTool.AppPaths.ExeDir, relativePath);
        if (File.Exists(target) && new FileInfo(target).Length > 0)
            return;

        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var temp = target + ".tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read))
            stream.CopyTo(fs);

        if (File.Exists(target))
            File.Delete(target);
        File.Move(temp, target);

        if (isExecutable && !OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch
            {
            }
        }
    }
}
