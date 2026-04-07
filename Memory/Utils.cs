using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

using System.Drawing;
using Console = Colorful.Console;

// ClientID and settings
using static ConfigValues;

public static class Logger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FLStudioRPC",
        "logs"
    );

    private static readonly string LogFilePath = Path.Combine(LogDir, "flrpc.log");
    private const long MaxLogSize = 512 * 1024; // 512 KB

    private static readonly object _lock = new object();

    public static void Log(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogDir);

                // Rotate if too large
                if (File.Exists(LogFilePath))
                {
                    var fileInfo = new FileInfo(LogFilePath);
                    if (fileInfo.Length > MaxLogSize)
                    {
                        string oldLog = LogFilePath + ".old";
                        if (File.Exists(oldLog)) File.Delete(oldLog);
                        File.Move(LogFilePath, oldLog);
                    }
                }

                using (var writer = new StreamWriter(LogFilePath, true))
                {
                    writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}");
                }
            }
        }
        catch
        {
            // Last resort - don't crash over logging
        }
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);

    public static void Error(string message, Exception ex)
    {
        Log("ERROR", $"{message}: {ex.Message}");
        Log("ERROR", $"  Stack trace: {ex.StackTrace}");
    }
}

public static class Utils
{
    // Win32 imports for enumerating windows
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    // State tracking for change-only logging
    private static string _lastWindowTitle = null;
    private static bool _lastProcessFound = false;
    private static bool _lastNoWindowWarned = false;

    public static string GetMainWindowsTitleByProcessNames(params string[] processNames)
    {
        // Collect all process IDs for the target process names
        var targetPids = new HashSet<uint>();
        foreach (var processName in processNames)
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(processName);
                foreach (var proc in processes)
                {
                    targetPids.Add((uint)proc.Id);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error enumerating processes for '{processName}'", ex);
            }
        }

        if (targetPids.Count == 0)
        {
            if (_lastProcessFound)
            {
                Logger.Info("FL processes no longer found");
                _lastProcessFound = false;
                _lastNoWindowWarned = false;
            }
            return null;
        }

        if (!_lastProcessFound)
        {
            Logger.Info($"FL process detected ({targetPids.Count} PID(s))");
            _lastProcessFound = true;
        }

        // Use EnumWindows to find a visible window belonging to one of these processes
        string foundTitle = null;

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd))
                return true; // continue

            GetWindowThreadProcessId(hWnd, out uint windowPid);

            if (!targetPids.Contains(windowPid))
                return true; // continue

            int length = GetWindowTextLength(hWnd);
            if (length == 0)
                return true; // continue

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            if (!string.IsNullOrEmpty(title))
            {
                foundTitle = title;
                return false; // stop enumerating
            }

            return true; // continue
        }, IntPtr.Zero);

        if (foundTitle == null)
        {
            if (!_lastNoWindowWarned)
            {
                Logger.Warn($"FL processes found ({targetPids.Count} PID(s)) but no visible window with a title");
                _lastNoWindowWarned = true;
            }
        }
        else
        {
            _lastNoWindowWarned = false;

            if (foundTitle != _lastWindowTitle)
            {
                Logger.Info($"Window title changed: '{_lastWindowTitle}' -> '{foundTitle}'");
                _lastWindowTitle = foundTitle;
            }
        }

        return foundTitle;
    }

    public static Version GetApplicationVersion(string processName)
    {
        Process[] processes = Process.GetProcessesByName(processName);

        if (processes.Length > 0)
        {
            try
            {
                string filePath = processes[0].MainModule.FileName;

                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(filePath);
                    return new Version(versionInfo.FileVersion);
                }
                else
                {
                    Logger.Warn($"GetApplicationVersion('{processName}'): filePath empty or missing ('{filePath}')");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error retrieving version information: {ex.Message}", Color.Red);
                Logger.Error($"GetApplicationVersion failed for '{processName}'", ex);
                return null;
            }
        }

        return null;
    }

    public static FLInfo GetFLInfo()
    {
        FLInfo Info = new FLInfo();

        string fullTitle = GetMainWindowsTitleByProcessNames("FL", "FL64");

        if (string.IsNullOrEmpty(fullTitle))
        {
            Info.ProjectName = null;
            Info.AppName = null;
        }
        else
        {
            if (AccurateVersion)
            {
                Version accurateVersion = GetApplicationVersion("FL64") ?? GetApplicationVersion("FL");
                Info.AppName = accurateVersion != null ? $"FL Studio {accurateVersion}" : null;
            }
            else
            {
                int hyphenIndex = fullTitle.IndexOf('-');

                Info.ProjectName = hyphenIndex == -1 ? null : fullTitle.Substring(0, hyphenIndex).Trim();
                Info.AppName = hyphenIndex == -1 ? fullTitle.Trim() : fullTitle.Substring(hyphenIndex + 1).Trim();
            }
        }

        return Info;
    }

    public struct FLInfo
    {
        public string AppName { get; set; }
        public string ProjectName { get; set; }
        public string AccurateVersion { get; set; }
    }
}