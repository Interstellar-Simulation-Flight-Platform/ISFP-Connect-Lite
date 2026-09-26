using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace ISFPConnectLite.Xlink;

/// <summary>
/// Installs the embedded ISFP_xLink plugin (zip) into &lt;XPlane&gt;/Resources/plugins/ISFP_xLink.
/// The zip contains the plugin folder contents directly (Data/, Profiles/, win_x64/...).
/// </summary>
public static class PluginInstaller
{
    public const string PluginFolderName = "ISFP_xLink";
    private const string ZipResourceName = "ISFPConnectLite.plugin.ISFP_xLink.zip";

    /// <summary>True if the folder looks like an X-Plane installation root.</summary>
    public static bool IsValidXplaneDir(string dir)
        => !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "X-Plane.exe"));

    public static string PluginTargetDir(string xplaneDir)
        => Path.Combine(xplaneDir, "Resources", "plugins", PluginFolderName);

    public static bool IsInstalled(string xplaneDir)
        => File.Exists(Path.Combine(PluginTargetDir(xplaneDir), "win_x64", $"{PluginFolderName}.xpl"));

    /// <summary>
    /// Extract the embedded plugin zip into the target folder (overwrite existing files).
    /// Returns (file count, was already installed).
    /// </summary>
    public static (int files, bool alreadyInstalled) Install(string xplaneDir)
    {
        if (!IsValidXplaneDir(xplaneDir))
            throw new ArgumentException("不是有效的 X-Plane 目录");

        string target = PluginTargetDir(xplaneDir);
        bool already = IsInstalled(xplaneDir);

        var asm = Assembly.GetExecutingAssembly();
        using var zipStream = asm.GetManifestResourceStream(ZipResourceName)
            ?? throw new InvalidOperationException("程序集中未找到嵌入的插件包");
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        Directory.CreateDirectory(target);
        int count = 0;
        foreach (var entry in archive.Entries)
        {
            string entryPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar).TrimEnd('\\', '/');
            if (entryPath.Length == 0) continue; // directory entries

            string dest = Path.GetFullPath(Path.Combine(target, entryPath));
            if (!dest.StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                throw new IOException("插件包内含有非法路径"); // zip-slip guard

            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var src = entry.Open();
            using var dst = File.Create(dest);
            src.CopyTo(dst);
            count++;
        }
        return (count, already);
    }
}
