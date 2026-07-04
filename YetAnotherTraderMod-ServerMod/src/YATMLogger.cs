using System;
using System.IO;

namespace YetAnotherTraderMod.src;

public static class YATMLogger
{
    private static string? _logPath;
    private static bool _initialized = false;
    
    // [NEW] Global Debug Flag
    public static bool IsDebugEnabled { get; set; } = false;
    public static bool IsRealDebugEnabled { get; set; } = false;

    public static void Init(string modPath)
    {
        _logPath = Path.Combine(modPath, "debug.log");
        try 
        {
            // Overwrite existing file (Create)
            File.WriteAllText(_logPath, $"[Tony] Debug Log Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n------------------------------------------------\n");
            _initialized = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tony] Failed to initialize log file: {ex.Message}");
        }
    }

    public static void Log(string message)
    {
        if (!_initialized || _logPath == null) return;
        try
        {
             var formatted = $"[{DateTime.Now:HH:mm:ss}] {message}";
             File.AppendAllText(_logPath, formatted + "\n");
        }
        catch 
        {
            // Fail silently to avoid console spam
        }
    }

    // [NEW] Detailed Debug Log
    public static void LogDebug(string message)
    {
        if (!IsDebugEnabled) return;
        if (!_initialized || _logPath == null) return;

        try 
        {
            var formatted = $"[DEBUG] [{DateTime.Now:HH:mm:ss}] {message}";
            File.AppendAllText(_logPath, formatted + "\n");
            // Also print to server console for immediate visibility if debug is ON
            Console.WriteLine($"[Tony-DEBUG] {message}");
        }
        catch { }
    }

    public static void LogRealDebug(string message)
    {
        if (!IsRealDebugEnabled) return;
        if (!_initialized || _logPath == null) return;

        try
        {
            var formatted = $"[REAL DEBUG] [{DateTime.Now:HH:mm:ss}] {message}";
            File.AppendAllText(_logPath, formatted + "\n");
            // Also print to server console for immediate visibility if debug is ON
            Console.WriteLine($"[Tony-REAL DEBUG] {message}");
        }
        catch { }
    }
}
