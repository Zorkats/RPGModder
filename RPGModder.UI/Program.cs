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
        // Try to create/acquire mutex
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        
        if (!createdNew)
        {
            // Another instance is running - send args via pipe and exit
            if (args.Length > 0)
            {
                SendToExistingInstance(args[0]);
            }
            return;
        }
        
        try
        {
            // Start the IPC listener for receiving URLs from other instances
            _ = StartIpcListenerAsync();
            
            // Store args for initial processing
            if (args.Length > 0)
            {
                InitialNxmUrl = args[0];
            }
            
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
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