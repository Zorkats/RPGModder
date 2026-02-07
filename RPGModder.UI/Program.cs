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
    
    // Event that MainWindow subscribes to for receiving NXM URLs
    public static event Action<string>? NxmUrlReceived;
    
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            string crashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RPGModder");
            if (!Directory.Exists(crashDir)) Directory.CreateDirectory(crashDir);

            string crashFile = Path.Combine(crashDir, "crash.log");

            string report = "=== RPGModder Crash Report ===\n" +
                            $"Time: {DateTime.Now}\n" +
                            $"Version: 1.0.0\n" +
                            "Please report this issue on GitHub or Nexus Mods.\n\n" +
                            "ERROR DETAILS:\n" +
                            "--------------------------------------------------\n" +
                            $"{ex}";

            File.WriteAllText(crashFile, report);

            // Try to open the log file for the user
            try
            {
                new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo(crashFile) { UseShellExecute = true }
                }.Start();
            }
            catch { }
        }
    }
    
    // Initial NXM URL passed via command line
    public static string? InitialNxmUrl { get; private set; }
    
    private static void SendToExistingInstance(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000); // 3 second timeout
            
            using var writer = new StreamWriter(client);
            writer.WriteLine(message);
            writer.Flush();
        }
        catch
        {
            // If we can't connect, just ignore - the other instance might be closing
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
                    // Invoke on the UI thread
                    NxmUrlReceived?.Invoke(message);
                }
            }
            catch
            {
                // Pipe error, wait a bit and retry
                await Task.Delay(100);
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}