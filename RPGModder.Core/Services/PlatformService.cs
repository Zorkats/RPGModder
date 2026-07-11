using System.Diagnostics;

namespace RPGModder.Core.Services;

public static class PlatformService
{
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string? FindExecutable(string name)
    {
        if (Path.IsPathRooted(name))
            return File.Exists(name) ? name : null;

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        IEnumerable<string> names = OperatingSystem.IsWindows()
            ? new[] { name, name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe" }
            : new[] { name };

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string candidateName in names)
            {
                string candidate = Path.Combine(directory.Trim(), candidateName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    public static bool Run(string fileName, IEnumerable<string> arguments, string? standardInput = null,
        int timeoutMilliseconds = 10_000, bool requireSuccess = true)
    {
        using var process = CreateProcess(fileName, arguments, redirectOutput: false, standardInput != null);
        if (!process.Start())
            return false;

        if (standardInput != null)
        {
            process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
        }

        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try { process.Kill(true); } catch { }
            return false;
        }

        return !requireSuccess || process.ExitCode == 0;
    }

    public static string? Capture(string fileName, IEnumerable<string> arguments, int timeoutMilliseconds = 10_000)
    {
        using var process = CreateProcess(fileName, arguments, redirectOutput: true, redirectInput: false);
        if (!process.Start())
            return null;

        string output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try { process.Kill(true); } catch { }
            return null;
        }

        return process.ExitCode == 0 ? output.Trim() : null;
    }

    private static Process CreateProcess(string fileName, IEnumerable<string> arguments,
        bool redirectOutput, bool redirectInput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            RedirectStandardInput = redirectInput
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return new Process { StartInfo = startInfo };
    }
}
