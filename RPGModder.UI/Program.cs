using Avalonia;
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RPGModder.Core.Services;

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
        if (args.Contains("--platform-diagnostics", StringComparer.OrdinalIgnoreCase))
        {
            WritePlatformDiagnostics();
            return;
        }

        if (args.Contains("--platform-self-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = RunPlatformSelfTest();
            return;
        }

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

    private static void WritePlatformDiagnostics()
    {
        var credentials = new CredentialStoreService();
        var steam = new SteamLaunchService();
        Console.WriteLine($"OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine($"Architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Executable={Environment.ProcessPath}");
        Console.WriteLine($"DataHome={SettingsService.GetSettingsFolder()}");
        Console.WriteLine($"Display={Environment.GetEnvironmentVariable("DISPLAY") ?? "<unset>"}");
        Console.WriteLine($"WaylandDisplay={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "<unset>"}");
        Console.WriteLine($"SecretServiceAvailable={credentials.IsAvailable}");
        Console.WriteLine($"XdgMimeAvailable={PlatformService.FindExecutable("xdg-mime") != null}");
        Console.WriteLine($"SteamInstalled={steam.IsSteamInstalled()}");
        Console.WriteLine($"SteamRoots={string.Join(Path.PathSeparator, SteamLibraryLocator.GetSteamRoots())}");
    }

    private static int RunPlatformSelfTest()
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("PLATFORM_SELF_TEST=FAIL");
            Console.Error.WriteLine("The platform self-test must run on Linux.");
            return 1;
        }

        string root = Path.Combine(Path.GetTempPath(), $"rpgmodder-platform-test-{Guid.NewGuid():N}");
        string? oldDataHome = Environment.GetEnvironmentVariable("RPGMODDER_DATA_HOME");
        string? oldXdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? oldXdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        try
        {
            Directory.CreateDirectory(root);
            string gameRoot = Path.Combine(root, "NativeGame");
            Directory.CreateDirectory(Path.Combine(gameRoot, "js"));
            string launchMarker = Path.Combine(root, "launched");
            string executable = Path.Combine(gameRoot, "Game");
            File.WriteAllText(executable, $"#!/bin/sh\nprintf launched > '{launchMarker.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'\n");
            File.SetUnixFileMode(executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(gameRoot, "js", "rmmz_core.js"), "self-test");

            DetectedGame game = new GameDetectorService().DetectGameAt(gameRoot)
                ?? throw new InvalidOperationException("Native RPG Maker game detection failed.");
            if (game.Engine != RpgMakerEngine.MZ || game.ExePath != executable)
                throw new InvalidOperationException("Native game metadata was incorrect.");

            if (!new SteamLaunchService().LaunchGame(executable, gameRoot, game.Name, preferSteam: false))
                throw new InvalidOperationException("Native game launch failed.");
            if (!SpinWait.SpinUntil(() => File.Exists(launchMarker), TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("Native game process did not execute.");

            NxmLink? link = NxmProtocolHandler.ParseNxmUrl(
                "nxm://lookoutside/mods/42/files/99?key=abc&expires=123&user_id=7");
            if (link is not { IsValid: true, HasDownloadParams: true, GameDomain: "lookoutside" })
                throw new InvalidOperationException("Canonical nxm URL parsing failed.");

            string settingsHome = Path.Combine(root, "config");
            Environment.SetEnvironmentVariable("RPGMODDER_DATA_HOME", settingsHome);
            var settings = new SettingsService(settingsHome);
            settings.Settings.AutoScanOnStartup = true;
            settings.Save();
            AssertUserOnlyMode(settingsHome, directory: true);
            AssertUserOnlyMode(Path.Combine(settingsHome, "settings.json"), directory: false);

            string xdgRoot = Path.Combine(root, "xdg");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(xdgRoot, "data"));
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(xdgRoot, "config"));
            Directory.CreateDirectory(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")!);
            string currentExecutable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Application executable path is unavailable.");
            if (!NxmProtocolHandler.RegisterProtocol(currentExecutable) || !NxmProtocolHandler.IsProtocolRegistered())
                throw new InvalidOperationException("Isolated XDG nxm registration failed.");
            if (!NxmProtocolHandler.UnregisterProtocol())
                throw new InvalidOperationException("Isolated XDG nxm unregistration failed.");
            if (NxmProtocolHandler.IsProtocolRegistered())
                throw new InvalidOperationException("Isolated XDG nxm association remained after unregistration.");

            Console.WriteLine("PLATFORM_SELF_TEST=PASS");
            Console.WriteLine($"DetectedGame={game.Name}:{game.Engine}:{game.ExePath}");
            Console.WriteLine("NativeLaunch=PASS");
            Console.WriteLine("SettingsPermissions=PASS");
            Console.WriteLine("NxmParsing=PASS");
            Console.WriteLine("NxmRegistration=PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PLATFORM_SELF_TEST=FAIL");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("RPGMODDER_DATA_HOME", oldDataHome);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldXdgDataHome);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldXdgConfigHome);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static void AssertUserOnlyMode(string path, bool directory)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException();

        UnixFileMode mode = File.GetUnixFileMode(path);
        UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                 UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & forbidden) != 0)
            throw new InvalidOperationException($"{path} grants group or other access ({mode}).");

        UnixFileMode required = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (directory)
            required |= UnixFileMode.UserExecute;
        if ((mode & required) != required)
            throw new InvalidOperationException($"{path} is missing required user permissions ({mode}).");
    }
}
