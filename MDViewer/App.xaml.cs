using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MDViewer.Views;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinRT.Interop;

namespace MDViewer;

public partial class App : Application
{
    // The public property the File Picker needs
    public Window? MainWindow { get; private set; }

    public App()
    {
        this.InitializeComponent();
        this.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcherQueue));

        MainWindow = new Window
        {
            Title = "MDViewer"
        };
        SetWindowIcon(MainWindow);

        // Create a Frame to act as the navigation context and navigate to the first page
        Frame rootFrame = new Frame();
        rootFrame.Navigate(typeof(MainPage));

        // Place the frame in the current Window
        MainWindow.Content = rootFrame;
        MaximizeWindow(MainWindow);
        
        // Ensure the current window is active
        MainWindow.Activate();

        if (rootFrame.Content is MainPage mainPage &&
            TryGetLaunchFilePath(args.Arguments, out string? launchFilePath) &&
            launchFilePath is not null)
        {
            _ = mainPage.OpenExternalFileAsync(launchFilePath);
        }
    }

    private static bool TryGetLaunchFilePath(string launchArguments, out string? filePath)
    {
        filePath = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(File.Exists)
            ?? ParseSingleLaunchArgument(launchArguments);

        return !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath);
    }

    private static string? ParseSingleLaunchArgument(string launchArguments)
    {
        string trimmed = launchArguments.Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length >= 2 &&
            trimmed[0] == '"' &&
            trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        return File.Exists(trimmed) ? trimmed : null;
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash("XAML unhandled exception", e.Exception);
    }

    private static void OnCurrentDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        LogCrash("AppDomain unhandled exception", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("Unobserved task exception", e.Exception);
    }

    private static void SetWindowIcon(Window window)
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

        if (!File.Exists(iconPath))
        {
            return;
        }

        AppWindow appWindow = GetAppWindow(window);
        appWindow.SetIcon(iconPath);
    }

    private static void MaximizeWindow(Window window)
    {
        AppWindow appWindow = GetAppWindow(window);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private static AppWindow GetAppWindow(Window window)
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(window);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    private static void LogCrash(string category, Exception? exception)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MDViewer");

            Directory.CreateDirectory(directory);

            string logPath = Path.Combine(directory, "crash.log");
            string details = exception?.ToString() ?? "(no managed exception object)";

            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.Now:O} [{category}]{Environment.NewLine}{details}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never introduce a second crash path.
        }
    }
}
