using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using Bastion.App.Services;
using Bastion.App.Services.Demo;
using Bastion.App.Shell;
using Bastion.App.ViewModels;
using Bastion.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Bastion.App;

/// <summary>
/// The composition root. It builds the service graph, installs the crash handlers that zero keys
/// before anything else happens, applies process hygiene, and opens the shell window.
/// </summary>
/// <remarks>
/// Command line: a single <c>.bastion</c> path is opened at start-up; <c>--demo</c> swaps
/// Bastion.Core's factory for an in-memory fake so the whole UI can be driven and screenshotted
/// without a real vault. Two test hooks exist in DEBUG builds only and are compiled out of
/// Release builds: <c>--test-pick-&lt;picker&gt;=&lt;path&gt;</c> answers a file picker from the
/// command line instead of opening an OS dialog (see <see cref="ScriptedFileDialogService"/>),
/// and <c>--trace-bindings=&lt;file&gt;</c> writes WPF's binding warnings to a file. Both are
/// documented in docs/DEVELOPING.md. The <c>.bastion</c> file association is only ever
/// registered from the Settings dialog, never here.
/// </remarks>
public partial class App : Application
{
    private ServiceProvider? _services;
    private ShellViewModel? _shell;
    private ThemeController? _theme;
    private ILog? _log;
    private FileLog? _fileLog;

    /// <summary>True when the process was started with <c>--demo</c>.</summary>
    public bool IsDemo { get; private set; }

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        PinCulture();
        TraceBindings(e?.Args ?? []);

        (string? vaultPath, bool demo) = ParseCommandLine(e?.Args ?? []);
        IsDemo = demo;

        _fileLog = new FileLog();
        _log = _fileLog;
        _log.Info(demo ? "Starting in demo mode." : "Starting.");

        InstallCrashHandlers();

        IReadOnlyDictionary<string, string> scriptedPickers = ScriptedPickersFromCommandLine(e?.Args ?? []);
        if (scriptedPickers.Count > 0)
        {
            _log.Warn($"{scriptedPickers.Count} file picker(s) are answered from the command line (test mode).");
        }

        _services = BuildServices(demo, _fileLog, scriptedPickers);

        var settings = _services.GetRequiredService<ISettingsService>();
        _theme = _services.GetRequiredService<ThemeController>();
        _theme.Apply();

        _services.GetRequiredService<IShellIntegration>().ApplyProcessHygiene();

        _shell = _services.GetRequiredService<ShellViewModel>();

        var window = _services.GetRequiredService<ShellWindow>();
        _services.GetRequiredService<ShellWindowAccessor>().Window = window;
        ((DialogService)_services.GetRequiredService<IDialogService>()).Host = window.DialogHost;

        MainWindow = window;
        window.Show();

        _services.GetRequiredService<IScreenPrivacy>().SetExcludeFromCapture(
            settings.Current.ExcludeFromScreenCapture);

        Observe(_shell.StartupAsync(vaultPath), "Start-up");
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.ZeroKeys();
        _shell?.Dispose();
        _theme?.Dispose();
        _services?.Dispose();
        _fileLog?.Dispose();
        base.OnExit(e);
    }

    private static (string? VaultPath, bool Demo) ParseCommandLine(string[] args)
    {
        string? path = null;
        bool demo = false;

        foreach (string arg in args)
        {
            if (string.Equals(arg, "--demo", StringComparison.OrdinalIgnoreCase))
            {
                demo = true;
            }
            else if (!arg.StartsWith('-'))
            {
                path = arg;
            }
        }

        return (path, demo);
    }

    /// <summary>
    /// Test hook (DEBUG builds only): <c>--test-pick-&lt;picker&gt;=&lt;path&gt;</c> answers a file
    /// picker from the command line. Release builds always return an empty map, so the flag has no
    /// effect in a shipped executable.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    private static IReadOnlyDictionary<string, string> ScriptedPickersFromCommandLine(string[] args)
    {
#if DEBUG
        return ScriptedFileDialogService.ParseAnswers(args);
#else
        _ = args;
        return new Dictionary<string, string>();
#endif
    }

    /// <summary>
    /// Test hook (DEBUG builds only): <c>--trace-bindings=&lt;file&gt;</c> routes WPF's data-binding
    /// trace at Warning level into a text file, so an automated run can assert that the shell produced
    /// no binding errors. Off unless the argument is given - the listener costs a string format per
    /// binding failure. Compiled out of Release builds.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    private static void TraceBindings(string[] args)
    {
#if !DEBUG
        _ = args;
        return;
#else
        const string Prefix = "--trace-bindings=";

        string? path = args
            .FirstOrDefault(a => a.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))?[Prefix.Length..]
            .Trim('"');

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var listener = new TextWriterTraceListener(path) { TraceOutputOptions = TraceOptions.None };

        PresentationTraceSources.Refresh();
        foreach (TraceSource source in new[]
                 {
                     PresentationTraceSources.DataBindingSource,
                     PresentationTraceSources.ResourceDictionarySource,
                     PresentationTraceSources.MarkupSource,
                     PresentationTraceSources.DependencyPropertySource,
                 })
        {
            source.Listeners.Add(listener);
            source.Switch.Level = SourceLevels.Warning;
        }

        Trace.AutoFlush = true;

        // A line of its own, so an empty run is provably "no warnings" and not "the listener
        // never attached".
        listener.WriteLine(
            $"Bastion binding trace, {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}. Warnings, if any, follow.");
        listener.Flush();
#endif
    }

    /// <summary>
    /// The interface is English, so every number in it is English too. Without this the same status
    /// bar mixed "2,3 s" and "42,2 MB/s" from the OS culture into English sentences.
    /// </summary>
    private static void PinCulture()
    {
        CultureInfo culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        // XAML formats through FrameworkElement.Language, which otherwise follows the OS.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }

    private static ServiceProvider BuildServices(
        bool demo, ILog log, IReadOnlyDictionary<string, string> scriptedPickers)
    {
        var services = new ServiceCollection();

        services.AddSingleton(log);
        services.AddSingleton<IUiDispatcher>(_ => new UiDispatcher(Current.Dispatcher));
        services.AddSingleton<ShellWindowAccessor>();

        services.AddSingleton<ISettingsService>(sp => new JsonSettingsService(sp.GetRequiredService<ILog>()));
        services.AddSingleton<IRecentVaults, RecentVaultsService>();
        services.AddSingleton<IRollbackGuard>(sp => new RollbackGuard(sp.GetRequiredService<ILog>()));
        services.AddSingleton<IInternalClipboard, InternalClipboard>();
        services.AddSingleton<IOsClipboard>(sp => new OsClipboard(sp.GetRequiredService<ILog>()));
        services.AddSingleton<IFileDialogService>(sp =>
        {
            var real = new FileDialogService(sp.GetRequiredService<ShellWindowAccessor>());
            return scriptedPickers.Count == 0
                ? real
                : new ScriptedFileDialogService(real, scriptedPickers, sp.GetRequiredService<ILog>());
        });
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<IIdleMonitor, Win32IdleMonitor>();
        services.AddSingleton<ISystemEvents, SystemEventsService>();
        services.AddSingleton<IAutoLockController, AutoLockController>();
        services.AddSingleton<IShellIntegration>(sp => new ShellIntegration(sp.GetRequiredService<ILog>()));
        services.AddSingleton<IScreenPrivacy>(sp => new ScreenPrivacy(
            sp.GetRequiredService<ShellWindowAccessor>(), sp.GetRequiredService<ILog>()));
        services.AddSingleton<ISingleInstance>(sp => new SingleInstance(
            () => Current.Dispatcher.BeginInvoke(() => Current.MainWindow?.Activate()),
            sp.GetRequiredService<ILog>()));
        services.AddSingleton<IKdfEstimator>(sp => new KdfEstimator(sp.GetRequiredService<ILog>()));
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton(sp => new ThemeController(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<ILog>()));

        if (demo)
        {
            // The demo host holds no key material: the binder yields no passphrase and the fake
            // session ignores the argument, which is exactly what makes the UI runnable without
            // a finished Bastion.Core.
            Services.PasswordBoxBinder.Factory = static (_, _) => null;
            services.AddSingleton<IVaultFactory>(sp => new FakeVaultFactory(sp.GetRequiredService<ILog>()));
        }
        else
        {
            services.AddSingleton<IVaultFactory>(_ => new VaultFactory());
        }

        services.AddSingleton<OperationViewModel>();
        services.AddSingleton<Func<IVaultSession, ExplorerViewModel>>(sp => session => new ExplorerViewModel(
            session,
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IFileDialogService>(),
            sp.GetRequiredService<IInternalClipboard>(),
            sp.GetRequiredService<IOsClipboard>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<ILog>(),
            sp.GetRequiredService<OperationViewModel>()));

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ShellWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
    }

    private void InstallCrashHandlers()
    {
        // Rule: write the log line first, synchronously, then zero the keys, then talk to the user.
        // The keys still go before any UI, so a dialog that never appears costs nothing; the log line
        // now goes before even that, because a process that dies inside its own crash handler - the
        // message box throws, the shutdown races the write - must not take the only account of why it
        // died with it. FileLog swallows everything, so none of these calls can throw.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled exception on the UI thread.", e.Exception);
        _shell?.ZeroKeys();

        MessageBoxResult answer;
        try
        {
            answer = MessageBox.Show(
                "Bastion hit an unexpected error and has zeroed the vault keys.\n\n"
                + "Continue only to save your work somewhere safe; then restart.\n\n"
                + e.Exception.GetType().Name + ": " + e.Exception.Message,
                "Bastion",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No window station, a message pump already tearing down, a second failure inside the
            // dialog: whatever it was, the crash is already on disk and the process now leaves.
            _log?.Error("The crash message could not be shown.", ex);
            answer = MessageBoxResult.Cancel;
        }

        e.Handled = answer == MessageBoxResult.OK;
        if (!e.Handled)
        {
            _log?.Error("Exiting after an unhandled exception on the UI thread.");
            Shutdown(1);
        }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _log?.Error(
            e.IsTerminating
                ? "Unhandled exception on a background thread; the process is terminating."
                : "Unhandled exception on a background thread.",
            e.ExceptionObject as Exception);
        _shell?.ZeroKeys();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Warn("A task faulted with nobody watching.", e.Exception);
        e.SetObserved();
    }

    /// <summary>
    /// Watches a task nobody awaits. Without this a fault on a fire-and-forget path is only ever
    /// reported by <see cref="TaskScheduler.UnobservedTaskException"/>, which runs on a finalizer
    /// after a collection that may never happen before the process exits - so the run can end with
    /// nothing in the log at all.
    /// </summary>
    /// <param name="task">The task to watch.</param>
    /// <param name="what">What was being done, for the log line.</param>
    private void Observe(Task task, string what) => _ = task.ContinueWith(
        faulted => _log?.Error($"{what} failed.", faulted.Exception),
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}
