using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace KwiAutomationLauncher;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly bool _previewMode;
    private readonly bool _previewSummary;
    private readonly DispatcherTimer _displayTimer;
    private readonly Stopwatch _runStopwatch = new();
    private readonly object _logSync = new();
    private readonly object _outputSync = new();
    private readonly List<List<string>> _stepOutputs = AutomationCatalog.Steps.Select(_ => new List<string>()).ToList();
    private StreamWriter? _logWriter;
    private Process? _activeProcess;
    private bool _isRunning;
    private bool _allowClose;
    private string _elapsedText = "00:00:00";
    private string _headerStatusText = "Preparing the run...";
    private string _completedSummary = $"0 of {AutomationCatalog.Steps.Count} complete";
    private string _currentStepLabel = "Ready to begin";
    private double _progressValue;
    private string _footerStatusText = "Getting everything ready";
    private string _footerDetailText = "The first job will begin automatically.";
    private Brush _footerStatusBrush = BrushFrom("#74A9A5");
    private bool _canClose;
    private Visibility _progressVisibility = Visibility.Visible;
    private Visibility _summaryVisibility = Visibility.Collapsed;
    private Visibility _summaryToggleVisibility = Visibility.Collapsed;
    private string _viewToggleText = "View summary";
    private string _summaryCompletedValue = "0 / 11";
    private string _summaryNewValue = "—";
    private string _summaryUpdatedValue = "—";
    private string _summaryAlertsValue = "—";
    private Brush _summaryAlertsBrush = BrushFrom("#18383C");
    private string _summaryBadgeText = "RUNNING";
    private Brush _summaryBadgeBackground = BrushFrom("#E4EFED");
    private Brush _summaryBadgeForeground = BrushFrom("#347B76");

    public MainWindow(bool previewMode, bool previewSummary)
    {
        _previewMode = previewMode;
        _previewSummary = previewSummary;
        InitializeComponent();
        DataContext = this;

        Steps = new ObservableCollection<AutomationStepViewModel>(
            AutomationCatalog.Steps.Select((step, index) => new AutomationStepViewModel(index + 1, step)));
        SummaryItems = new ObservableCollection<StepSummaryViewModel>();

        _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _displayTimer.Tick += (_, _) => ElapsedText = FormatDuration(_runStopwatch.Elapsed);
    }

    public ObservableCollection<AutomationStepViewModel> Steps { get; }
    public ObservableCollection<StepSummaryViewModel> SummaryItems { get; }

    public string ElapsedText { get => _elapsedText; set => SetField(ref _elapsedText, value); }
    public string HeaderStatusText { get => _headerStatusText; set => SetField(ref _headerStatusText, value); }
    public string CompletedSummary { get => _completedSummary; set => SetField(ref _completedSummary, value); }
    public string CurrentStepLabel { get => _currentStepLabel; set => SetField(ref _currentStepLabel, value); }
    public double ProgressValue { get => _progressValue; set => SetField(ref _progressValue, value); }
    public string FooterStatusText { get => _footerStatusText; set => SetField(ref _footerStatusText, value); }
    public string FooterDetailText { get => _footerDetailText; set => SetField(ref _footerDetailText, value); }
    public Brush FooterStatusBrush { get => _footerStatusBrush; set => SetField(ref _footerStatusBrush, value); }
    public bool CanClose { get => _canClose; set => SetField(ref _canClose, value); }
    public Visibility ProgressVisibility { get => _progressVisibility; set => SetField(ref _progressVisibility, value); }
    public Visibility SummaryVisibility { get => _summaryVisibility; set => SetField(ref _summaryVisibility, value); }
    public Visibility SummaryToggleVisibility { get => _summaryToggleVisibility; set => SetField(ref _summaryToggleVisibility, value); }
    public string ViewToggleText { get => _viewToggleText; set => SetField(ref _viewToggleText, value); }
    public string SummaryCompletedValue { get => _summaryCompletedValue; set => SetField(ref _summaryCompletedValue, value); }
    public string SummaryNewValue { get => _summaryNewValue; set => SetField(ref _summaryNewValue, value); }
    public string SummaryUpdatedValue { get => _summaryUpdatedValue; set => SetField(ref _summaryUpdatedValue, value); }
    public string SummaryAlertsValue { get => _summaryAlertsValue; set => SetField(ref _summaryAlertsValue, value); }
    public Brush SummaryAlertsBrush { get => _summaryAlertsBrush; set => SetField(ref _summaryAlertsBrush, value); }
    public string SummaryBadgeText { get => _summaryBadgeText; set => SetField(ref _summaryBadgeText, value); }
    public Brush SummaryBadgeBackground { get => _summaryBadgeBackground; set => SetField(ref _summaryBadgeBackground, value); }
    public Brush SummaryBadgeForeground { get => _summaryBadgeForeground; set => SetField(ref _summaryBadgeForeground, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_previewMode)
        {
            ShowPreviewState();
            if (_previewSummary)
                ShowPreviewSummary();
            return;
        }

        await Task.Delay(450);
        await RunSequenceAsync();
    }

    private async Task RunSequenceAsync()
    {
        _isRunning = true;
        CanClose = false;
        _runStopwatch.Restart();
        _displayTimer.Start();

        try
        {
            Directory.CreateDirectory(AutomationCatalog.LogDirectory);
            var logPath = Path.Combine(AutomationCatalog.LogDirectory, $"Run-All-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
            _logWriter = new StreamWriter(logPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };

            WriteLog("KWI AUTOMATION SEQUENCE - LIVE RUN");
            WriteLog($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            if (!AutomationCatalog.TryValidate(out var validationMessage))
                throw new InvalidOperationException(validationMessage);

            WriteLog($"Preflight: {validationMessage}");
            FooterStatusText = "Live refresh in progress";
            FooterDetailText = "You can leave this window open while the jobs run.";
            FooterStatusBrush = BrushFrom("#4E9A94");

            for (var index = 0; index < AutomationCatalog.Steps.Count; index++)
            {
                var definition = AutomationCatalog.Steps[index];
                var viewModel = Steps[index];
                var stepTimer = Stopwatch.StartNew();

                viewModel.MarkRunning();
                CurrentStepLabel = $"Step {index + 1} of {AutomationCatalog.Steps.Count}";
                HeaderStatusText = $"Running {definition.Name}";
                FooterStatusText = definition.Name;
                FooterDetailText = definition.Description;
                WriteLog("");
                WriteLog($"STEP {index + 1} STARTED: {definition.Name}");

                var exitCode = await RunProcessAsync(definition, index);
                stepTimer.Stop();

                if (exitCode != 0)
                {
                    viewModel.MarkFailed(FormatDuration(stepTimer.Elapsed));
                    throw new InvalidOperationException($"{definition.Name} stopped with exit code {exitCode}.");
                }

                viewModel.MarkCompleted(FormatDuration(stepTimer.Elapsed));
                var completed = index + 1;
                ProgressValue = completed * 100d / AutomationCatalog.Steps.Count;
                CompletedSummary = $"{completed} of {AutomationCatalog.Steps.Count} complete";
                WriteLog($"STEP {completed} COMPLETED: {definition.Name} ({FormatDuration(stepTimer.Elapsed)})");
            }

            _runStopwatch.Stop();
            _displayTimer.Stop();
            ElapsedText = FormatDuration(_runStopwatch.Elapsed);
            HeaderStatusText = "Everything is up to date";
            CurrentStepLabel = "Run complete";
            FooterStatusText = "All automations completed successfully";
            FooterDetailText = $"Finished in {FormatDuration(_runStopwatch.Elapsed)}. It is safe to close this window.";
            FooterStatusBrush = BrushFrom("#3E9A73");
            WriteLog("");
            WriteLog($"ALL AUTOMATIONS COMPLETED: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            BuildAndShowSummary(null);
        }
        catch (Exception exception)
        {
            var runningStep = Steps.FirstOrDefault(step => step.State == AutomationVisualState.Running);
            runningStep?.MarkFailed(FormatDuration(_runStopwatch.Elapsed));
            _runStopwatch.Stop();
            _displayTimer.Stop();
            HeaderStatusText = "The sequence needs attention";
            CurrentStepLabel = "Run stopped";
            FooterStatusText = "The sequence stopped safely";
            FooterDetailText = exception.Message;
            FooterStatusBrush = BrushFrom("#C56A65");
            WriteLog("");
            WriteLog($"FAILED: {exception}");
            BuildAndShowSummary(exception.Message);
        }
        finally
        {
            _activeProcess = null;
            lock (_logSync)
            {
                _logWriter?.Dispose();
                _logWriter = null;
            }
            _isRunning = false;
            CanClose = true;
        }
    }

    private async Task<int> RunProcessAsync(AutomationStepDefinition step, int stepIndex)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = step.Command,
            WorkingDirectory = step.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in step.Arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        _activeProcess = process;

        if (!process.Start())
            throw new InvalidOperationException($"Windows could not start {step.Name}.");

        var outputTask = PumpOutputAsync(process.StandardOutput, "OUT", stepIndex);
        var errorTask = PumpOutputAsync(process.StandardError, "ERR", stepIndex);
        await Task.WhenAll(process.WaitForExitAsync(), outputTask, errorTask);
        return process.ExitCode;
    }

    private async Task PumpOutputAsync(StreamReader reader, string streamName, int stepIndex)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (_outputSync)
                _stepOutputs[stepIndex].Add(line);
            WriteLog($"[{streamName}] {line}");
        }
    }

    private void WriteLog(string message)
    {
        lock (_logSync)
            _logWriter?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
    }

    private void BuildAndShowSummary(string? failureMessage)
    {
        SummaryItems.Clear();
        long insertedTotal = 0;
        long updatedTotal = 0;
        long reviewItems = 0;

        for (var index = 0; index < AutomationCatalog.Steps.Count; index++)
        {
            List<string> output;
            lock (_outputSync)
                output = _stepOutputs[index].ToList();

            var visualState = Steps[index].State;
            var parsed = StepSummaryParser.Parse(AutomationCatalog.Steps[index].Name, output);

            if (visualState == AutomationVisualState.Waiting)
            {
                SummaryItems.Add(StepSummaryViewModel.Skipped(
                    AutomationCatalog.Steps[index].Name,
                    "Not run because the sequence stopped earlier."));
                reviewItems++;
                continue;
            }

            if (visualState == AutomationVisualState.Failed)
            {
                SummaryItems.Add(StepSummaryViewModel.Failed(
                    AutomationCatalog.Steps[index].Name,
                    failureMessage ?? "The program returned an unsuccessful exit code."));
                reviewItems++;
                continue;
            }

            SummaryItems.Add(StepSummaryViewModel.Completed(
                AutomationCatalog.Steps[index].Name,
                parsed.Headline,
                parsed.Detail,
                parsed.ReviewItems > 0));
            insertedTotal += parsed.InsertedRows ?? 0;
            updatedTotal += parsed.UpdatedRows ?? 0;
            reviewItems += parsed.ReviewItems;
        }

        var completedCount = Steps.Count(step => step.State == AutomationVisualState.Completed);
        var failedCount = Steps.Count(step => step.State == AutomationVisualState.Failed);
        var skippedCount = Steps.Count(step => step.State == AutomationVisualState.Waiting);
        SummaryCompletedValue = $"{completedCount} / {Steps.Count}";
        SummaryNewValue = insertedTotal.ToString("N0");
        SummaryUpdatedValue = updatedTotal.ToString("N0");
        SummaryAlertsValue = reviewItems.ToString("N0");
        SummaryAlertsBrush = reviewItems == 0 ? BrushFrom("#3E8866") : BrushFrom("#A65D43");

        if (failedCount > 0 || skippedCount > 0)
        {
            SummaryBadgeText = "INCOMPLETE — REVIEW NEEDED";
            SummaryBadgeBackground = BrushFrom("#F9E5E1");
            SummaryBadgeForeground = BrushFrom("#A6504C");
        }
        else if (reviewItems > 0)
        {
            SummaryBadgeText = "COMPLETE — REVIEW NOTED ITEMS";
            SummaryBadgeBackground = BrushFrom("#F8EEDC");
            SummaryBadgeForeground = BrushFrom("#95632F");
        }
        else
        {
            SummaryBadgeText = "COMPLETE — ALL CLEAR";
            SummaryBadgeBackground = BrushFrom("#DFF1E8");
            SummaryBadgeForeground = BrushFrom("#347A5A");
        }

        WriteLog("");
        WriteLog("RUN SUMMARY");
        WriteLog($"Jobs complete: {completedCount}/{Steps.Count}; failed: {failedCount}; not run: {skippedCount}");
        WriteLog($"New warehouse rows: {insertedTotal:N0}; updated rows: {updatedTotal:N0}; items to review: {reviewItems:N0}");
        foreach (var item in SummaryItems)
            WriteLog($"{item.Name}: {item.StatusText} - {item.Headline} {item.Detail}".Trim());

        ProgressVisibility = Visibility.Collapsed;
        SummaryVisibility = Visibility.Visible;
        SummaryToggleVisibility = Visibility.Visible;
        ViewToggleText = "View run details";
    }

    private void ShowPreviewState()
    {
        Steps[0].MarkCompleted("00:42");
        Steps[1].MarkCompleted("01:18");
        Steps[2].MarkRunning();
        ElapsedText = "00:02:19";
        HeaderStatusText = "Running Service Order Items Pull";
        CompletedSummary = $"2 of {Steps.Count} complete";
        CurrentStepLabel = $"Step 3 of {Steps.Count}";
        ProgressValue = 2d * 100 / Steps.Count;
        FooterStatusText = "Service Order Items Pull";
        FooterDetailText = "Refreshing service-order line item data.";
        FooterStatusBrush = BrushFrom("#4E9A94");
        CanClose = true;
    }

    private void ShowPreviewSummary()
    {
        lock (_outputSync)
        {
            _stepOutputs[0].AddRange(new[] { "-> Inserted rows: 18", "-> Updated rows: 42", "-> Final CSV rows: 8,614" });
            _stepOutputs[1].AddRange(new[] { "-> Inserted rows: 7", "-> Updated rows: 21", "-> Final CSV rows: 34,208" });
            _stepOutputs[2].AddRange(new[] { "-> Inserted rows: 31", "-> Updated rows: 116", "-> Final CSV rows: 91,440" });
            _stepOutputs[3].Add("Published 862,540 rows: 24 inserted, 109 updated.");
            _stepOutputs[4].AddRange(new[] { "-> Inserted rows: 2", "-> Updated rows: 6", "-> Final CSV rows: 4,921" });
            _stepOutputs[5].AddRange(new[] { "-> Inserted rows: 13", "-> Updated rows: 9", "-> Final CSV rows: 57,806" });
            _stepOutputs[6].AddRange(new[] { "Organization Accounts Checked: 84", "Names Updated: 3", "Contacts Checked: 126", "Emails Updated: 5", "Skipped - Review Only: 2", "Errors: 0", "Errors: 0" });
            _stepOutputs[7].AddRange(new[] { "Organization Accounts Checked: 84", "Websites Updated: 11", "Skipped - Already Clean: 70", "Errors: 0" });
            _stepOutputs[8].Add("Rows written: 49,412");
            _stepOutputs[9].Add("Upcoming events found: 14");
            _stepOutputs[10].AddRange(new[] { "Workbooks created: 9", "Master log rows updated: 1,284" });
        }

        foreach (var step in Steps)
            step.MarkCompleted("01:12");
        BuildAndShowSummary(null);
        HeaderStatusText = "Everything is up to date";
        CompletedSummary = $"{Steps.Count} of {Steps.Count} complete";
        ProgressValue = 100;
        FooterStatusText = "All automations completed successfully";
        FooterDetailText = "Summary preview — no automation was run.";
        FooterStatusBrush = BrushFrom("#3E9A73");
    }

    private void ToggleView_Click(object sender, RoutedEventArgs e)
    {
        var showingSummary = SummaryVisibility == Visibility.Visible;
        SummaryVisibility = showingSummary ? Visibility.Collapsed : Visibility.Visible;
        ProgressVisibility = showingSummary ? Visibility.Visible : Visibility.Collapsed;
        ViewToggleText = showingSummary ? "View summary" : "View run details";
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AutomationCatalog.LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = AutomationCatalog.LogDirectory,
            UseShellExecute = true
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isRunning || _allowClose)
            return;

        var response = MessageBox.Show(
            "An automation is still running. Closing now will stop the active job and the remaining sequence.\n\nClose anyway?",
            "Automation still running",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (response != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        try
        {
            _activeProcess?.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have ended between the prompt and this call.
        }

        _allowClose = true;
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"hh\:mm\:ss") : duration.ToString(@"mm\:ss");

    private static SolidColorBrush BrushFrom(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class AutomationStepViewModel : INotifyPropertyChanged
{
    private AutomationVisualState _state = AutomationVisualState.Waiting;
    private string _statusText = "WAITING";
    private string _durationText = "";
    private string _indicatorText;
    private Brush _cardBackground = MainWindowBrushes.WaitingCard;
    private Brush _borderBrush = MainWindowBrushes.WaitingBorder;
    private Brush _indicatorBackground = MainWindowBrushes.WaitingIndicator;
    private Brush _indicatorForeground = MainWindowBrushes.WaitingNumber;
    private Brush _statusForeground = MainWindowBrushes.WaitingStatus;

    public AutomationStepViewModel(int number, AutomationStepDefinition definition)
    {
        Number = number;
        Name = definition.Name;
        Description = definition.Description;
        _indicatorText = number.ToString();
    }

    public int Number { get; }
    public string Name { get; }
    public string Description { get; }
    public AutomationVisualState State { get => _state; private set => SetField(ref _state, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string DurationText { get => _durationText; private set => SetField(ref _durationText, value); }
    public string IndicatorText { get => _indicatorText; private set => SetField(ref _indicatorText, value); }
    public Brush CardBackground { get => _cardBackground; private set => SetField(ref _cardBackground, value); }
    public Brush BorderBrush { get => _borderBrush; private set => SetField(ref _borderBrush, value); }
    public Brush IndicatorBackground { get => _indicatorBackground; private set => SetField(ref _indicatorBackground, value); }
    public Brush IndicatorForeground { get => _indicatorForeground; private set => SetField(ref _indicatorForeground, value); }
    public Brush StatusForeground { get => _statusForeground; private set => SetField(ref _statusForeground, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void MarkRunning()
    {
        State = AutomationVisualState.Running;
        StatusText = "RUNNING";
        DurationText = "In progress";
        IndicatorText = "→";
        CardBackground = MainWindowBrushes.RunningCard;
        BorderBrush = MainWindowBrushes.RunningBorder;
        IndicatorBackground = MainWindowBrushes.RunningIndicator;
        IndicatorForeground = Brushes.White;
        StatusForeground = MainWindowBrushes.RunningStatus;
    }

    public void MarkCompleted(string duration)
    {
        State = AutomationVisualState.Completed;
        StatusText = "DONE";
        DurationText = duration;
        IndicatorText = "✓";
        CardBackground = MainWindowBrushes.CompletedCard;
        BorderBrush = MainWindowBrushes.CompletedBorder;
        IndicatorBackground = MainWindowBrushes.CompletedIndicator;
        IndicatorForeground = Brushes.White;
        StatusForeground = MainWindowBrushes.CompletedStatus;
    }

    public void MarkFailed(string duration)
    {
        State = AutomationVisualState.Failed;
        StatusText = "STOPPED";
        DurationText = duration;
        IndicatorText = "!";
        CardBackground = MainWindowBrushes.FailedCard;
        BorderBrush = MainWindowBrushes.FailedBorder;
        IndicatorBackground = MainWindowBrushes.FailedIndicator;
        IndicatorForeground = Brushes.White;
        StatusForeground = MainWindowBrushes.FailedStatus;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum AutomationVisualState
{
    Waiting,
    Running,
    Completed,
    Failed
}

public sealed class StepSummaryViewModel
{
    private StepSummaryViewModel(
        string name,
        string statusText,
        string indicatorText,
        string headline,
        string detail,
        Brush indicatorBrush,
        Brush borderBrush,
        Brush statusBrush)
    {
        Name = name;
        StatusText = statusText;
        IndicatorText = indicatorText;
        Headline = headline;
        Detail = detail;
        IndicatorBrush = indicatorBrush;
        BorderBrush = borderBrush;
        StatusBrush = statusBrush;
    }

    public string Name { get; }
    public string StatusText { get; }
    public string IndicatorText { get; }
    public string Headline { get; }
    public string Detail { get; }
    public Brush IndicatorBrush { get; }
    public Brush BorderBrush { get; }
    public Brush StatusBrush { get; }

    public static StepSummaryViewModel Completed(string name, string headline, string detail, bool needsReview) =>
        needsReview
            ? new(name, "DONE — REVIEW NOTED", "✓", headline, detail, MainWindowBrushes.WarningIndicator, MainWindowBrushes.WarningBorder, MainWindowBrushes.WarningStatus)
            : new(name, "COMPLETED", "✓", headline, detail, MainWindowBrushes.CompletedIndicator, MainWindowBrushes.CompletedBorder, MainWindowBrushes.CompletedStatus);

    public static StepSummaryViewModel Failed(string name, string detail) =>
        new(name, "FAILED", "!", "This job did not complete.", detail, MainWindowBrushes.FailedIndicator, MainWindowBrushes.FailedBorder, MainWindowBrushes.FailedStatus);

    public static StepSummaryViewModel Skipped(string name, string detail) =>
        new(name, "NOT RUN", "—", "Skipped after an earlier failure.", detail, MainWindowBrushes.WaitingNumber, MainWindowBrushes.WaitingBorder, MainWindowBrushes.WaitingStatus);
}

public sealed record ParsedStepSummary(
    string Headline,
    string Detail,
    long? InsertedRows,
    long? UpdatedRows,
    long ReviewItems);

public static class StepSummaryParser
{
    public static ParsedStepSummary Parse(string stepName, IReadOnlyList<string> lines)
    {
        var inserted = FindLast(lines,
            @"Inserted rows:\s*([\d,]+)",
            @"([\d,]+)\s+inserted");
        var updated = FindLast(lines,
            @"Updated rows:\s*([\d,]+)",
            @"([\d,]+)\s+updated");
        var finalRows = FindLast(lines,
            @"Final CSV rows:\s*([\d,]+)",
            @"Rows written:\s*([\d,]+)",
            @"Published\s+([\d,]+)\s+rows");

        if (stepName == "Exhibitors Pull")
            return PullSummary("exhibitors", inserted, updated, finalRows);
        if (stepName == "Service Orders Pull")
            return PullSummary("service orders", inserted, updated, finalRows);
        if (stepName == "Service Order Items Pull")
            return PullSummary("service-order items", inserted, updated, finalRows);
        if (stepName == "Activities Pull")
            return PullSummary("activities", inserted, updated, finalRows);
        if (stepName == "Events Pull")
            return PullSummary("events", inserted, updated, finalRows);
        if (stepName == "Notes Pull")
            return PullSummary("notes", inserted, updated, finalRows);

        if (stepName == "Accounts Pull")
        {
            var totalAccounts = FindLast(lines,
                @"Rows written:\s*([\d,]+)",
                @"Total records after de-dupe:\s*([\d,]+)");
            var headline = totalAccounts.HasValue ? $"{totalAccounts:N0} total accounts now in the warehouse" : "Account workbook rebuilt successfully";
            return new ParsedStepSummary(
                headline,
                "This full rebuild reports the current total, but does not separate new accounts from changed accounts.",
                null,
                null,
                0);
        }

        if (stepName == "Account Name, Punctuation & Email Cleanup")
        {
            var namesUpdated = FindLast(lines, @"Names Updated:\s*([\d,]+)") ?? 0;
            var emailsUpdated = FindLast(lines, @"Emails Updated:\s*([\d,]+)") ?? 0;
            var accountsChecked = FindLast(lines, @"Organization Accounts Checked:\s*([\d,]+)") ?? 0;
            var contactsChecked = FindLast(lines, @"Contacts Checked:\s*([\d,]+)") ?? 0;
            var skipped = FindSum(lines, @"Skipped\s*-\s*[^:]+:\s*([\d,]+)");
            var reviewOnly = FindSum(lines, @"Skipped\s*-\s*Review Only:\s*([\d,]+)");
            var errors = FindSum(lines, @"^\s*Errors:\s*([\d,]+)");
            return new ParsedStepSummary(
                $"{namesUpdated:N0} names and {emailsUpdated:N0} emails corrected",
                $"Checked {accountsChecked:N0} changed accounts and {contactsChecked:N0} changed contacts; {skipped:N0} skipped; {errors:N0} errors.",
                null,
                null,
                errors + reviewOnly);
        }

        if (stepName == "Website Correction Daily")
        {
            var checkedCount = FindLast(lines, @"Organization Accounts Checked:\s*([\d,]+)") ?? 0;
            var websitesUpdated = FindLast(lines, @"Websites Updated:\s*([\d,]+)") ?? 0;
            var skipped = FindSum(lines, @"Skipped\s*-\s*[^:]+:\s*([\d,]+)");
            var errors = FindSum(lines, @"^\s*Errors:\s*([\d,]+)");
            return new ParsedStepSummary(
                $"{websitesUpdated:N0} website values corrected",
                $"Checked {checkedCount:N0} changed organization accounts; {skipped:N0} skipped; {errors:N0} errors.",
                null,
                null,
                errors);
        }

        if (stepName == "Registration List Automation")
        {
            var upcomingEvents = FindLast(lines, @"Upcoming events found:\s*([\d,]+)");
            var errorsLogged = lines.Any(line => line.Contains("Errors were logged here:", StringComparison.OrdinalIgnoreCase));
            return new ParsedStepSummary(
                upcomingEvents.HasValue ? $"{upcomingEvents:N0} upcoming events evaluated" : "Registration lists rebuilt",
                errorsLogged ? "One or more event-level errors were logged; open the run log for the error workbook path." : "No event-level failures were reported by the registration-list job.",
                null,
                null,
                errorsLogged ? 1 : 0);
        }

        if (stepName == "Accounts Data Integrity Report")
        {
            var workbooks = FindLast(lines, @"Workbooks created:\s*([\d,]+)");
            var masterRows = FindLast(lines, @"Master log rows updated:\s*([\d,]+)");
            var headline = workbooks.HasValue ? $"{workbooks:N0} audit workbooks created" : "Data-integrity report created";
            var detail = masterRows.HasValue
                ? $"The master audit log now contains {masterRows:N0} rows. Review Run_Summary.xlsx for account and contact issues."
                : "Review Run_Summary.xlsx for account and contact issues.";
            return new ParsedStepSummary(headline, detail, null, null, 0);
        }

        return new ParsedStepSummary("Completed successfully", "No additional count metrics were reported by this program.", inserted, updated, 0);
    }

    private static ParsedStepSummary PullSummary(string noun, long? inserted, long? updated, long? finalRows)
    {
        var headlineParts = new List<string>();
        if (inserted.HasValue)
            headlineParts.Add($"{inserted:N0} new");
        if (updated.HasValue)
            headlineParts.Add($"{updated:N0} updated");
        if (finalRows.HasValue)
            headlineParts.Add($"{finalRows:N0} total");

        var headline = headlineParts.Count > 0
            ? string.Join(" • ", headlineParts) + $" {noun}"
            : $"{noun} refresh completed";
        var detail = inserted.HasValue || updated.HasValue
            ? "Counts are taken directly from the program's validated publication output."
            : "The program completed but did not report row-change metrics.";
        return new ParsedStepSummary(headline, detail, inserted, updated, 0);
    }

    private static long? FindLast(IReadOnlyList<string> lines, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            for (var index = lines.Count - 1; index >= 0; index--)
            {
                var match = Regex.Match(lines[index], pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success && TryParseCount(match.Groups[1].Value, out var value))
                    return value;
            }
        }
        return null;
    }

    private static long FindSum(IReadOnlyList<string> lines, string pattern)
    {
        long total = 0;
        foreach (var line in lines)
        {
            var match = Regex.Match(line, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && TryParseCount(match.Groups[1].Value, out var value))
                total += value;
        }
        return total;
    }

    private static bool TryParseCount(string text, out long value) =>
        long.TryParse(text.Replace(",", string.Empty), out value);
}

internal static class MainWindowBrushes
{
    public static readonly Brush WaitingCard = Make("#FFFFFF");
    public static readonly Brush WaitingBorder = Make("#DDE9E7");
    public static readonly Brush WaitingIndicator = Make("#EAF1F0");
    public static readonly Brush WaitingNumber = Make("#6E8586");
    public static readonly Brush WaitingStatus = Make("#829294");
    public static readonly Brush RunningCard = Make("#F0F8F7");
    public static readonly Brush RunningBorder = Make("#73AAA6");
    public static readonly Brush RunningIndicator = Make("#4E9A94");
    public static readonly Brush RunningStatus = Make("#347B76");
    public static readonly Brush CompletedCard = Make("#F3FAF7");
    public static readonly Brush CompletedBorder = Make("#B9DDCF");
    public static readonly Brush CompletedIndicator = Make("#4A9A73");
    public static readonly Brush CompletedStatus = Make("#3E8866");
    public static readonly Brush FailedCard = Make("#FFF6F5");
    public static readonly Brush FailedBorder = Make("#E7B9B5");
    public static readonly Brush FailedIndicator = Make("#C56A65");
    public static readonly Brush FailedStatus = Make("#A6504C");
    public static readonly Brush WarningIndicator = Make("#C08A45");
    public static readonly Brush WarningBorder = Make("#E7D0AC");
    public static readonly Brush WarningStatus = Make("#95632F");

    private static Brush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

public sealed record AutomationStepDefinition(
    string Name,
    string Description,
    string WorkingDirectory,
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> RequiredPaths);

public static class AutomationCatalog
{
    public const string AutomationRoot = @"C:\kwi-automations";
    public static readonly string LogDirectory = Path.Combine(AutomationRoot, "logs", "Run-All");
    private static readonly string DotNet = @"C:\Program Files\dotnet\dotnet.exe";

    public static IReadOnlyList<AutomationStepDefinition> Steps { get; } = BuildSteps();

    public static bool TryValidate(out string message)
    {
        var missing = new List<string>();

        if (!Directory.Exists(AutomationRoot))
            missing.Add(AutomationRoot);
        if (!File.Exists(DotNet))
            missing.Add(DotNet);

        foreach (var variable in new[] { "MOMENTUS_APIUSER", "MOMENTUS_SECRET", "MOMENTUS_KEY" })
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                missing.Add($"environment variable {variable}");
        }

        foreach (var step in Steps)
        {
            if (!Directory.Exists(step.WorkingDirectory))
                missing.Add(step.WorkingDirectory);
            foreach (var path in step.RequiredPaths.Where(path => !File.Exists(path)))
                missing.Add(path);
        }

        if (missing.Count > 0)
        {
            message = "Required item missing: " + string.Join("; ", missing.Distinct(StringComparer.OrdinalIgnoreCase));
            return false;
        }

        message = "All programs, folders, and credential variables are available.";
        return true;
    }

    private static IReadOnlyList<AutomationStepDefinition> BuildSteps()
    {
        var steps = new List<AutomationStepDefinition>();

        steps.Add(DotNetStep(
            "Exhibitors Pull",
            "Refreshing exhibitor records.",
            @"momentus\exhibitors-pull",
            "ExhbitorPull.csproj"));
        steps.Add(DotNetStep(
            "Service Orders Pull",
            "Refreshing service-order headers.",
            @"momentus\ServiceOrderPull",
            "ServiceOrderPull.csproj"));
        steps.Add(DotNetStep(
            "Service Order Items Pull",
            "Refreshing service-order line item data.",
            @"momentus\ServiceOrderItemsPull",
            "ServiceOrderItemsPull.csproj"));

        var activitiesFolder = Path.Combine(AutomationRoot, @"momentus\ActivitiesPull");
        var activitiesExecutable = Path.Combine(activitiesFolder, @"publish\ActivitiesPull.exe");
        steps.Add(new AutomationStepDefinition(
            "Activities Pull",
            "Applying the incremental activities refresh.",
            activitiesFolder,
            activitiesExecutable,
            new[] { "incremental" },
            new[] { activitiesExecutable }));

        steps.Add(DotNetStep(
            "Events Pull",
            "Refreshing event records.",
            @"momentus\EventsPull",
            "EventsPull.csproj"));
        steps.Add(DotNetStep(
            "Notes Pull",
            "Refreshing account and contact notes.",
            @"momentus\NotesPull",
            "NotesPull.csproj"));

        steps.Add(DotNetStep(
            "Account Name, Punctuation & Email Cleanup",
            "Applying approved account and email cleanup rules.",
            @"momentus\Account_name_punctuation_and_email_cleanup",
            "Account_name_punctuation_and_email_cleanup.csproj",
            "--apply"));
        steps.Add(DotNetStep(
            "Website Correction Daily",
            "Applying approved website corrections.",
            @"momentus\WebsiteCorrectionDaily",
            "WebsiteCorrectionDaily.csproj",
            "--apply"));
        steps.Add(DotNetStep(
            "Accounts Pull",
            "Refreshing the account warehouse file.",
            @"momentus\Accounts_Pull",
            "Accounts_Pull.csproj"));
        steps.Add(DotNetStep(
            "Registration List Automation",
            "Rebuilding the registration-list workbook.",
            @"projects\RegistrationListAutomation",
            "RegistrationListAutomation.csproj"));
        steps.Add(DotNetStep(
            "Accounts Data Integrity Report",
            "Producing the final account integrity report.",
            @"projects\AccountsDataIntegrityReport",
            "Program_Momentus_AccountExport_WithAffiliations.csproj"));

        return steps;
    }

    private static AutomationStepDefinition DotNetStep(
        string name,
        string description,
        string relativeFolder,
        string projectName,
        params string[] applicationArguments)
    {
        var folder = Path.Combine(AutomationRoot, relativeFolder);
        var projectPath = Path.Combine(folder, projectName);
        var arguments = new List<string> { "run", "--project", projectPath, "-c", "Release" };
        if (applicationArguments.Length > 0)
        {
            arguments.Add("--");
            arguments.AddRange(applicationArguments);
        }

        return new AutomationStepDefinition(
            name,
            description,
            folder,
            DotNet,
            arguments,
            new[] { projectPath });
    }
}
