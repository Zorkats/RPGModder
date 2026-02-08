using Avalonia;
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace RPGModder.UI;

class Program
{
    private const string MutexName = "RPGModder_SingleInstance_Mutex";
    private const string PipeName = "RPGModder_IPC_Pipe";
    private static Mutex? _mutex;

    // Event that MainWindow subscribes to for receiving NXM URLs from other instances.
    public static event Action<string>? NxmUrlReceived;

    // Initial NXM URL passed via command line on first launch.
    public static string? InitialNxmUrl { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // Try to create/acquire the mutex — only one instance allowed
        _mutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // Another instance is already running — forward the NXM URL via pipe and exit
            if (args.Length > 0 && !string.IsNullOrEmpty(args[0]))
            {
                SendToExistingInstance(args[0]);
            }
            return;
        }

        try
        {
            // Start the IPC listener so we can receive URLs from future instances
            _ = StartIpcListenerAsync();

            // Capture the initial NXM URL if launched from a protocol handler
            if (args.Length > 0 && args[0].StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
            {
                InitialNxmUrl = args[0];
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
        }
        finally
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
    }

    private static void SendToExistingInstance(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000); // 3-second timeout

            using var writer = new StreamWriter(client);
            writer.WriteLine(message);
            writer.Flush();
        }
        catch
        {
            // If we can't connect, the other instance might be closing — just exit
        }
    }

    private static async Task StartIpcListenerAsync()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                await server.WaitForConnectionAsync();

                using var reader = new StreamReader(server);
                var message = await reader.ReadLineAsync();

                if (!string.IsNullOrEmpty(message))
                {
                    NxmUrlReceived?.Invoke(message);
                }
            }
            catch
            {
                // Pipe error — wait a bit and retry
                await Task.Delay(100);
            }
        }
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            string crashDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RPGModder");
            if (!Directory.Exists(crashDir)) Directory.CreateDirectory(crashDir);

            string crashFile = Path.Combine(crashDir, "crash.log");
            string report =
                "=== RPGModder Crash Report ===\n" +
                $"Time: {DateTime.Now}\n" +
                $"Version: {System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version}\n" +
                "Please report this issue on GitHub or Nexus Mods.\n\n" +
                "ERROR DETAILS:\n" +
                "--------------------------------------------------\n" +
                $"{ex}";

            File.WriteAllText(crashFile, report);

            new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(crashFile) { UseShellExecute = true }
            }.Start();
        }
        catch { }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
