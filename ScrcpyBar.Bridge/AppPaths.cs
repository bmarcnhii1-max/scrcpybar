namespace ScrcpyBar.Bridge;

/// <summary>
/// Filesystem locations for the bridge. Mutable data (caches, logs, silence.wav) lives in
/// %LOCALAPPDATA%\ScrcpyBar so the exe can stay a truly single file wherever the user drops it,
/// including read-only install locations.
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScrcpyBar");

    public static string EnsureDataDir()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
        }
        catch
        {
        }

        return DataDir;
    }

    public static string DataSubDir(string name)
    {
        var dir = Path.Combine(EnsureDataDir(), name);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
        }

        return dir;
    }
}
