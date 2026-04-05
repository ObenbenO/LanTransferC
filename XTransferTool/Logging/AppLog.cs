using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace XTransferTool.Logging;

public static class AppLog
{
    public static string LogFolder(string profile)
    {
        var p = string.IsNullOrWhiteSpace(profile) ? "default" : profile.Trim();

        var overrideDir = Environment.GetEnvironmentVariable("XTRANSFER_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return Path.Combine(overrideDir.Trim(), p);

        var baseDir = AppContext.BaseDirectory;
        var prefer = Path.GetFullPath(Path.Combine(baseDir, "..", "log", p));
        if (TryEnsureWritable(prefer))
            return prefer;

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = baseDir;

        return Path.Combine(root, "XTransferTool", p, "logs");
    }

    public static void Init(string profile)
    {
        var folder = LogFolder(profile);
        Directory.CreateDirectory(folder);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(folder, "xtransfer-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 32 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("logger initialized profile={Profile} folder={Folder}", profile, folder);
    }

    public static void Shutdown()
    {
        try
        {
            Log.Information("logger shutting down");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static bool TryEnsureWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".probe");
            using (File.Open(probe, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
            {
            }
            try { File.Delete(probe); } catch { }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
