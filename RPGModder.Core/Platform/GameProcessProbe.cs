using System.Diagnostics;

namespace RPGModder.Core.Platform;

/// <summary>
/// Finds a running game by the process name of its executable, the same test the deploy button used
/// before the check moved into Core.
/// </summary>
public sealed class GameProcessProbe : IGameProcessProbe
{
    public bool IsRunning(string executablePath)
    {
        string processName = Path.GetFileNameWithoutExtension(executablePath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        Process[] processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }
}
