using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace CyberpunkMapExtractor;

internal static class WkitLoader
{
    private static readonly string WkitDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "WolvenKit-10.0.0-beta.1"));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    public static string WolvenKitDirectory =>
        Directory.Exists(WkitDir)
            ? WkitDir
            : @"Q:\Projects\cyberpunk-map\WolvenKit-10.0.0-beta.1";

    [ModuleInitializer]
    internal static void Init()
    {
        var dir = WolvenKitDirectory;
        SetDllDirectory(dir);

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!path.Contains(dir, StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("PATH", dir + ";" + path);
        }

        AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
        {
            var candidate = Path.Combine(dir, assemblyName.Name + ".dll");
            if (File.Exists(candidate))
            {
                return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            }
            return null;
        };
    }
}
