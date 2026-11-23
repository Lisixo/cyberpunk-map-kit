using WolvenKit.Common;
using WolvenKit.Core.Interfaces;

namespace CyberpunkMapExtractor;

internal sealed class ConsoleLogger : ILoggerService
{
    public bool Verbose { get; set; }
    public LoggerVerbosity LoggerVerbosity { get; set; } = LoggerVerbosity.Normal;

    public ConsoleLogger(bool verbose = false)
    {
        Verbose = verbose;
    }

    public void SetLoggerVerbosity(LoggerVerbosity verbosity) => LoggerVerbosity = verbosity;

    public void Info(string s)
    {
        if (Verbose) Console.WriteLine($"[INFO] {s}");
    }
    public void Info(int id, string s)
    {
        if (Verbose) Console.WriteLine($"[INFO:{id}] {s}");
    }
    public void Warning(string s)
    {
        if (Verbose) Console.WriteLine($"[WARN] {s}");
    }
    public void Warning(int id, string s)
    {
        if (Verbose) Console.WriteLine($"[WARN:{id}] {s}");
    }
    public int ErrorCount { get; private set; }

    public void Error(string msg)
    {
        ErrorCount++;
        if (Verbose) Console.Error.WriteLine($"[ERR] {msg}");
    }
    public void Error(int id, string s)
    {
        ErrorCount++;
        if (Verbose) Console.Error.WriteLine($"[ERR:{id}] {s}");
    }
    public void Error(Exception exception)
    {
        ErrorCount++;
        if (Verbose) Console.Error.WriteLine($"[EXC] {exception}");
    }
    public void Error(int id, Exception exception)
    {
        ErrorCount++;
        if (Verbose) Console.Error.WriteLine($"[EXC:{id}] {exception}");
    }
    public void Success(string msg) => Console.WriteLine($"[OK] {msg}");
    public void Success(int id, string s) => Console.WriteLine($"[OK:{id}] {s}");
    public void Debug(string msg)
    {
        if (Verbose) Console.WriteLine($"[DBG] {msg}");
    }
    public void Debug(int id, string msg)
    {
        if (Verbose) Console.WriteLine($"[DBG:{id}] {msg}");
    }
}
