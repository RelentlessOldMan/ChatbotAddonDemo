using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Demo.Domain;
using Microsoft.Win32;

namespace Demo.Desktop.ViewModels;

/// <summary>The "legacy" main window. The AI pieces are additive: Chat is null until opted in.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private static readonly string[] TabNames = ["Test plans", "Runs", "Spec sheets"];

    private readonly AppServices _services;
    private readonly DispatcherTimer _refreshTimer;
    private readonly FileSystemWatcher _watcher;
    private readonly HashSet<string> _knownRunIds = [];
    private TestRun? _shownRun;

    public MainViewModel(AppServices services)
    {
        _services = services;

        // Changes arrive from the assistant, the bench thread, or another program (the console tool or a
        // coding agent) writing to the same workspace. All of them just schedule a refresh from disk.
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { _refreshTimer!.Stop(); Refresh(); }, Application.Current.Dispatcher);
        _refreshTimer.Stop();
        services.Bench.Changed += (_, _) => ScheduleRefresh();
        _watcher = new FileSystemWatcher(services.Bench.Workspace) { IncludeSubdirectories = true, EnableRaisingEvents = true };
        _watcher.Created += (_, _) => ScheduleRefresh();
        _watcher.Changed += (_, _) => ScheduleRefresh();
        _watcher.Renamed += (_, _) => ScheduleRefresh();

        Refresh(initial: true);
        ApplySettings();
    }

    public string Workspace => _services.Bench.Workspace;
    public string Title => _services.IsDemo ? "TestBench 7.2 — DEMO MODE" : "TestBench 7.2";

    /// <summary>Raised when the view should show the Settings dialog.</summary>
    public event Action? SettingsRequested;

    public ObservableCollection<TestPlan> Plans { get; } = [];
    public ObservableCollection<TestRun> Runs { get; } = [];
    public ObservableCollection<SpecSheet> Specs { get; } = [];
    public ObservableCollection<Finding> Findings { get; } = [];
    public ObservableCollection<string> PlotNames { get; } = [];

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private string _benchStatus = "";
    [ObservableProperty] private bool _benchBusy;

    // ---- Plans tab ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunPlanCommand))]
    private TestPlan? _selectedPlan;

    [ObservableProperty] private string _planXml = "";
    [ObservableProperty] private string _planConditions = "";
    [ObservableProperty] private string _planEstimate = "";

    partial void OnSelectedPlanChanged(TestPlan? value)
    {
        _services.Selection.SelectedPlanId = value?.Id;
        PlanXml = value is null ? "" : _services.Bench.GetPlanXml(value.Id);
        PlanConditions = value is null ? "" : string.Join("\n", value.Conditions.Select(c => $"{c.Parameter}: {string.Join(", ", c.Values)} {c.Units}"))
            + $"\nUnits (DUTs): {value.Samples}";
        PlanEstimate = value is null ? "" : $"{value.MeasurementCount} measurements · about {Bench.EstimateDuration(value, _services.Bench.Options).TotalSeconds:0} s on the bench";
    }

    private bool CanRunPlan() => SelectedPlan is not null && !BenchBusy;

    [RelayCommand(CanExecute = nameof(CanRunPlan))]
    private void RunPlan() => Try(() => _services.Bench.StartRun(SelectedPlan!.Id));

    [RelayCommand]
    private void ImportPlan()
    {
        var dialog = new OpenFileDialog { Filter = "Test plans (*.xml)|*.xml", Title = "Import a test plan" };
        if (dialog.ShowDialog() == true) Try(() => SelectedPlan = _services.Bench.ImportPlan(dialog.FileName));
    }

    // ---- Runs tab -----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelRunCommand), nameof(WriteReportCommand), nameof(OpenRunFolderCommand))]
    private TestRun? _selectedRun;

    [ObservableProperty] private string _runVerdict = "";
    [ObservableProperty] private string _runHeadline = "";
    [ObservableProperty] private string? _selectedPlotName;
    [ObservableProperty] private ImageSource? _plotImage;

    partial void OnSelectedRunChanged(TestRun? value)
    {
        _services.Selection.SelectedRunId = value?.Id;
        ShowRunDetails(value);
    }

    partial void OnSelectedPlotNameChanged(string? value)
    {
        _services.Selection.ShownPlot = value is null ? null : $"plots/{value}.png";
        PlotImage = value is null || SelectedRun is null ? null : LoadImage(Path.Combine(_services.Bench.Files(SelectedRun.Id).PlotsFolder, value + ".png"));
    }

    private bool IsRunning() => SelectedRun?.Status == RunStatus.Running;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task CancelRunAsync()
    {
        try { await _services.Bench.CancelRunAsync(SelectedRun!.Id); }
        catch (DomainException ex) { Warn(ex.Message); }
    }

    private bool IsCompleted() => SelectedRun?.Status == RunStatus.Completed;

    [RelayCommand(CanExecute = nameof(IsCompleted))]
    private void WriteReport() => Try(() => Process.Start(new ProcessStartInfo(_services.Bench.GenerateReport(SelectedRun!.Id)) { UseShellExecute = true }));

    private bool HasRun() => SelectedRun is not null;

    [RelayCommand(CanExecute = nameof(HasRun))]
    private void OpenRunFolder() => Process.Start(new ProcessStartInfo(_services.Bench.Files(SelectedRun!.Id).Folder) { UseShellExecute = true });

    // ---- Spec sheets tab ------------------------------------------------------------------------

    [ObservableProperty] private SpecSheet? _selectedSpec;

    // ---- Assistant ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatVisible), nameof(IsChatAvailable))]
    private ChatViewModel? _chat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatVisible))]
    private bool _isChatOpen = true;

    public bool IsChatAvailable => Chat is not null;
    public bool IsChatVisible => Chat is not null && IsChatOpen;

    partial void OnSelectedTabIndexChanged(int value) => _services.Selection.ActiveTab = TabNames[Math.Clamp(value, 0, TabNames.Length - 1)];

    [RelayCommand]
    private void ToggleChat() => IsChatOpen = !IsChatOpen;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    /// <summary>Re-reads settings after the Settings dialog closes: shows or hides the assistant.</summary>
    public void ApplySettings()
    {
        var settings = _services.Settings.Load();
        Chat = settings.ChatEnabled ? new ChatViewModel(_services, settings, OpenSettings) : null;
        IsChatOpen = true;
    }

    // ---- Refresh --------------------------------------------------------------------------------

    private void ScheduleRefresh() => Application.Current?.Dispatcher.BeginInvoke(() => { if (!_refreshTimer.IsEnabled) _refreshTimer.Start(); });

    private void Refresh(bool initial = false)
    {
        var bench = _services.Bench;
        var planId = SelectedPlan?.Id;
        var runId = SelectedRun?.Id;
        var specName = SelectedSpec?.FileName;

        Replace(Plans, bench.ListPlans(), p => p.Id);      // plans never change once saved
        Replace(Runs, bench.ListRuns(), r => r);           // runs change as they progress
        Replace(Specs, bench.ListSpecs(), s => s.FileName);

        // A run that just started (from the UI, the assistant or the console tool): bring it into view.
        var started = Runs.LastOrDefault(r => r.Status == RunStatus.Running && !_knownRunIds.Contains(r.Id));
        foreach (var r in Runs) _knownRunIds.Add(r.Id);
        if (started is not null && !initial)
        {
            runId = started.Id;
            SelectedTabIndex = 1;
        }

        SelectedPlan = Plans.FirstOrDefault(p => p.Id == planId) ?? (initial ? Plans.FirstOrDefault() : null);
        SelectedRun = Runs.FirstOrDefault(r => r.Id == runId) ?? (initial ? Runs.LastOrDefault() : null);
        SelectedSpec = Specs.FirstOrDefault(s => s.FileName == specName) ?? Specs.FirstOrDefault();

        var busy = bench.BenchBusyWith();
        BenchBusy = busy is not null;
        BenchStatus = busy is null ? "Bench idle" : $"Bench running {busy.Id} · {busy.PercentComplete}%";
        RunPlanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Reloads findings and plots only when the run's state actually changed, so images don't flicker.</summary>
    private void ShowRunDetails(TestRun? run)
    {
        if (run == _shownRun) return;
        var sameRunSameStatus = run is not null && _shownRun is not null && run.Id == _shownRun.Id && run.Status == _shownRun.Status;
        _shownRun = run;
        RunHeadline = run is null ? "" : $"{run.Id} · {run.PlanName}\n{run.Status} · {run.Completed}/{run.Total} measurements · {run.Failed} failed";
        if (sameRunSameStatus) return;

        Findings.Clear();
        RunVerdict = "";
        var plot = SelectedPlotName;
        PlotNames.Clear();
        if (run?.Status != RunStatus.Completed) { SelectedPlotName = null; return; }

        var summary = _services.Bench.Analyze(run.Id, MeasurementFilter.All);
        RunVerdict = summary.Verdict switch { Verdict.PassWithWarnings => "PASS WITH WARNINGS", var v => v.ToString().ToUpperInvariant() };
        foreach (var f in summary.Findings) Findings.Add(f);
        foreach (var p in _services.Bench.Files(run.Id).Plots) PlotNames.Add(Path.GetFileNameWithoutExtension(p));

        // Show the plot for the worst finding by default: that's what the engineer looks at first.
        SelectedPlotName = PlotNames.Contains(plot ?? "") ? plot
            : PlotNames.FirstOrDefault(n => n == summary.Findings.FirstOrDefault()?.TestId) ?? PlotNames.FirstOrDefault();
        OnSelectedPlotNameChanged(SelectedPlotName);
    }

    private static BitmapImage? LoadImage(string path)
    {
        if (!File.Exists(path)) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // read now and release the file
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Replaces the items only if something changed, so the grids keep their scroll position and selection.</summary>
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items, Func<T, object> key)
    {
        var list = items.ToList();
        if (target.Select(key).SequenceEqual(list.Select(key))) return;
        target.Clear();
        foreach (var item in list) target.Add(item);
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (DomainException ex) { Warn(ex.Message); }
    }

    private static void Warn(string message) => MessageBox.Show(message, "TestBench", MessageBoxButton.OK, MessageBoxImage.Warning);
}
