#if UNITY_EDITOR_WIN
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEngine;

internal static class SteamBuildPackager
{
    private const string ValidateMenu = "Bees/Build/Validate Windows Build for Steam";
    private const string PackageMenu = "Bees/Build/Package Windows Build for Steam";

    [MenuItem(ValidateMenu)]
    private static void ValidateWindowsBuild()
    {
        string buildRoot = EditorUtility.OpenFolderPanel("Select the Windows build folder", string.Empty, string.Empty);
        if (string.IsNullOrEmpty(buildRoot))
        {
            return;
        }

        if (!TryValidateBuild(buildRoot, out string summary, out string error))
        {
            Debug.LogError($"Steam build validation failed: {error}");
            EditorUtility.DisplayDialog("Steam build validation failed", error, "OK");
            return;
        }

        Debug.Log($"Steam build validation passed. {summary}");
        EditorUtility.DisplayDialog("Steam build validation passed", summary, "OK");
    }

    [MenuItem(PackageMenu)]
    private static void PackageWindowsBuild()
    {
        string buildRoot = EditorUtility.OpenFolderPanel("Select the Windows build folder", string.Empty, string.Empty);
        if (string.IsNullOrEmpty(buildRoot))
        {
            return;
        }

        if (!TryValidateBuild(buildRoot, out string summary, out string error))
        {
            Debug.LogError($"Steam build validation failed: {error}");
            EditorUtility.DisplayDialog("Steam build validation failed", error, "OK");
            return;
        }

        string defaultName = new DirectoryInfo(buildRoot).Name + "-steam";
        string outputDirectory = Directory.GetParent(buildRoot)?.FullName ?? buildRoot;
        string zipPath = EditorUtility.SaveFilePanel("Save Steam upload ZIP", outputDirectory, defaultName, "zip");
        if (string.IsNullOrEmpty(zipPath))
        {
            return;
        }

        string normalizedRoot = Path.GetFullPath(buildRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedZip = Path.GetFullPath(zipPath);
        string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;

        if (normalizedZip.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            const string message = "Save the Steam ZIP outside the build folder so the archive cannot include itself.";
            Debug.LogError(message);
            EditorUtility.DisplayDialog("Invalid ZIP location", message, "OK");
            return;
        }

        try
        {
            CreateZip(normalizedRoot, normalizedZip);
            string message = $"Created {normalizedZip}\n\n{summary}";
            Debug.Log(message);
            EditorUtility.DisplayDialog("Steam package created", message, "OK");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("Steam packaging failed", exception.Message, "OK");
        }
    }

    private static bool TryValidateBuild(string buildRoot, out string summary, out string error)
    {
        summary = null;
        error = null;

        if (!Directory.Exists(buildRoot))
        {
            error = $"Build folder does not exist: {buildRoot}";
            return false;
        }

        string playerExe = FindPlayerExecutable(buildRoot);
        if (playerExe == null)
        {
            error = "Could not find the Unity player executable in the selected folder.";
            return false;
        }

        string unityPlayer = Path.Combine(buildRoot, "UnityPlayer.dll");
        if (!File.Exists(unityPlayer))
        {
            error = "UnityPlayer.dll is missing. Select/package the entire Unity Windows build folder, not only the .exe.";
            return false;
        }

        string productBaseName = Path.GetFileNameWithoutExtension(playerExe);
        string dataDirectory = Path.Combine(buildRoot, productBaseName + "_Data");
        if (!Directory.Exists(dataDirectory))
        {
            error = $"{productBaseName}_Data is missing. The Unity executable cannot run without its matching Data folder.";
            return false;
        }

        string monoDirectory = Path.Combine(dataDirectory, "MonoBleedingEdge");
        string gameAssembly = Path.Combine(buildRoot, "GameAssembly.dll");
        bool isMonoBuild = Directory.Exists(monoDirectory) && Directory.EnumerateFiles(monoDirectory, "*", SearchOption.AllDirectories).Any();
        bool isIl2CppBuild = File.Exists(gameAssembly);

        if (!isMonoBuild && !isIl2CppBuild)
        {
            error = "The build contains neither a usable <Game>_Data/MonoBleedingEdge runtime nor GameAssembly.dll. Rebuild the Windows player before uploading it to Steam.";
            return false;
        }

        bool hasSteamNativeLibrary = Directory.EnumerateFiles(buildRoot, "steam_api64.dll", SearchOption.AllDirectories).Any() ||
                                     Directory.EnumerateFiles(buildRoot, "steam_api.dll", SearchOption.AllDirectories).Any();
        if (!hasSteamNativeLibrary)
        {
            error = "The Windows build does not contain steam_api64.dll/steam_api.dll. Steamworks.NET cannot initialize from this package.";
            return false;
        }

        bool hasSteamAppId = File.Exists(Path.Combine(buildRoot, "steam_appid.txt"));
        string backend = isMonoBuild ? "Mono" : "IL2CPP";
        summary = $"Player: {Path.GetFileName(playerExe)}\nScripting backend detected: {backend}\nSteam native library: present";
        if (hasSteamAppId)
        {
            summary += "\nWarning: steam_appid.txt is present. Keep it for direct local Steam testing only; normally do not ship it in a retail Steam depot.";
        }

        return true;
    }

    private static string FindPlayerExecutable(string buildRoot)
    {
        string preferred = Path.Combine(buildRoot, PlayerSettings.productName + ".exe");
        if (File.Exists(preferred))
        {
            return preferred;
        }

        return Directory.EnumerateFiles(buildRoot, "*.exe", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => !Path.GetFileName(path).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase));
    }

    private static void CreateZip(string buildRoot, string zipPath)
    {
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        string prefix = buildRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using (FileStream zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (ZipArchive archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            foreach (string file in Directory.EnumerateFiles(buildRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = file.Substring(prefix.Length).Replace('\\', '/');
                ZipArchiveEntry entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
                using (Stream input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (Stream output = entry.Open())
                {
                    input.CopyTo(output);
                }
            }
        }
    }
}
#endif
