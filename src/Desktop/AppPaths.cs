using System.IO;

namespace VocaLink.Desktop;

/// <summary>
/// VocaLink 的便携式目录布局。开发环境使用仓库根目录，发布环境使用程序目录。
/// </summary>
public static class AppPaths
{
    public static string RootDirectory { get; private set; } =
        AppContext.BaseDirectory;

    public static string DataDirectory =>
        Path.Combine(RootDirectory, "Data");

    public static string ChatDirectory =>
        Path.Combine(RootDirectory, "Chat");

    public static string ChatOutputDirectory =>
        Path.Combine(ChatDirectory, "Output");

    public static string SingDirectory =>
        Path.Combine(RootDirectory, "Sing");

    public static string MusicDirectory =>
        Path.Combine(SingDirectory, "Music");

    /// <summary>
    /// 用户主动生成的语音文件目录，不与对话临时语音混合保存。
    /// </summary>
    public static string VoiceDirectory =>
        Path.Combine(ChatDirectory, "Voice");

    public static string ConfigDirectory =>
        Path.Combine(RootDirectory, "config");

    public static string SingingProjectsDirectory =>
        Path.Combine(SingDirectory, "Projects");

    public static string DatabasePath =>
        Path.Combine(DataDirectory, "vocalink.db");

    public static string StartupLogPath =>
        Path.Combine(DataDirectory, "startup.log");

    public static void Initialize()
    {
        RootDirectory = FindWorkspaceRoot() ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(ChatDirectory);
        Directory.CreateDirectory(ChatOutputDirectory);
        Directory.CreateDirectory(SingDirectory);
        Directory.CreateDirectory(MusicDirectory);
        Directory.CreateDirectory(VoiceDirectory);
        Directory.CreateDirectory(SingingProjectsDirectory);
        Environment.SetEnvironmentVariable(
            "VOCALINK_ROOT",
            RootDirectory);
        MigrateLegacyDatabase();
    }

    private static string? FindWorkspaceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VocaLink.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void MigrateLegacyDatabase()
    {
        if (File.Exists(DatabasePath))
        {
            return;
        }

        var legacyDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "VocaLink");
        var legacyDatabase = Path.Combine(legacyDirectory, "vocalink.db");
        if (!File.Exists(legacyDatabase))
        {
            return;
        }

        File.Copy(legacyDatabase, DatabasePath, overwrite: false);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var legacySidecar = legacyDatabase + suffix;
            if (File.Exists(legacySidecar))
            {
                File.Copy(
                    legacySidecar,
                    DatabasePath + suffix,
                    overwrite: false);
            }
        }
    }
}
