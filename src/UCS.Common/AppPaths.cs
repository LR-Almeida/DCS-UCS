namespace UCS.Common;

/// <summary>
/// Resolves where configuration and profile files live. Portable first (next to the
/// executable); falls back to %APPDATA%\DCS-UCS when the program folder is read-only
/// (e.g. installed under Program Files).
/// </summary>
public static class AppPaths
{
    public static string ExeDir => AppContext.BaseDirectory;

    public static string UserDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DCS-UCS");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".ucs_write_test_{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns the path of a config file, preferring an existing file.</summary>
    public static string ConfigFile(string fileName)
    {
        var portable = Path.Combine(ExeDir, fileName);
        if (File.Exists(portable)) return portable;
        var user = Path.Combine(UserDir, fileName);
        if (File.Exists(user)) return user;
        return IsWritable(ExeDir) ? portable : user;
    }

    /// <summary>Profile search folders, highest priority first.</summary>
    public static IReadOnlyList<string> ProfileDirs()
    {
        var list = new List<string> { Path.Combine(UserDir, "profiles"), Path.Combine(ExeDir, "profiles") };
        var cwd = Path.Combine(Environment.CurrentDirectory, "profiles");
        if (!list.Contains(cwd, StringComparer.OrdinalIgnoreCase)) list.Add(cwd);
        return list;
    }

    /// <summary>Folder where new profiles should be written by default.</summary>
    public static string WritableProfileDir()
    {
        var exeProfiles = Path.Combine(ExeDir, "profiles");
        if (IsWritable(exeProfiles)) return exeProfiles;
        var user = Path.Combine(UserDir, "profiles");
        Directory.CreateDirectory(user);
        return user;
    }
}
