namespace AstroCounter.Core.Stellarium;

using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

/// <summary>
/// Locates and launches Stellarium in windowed mode (--full-screen no).
/// </summary>
public static class StellariumLauncher
{
    private static readonly string[] CandidatePaths = new[]
    {
        @"C:\Program Files\Stellarium\stellarium.exe",
        @"C:\Program Files (x86)\Stellarium\stellarium.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Stellarium", "stellarium.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stellarium", "stellarium.exe")
    };

    /// <summary>
    /// Searches for the Stellarium executable in common installation folders and Windows Registry.
    /// </summary>
    public static string? FindStellariumExecutable()
    {
        // 1. Direct candidate paths
        foreach (var path in CandidatePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        // 2. Windows App Paths registry
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\stellarium.exe")
                             ?? Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\stellarium.exe");
                if (key?.GetValue(null) is string regPath && File.Exists(regPath))
                {
                    return regPath;
                }
            }
            catch
            {
                // Ignore registry permission issues
            }
        }

        return null;
    }

    /// <summary>
    /// Launches Stellarium in windowed mode (--full-screen no).
    /// </summary>
    public static bool LaunchWindowed(string? customPath = null)
    {
        string? exe = customPath ?? FindStellariumExecutable();
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--full-screen no",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
