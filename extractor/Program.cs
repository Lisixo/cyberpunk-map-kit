using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Types;

namespace CyberpunkMapExtractor;

internal static class Program
{
    public static int Main(string[] args)
    {
        return Runner.Run(args);
    }
}

internal static class Runner
{
    public static int Run(string[] args)
    {
        bool verbose = args.Contains("--verbose");

        if (args.Contains("--inspect"))
        {
            Inspector.RunAsync(verbose).GetAwaiter().GetResult();
            return 0;
        }
        if (args.Contains("--scan"))
        {
            Inspector.ScanAllSectorsAsync(verbose).GetAwaiter().GetResult();
            return 0;
        }
        if (args.Contains("--probe"))
        {
            Inspector.ProbeDetailsAsync(verbose).GetAwaiter().GetResult();
            return 0;
        }
        if (args.Contains("--probe2"))
        {
            Inspector.Probe2Async(verbose).GetAwaiter().GetResult();
            return 0;
        }
        if (args.Contains("--probe3"))
        {
            Inspector.Probe3Async(verbose).GetAwaiter().GetResult();
            return 0;
        }
        if (args.Contains("--probe-ft"))
        {
            Inspector.ProbeFastTravelAsync().GetAwaiter().GetResult();
            return 0;
        }

        string gameDir = GetArg(args, "--game") ?? ExtractorRunner.DefaultGameDir;
        string defaultOut = Path.GetFullPath(Path.Combine(
            Directory.Exists(@"Q:\Projects\cyberpunk-map") ? @"Q:\Projects\cyberpunk-map" : Environment.CurrentDirectory,
            "output"));
        string outDir = GetArg(args, "--out") ?? defaultOut;

        return ExtractorRunner.RunExtractAsync(gameDir, outDir, verbose).GetAwaiter().GetResult();
    }

    private static string? GetArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
