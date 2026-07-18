using RockcliffeCourtBooker.App.Services;
using RockcliffeCourtBooker.App.ViewModels;
using RockcliffeCourtBooker.Infrastructure;
using System.Data.Common;

namespace RockcliffeCourtBooker.App;

public partial class App : Application, IDisposable
{
    private SqliteBookingRepository? repository;
    private bool disposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            var paths = AppPaths.CreateDefault();
            paths.EnsureDirectoriesExist();
            repository = new SqliteBookingRepository(paths.DatabasePath);
            await repository.InitializeAsync();

            var configurationLoader = new ExternalConfigurationLoader();
            var configurationService = new ExternalJsonConfigurationService(configurationLoader, repository);
            var workerExecutable = Path.Combine(
                AppContext.BaseDirectory,
                "worker",
                "RockcliffeCourtBooker.Worker.exe");
            var bookingService = new SqliteBookingApplicationService(
                repository,
                configurationLoader,
                paths,
                workerExecutable);

            var settings = await repository.GetSettingsAsync();
            var applicationState = new ApplicationState();
            applicationState.LoadSavedConfigurationPath(settings.ExternalConfigurationPath);

            // Reconcile the task at every launch. A failed safety check automatically turns unattended mode off.
            _ = await bookingService.SetUnattendedSubmissionEnabledAsync(settings.UnattendedSubmissionEnabled);

            var dialogs = new WindowsUserDialogService();
            var launcher = new WindowsExternalLauncher();
            var setup = new SetupViewModel(
                applicationState,
                new WindowsJsonFilePickerService(),
                configurationService,
                new PlaywrightBrowserDiagnosticsService(configurationLoader),
                bookingService);
            var newBooking = new NewBookingViewModel(applicationState, bookingService);
            var schedules = new SchedulesViewModel(applicationState, bookingService, dialogs);
            var history = new HistoryViewModel(bookingService, dialogs, launcher);
            var mainViewModel = new MainViewModel(
                applicationState,
                bookingService,
                setup,
                newBooking,
                schedules,
                history);

            var window = new MainWindow { DataContext = mainViewModel };
            MainWindow = window;
            window.Show();
            ShutdownMode = ShutdownMode.OnLastWindowClose;

            var historyArgument = FindHistoryArgument(e.Args);
            if (historyArgument.ShouldNavigate)
            {
                if (historyArgument.AttemptId is { } attemptId)
                {
                    history.SelectAttempt(attemptId);
                }

                mainViewModel.NavigateToHistory();
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException or DbException)
        {
            MessageBox.Show(
                "The local application database or Windows integration could not be initialized. No booking was attempted.\n\n" +
                exception.Message,
                "Rockcliffe Court Booker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        repository?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private static (bool ShouldNavigate, Guid? AttemptId) FindHistoryArgument(string[] arguments)
    {
        for (var index = 0; index < arguments.Length; index++)
        {
            if (!string.Equals(arguments[index], "--history", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return index + 1 < arguments.Length && Guid.TryParse(arguments[index + 1], out var attemptId)
                ? (true, attemptId)
                : (true, null);
        }

        return (false, null);
    }
}
