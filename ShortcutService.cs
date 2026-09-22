using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NextCue;

public sealed class ShortcutService
{
    private const string ShortcutName = "NextCue";
    private const string ShortcutExtension = ".lnk";

    public ShortcutStatus GetShortcutStatus(ShortcutLocation location)
    {
        string shortcutPath = GetShortcutPath(location);
        bool fileExists = File.Exists(shortcutPath);

        if (!fileExists)
        {
            return new ShortcutStatus(shortcutPath, false, false);
        }

        string? targetPath = TryReadShortcutTarget(shortcutPath);
        string? executablePath = TryGetCurrentExecutablePath();
        bool isValid = !string.IsNullOrWhiteSpace(targetPath) &&
            !string.IsNullOrWhiteSpace(executablePath) &&
            File.Exists(targetPath) &&
            PathsEqual(targetPath, executablePath);

        return new ShortcutStatus(shortcutPath, true, isValid);
    }

    public ShortcutOperationResult CreateShortcut(ShortcutLocation location)
    {
        string shortcutPath = GetShortcutPath(location);
        string? executablePath = TryGetCurrentExecutablePath();

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return ShortcutOperationResult.Fail("无法定位 NextCue 程序文件。");
        }

        object? shellObject = null;
        object? shortcutObject = null;

        try
        {
            string? shortcutDirectory = Path.GetDirectoryName(shortcutPath);

            if (!string.IsNullOrWhiteSpace(shortcutDirectory))
            {
                Directory.CreateDirectory(shortcutDirectory);
            }

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");

            if (shellType is null)
            {
                return ShortcutOperationResult.Fail("无法访问 Windows 快捷方式组件。");
            }

            shellObject = Activator.CreateInstance(shellType);

            if (shellObject is null)
            {
                return ShortcutOperationResult.Fail("无法访问 Windows 快捷方式组件。");
            }

            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObject;
            shortcut.TargetPath = executablePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
            shortcut.IconLocation = $"{executablePath},0";
            shortcut.Description = "NextCue";
            shortcut.Save();

            return ShortcutOperationResult.Ok("已创建");
        }
        catch
        {
            return ShortcutOperationResult.Fail("创建桌面快捷方式失败。");
        }
        finally
        {
            ReleaseComObject(shortcutObject);
            ReleaseComObject(shellObject);
        }
    }

    public ShortcutOperationResult RemoveShortcut(ShortcutLocation location)
    {
        string shortcutPath = GetShortcutPath(location);

        try
        {
            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
            }

            return ShortcutOperationResult.Ok("已移除");
        }
        catch
        {
            return ShortcutOperationResult.Fail("移除桌面快捷方式失败。");
        }
    }

    public string GetShortcutPath(ShortcutLocation location)
    {
        string folder = location switch
        {
            ShortcutLocation.Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ShortcutLocation.StartMenu => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs"),
            _ => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        return Path.Combine(folder, $"{ShortcutName}{ShortcutExtension}");
    }

    private static string? TryReadShortcutTarget(string shortcutPath)
    {
        object? shellObject = null;
        object? shortcutObject = null;

        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");

            if (shellType is null)
            {
                return null;
            }

            shellObject = Activator.CreateInstance(shellType);

            if (shellObject is null)
            {
                return null;
            }

            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObject;
            return shortcut.TargetPath as string;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(shortcutObject);
            ReleaseComObject(shellObject);
        }
    }

    private static string? TryGetCurrentExecutablePath()
    {
        return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
    }

    private static bool PathsEqual(string firstPath, string secondPath)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(firstPath),
                Path.GetFullPath(secondPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(firstPath, secondPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}

public enum ShortcutLocation
{
    Desktop,
    StartMenu
}

public sealed record ShortcutStatus(string Path, bool FileExists, bool IsValid);

public sealed record ShortcutOperationResult(bool Success, string Message)
{
    public static ShortcutOperationResult Ok(string message)
    {
        return new ShortcutOperationResult(true, message);
    }

    public static ShortcutOperationResult Fail(string message)
    {
        return new ShortcutOperationResult(false, message);
    }
}
