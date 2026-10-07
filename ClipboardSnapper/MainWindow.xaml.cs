using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Launcher = Windows.System.Launcher;

namespace ClipboardSnapper;

public sealed partial class MainWindow : Window
{
    private readonly ClipboardMonitor _monitor = new();
    private readonly ObservableCollection<HistoryItem> _history = [];
    private readonly SaveFolderPreferences _preferences = SaveFolderPreferences.ForCurrentProcess();
    private readonly NamingPreferences _namingPreferences;
    private readonly ObservableCollection<NamingPreset> _presets = [];
    private NamingState _namingState = NamingState.Default;
    private Task<NamingPreference>? _namingUpdate;
    private bool _namingReady;
    private bool _namingValid;
    private bool _updatingNaming;
    private bool _presetBusy;
    private ContentDialog? _presetDialog;
    private readonly DispatcherQueueTimer _refresh;
    private Task<FolderPreference>? _folderUpdate;
    private int _folderRevision;
    private bool _folderReady;
    private bool _starting;
    private SaveResult? _previewAttempt;
    private CancellationTokenSource? _previewCancellation;
    private ContentDialog? _detailsDialog;
    private bool _previewBusy;
    private bool _watching;
    private bool _closing;
    private bool _allowClose;
    private bool _detailsOpen;

    public MainWindow()
    {
        _namingPreferences = new NamingPreferences(_preferences.ConfigPath);
        InitializeComponent();
        UpdateQualityVisibility();
        WindowPlacement.Apply(AppWindow);
        FolderPath.Text = _preferences.DefaultFolder;
        HistoryList.ItemsSource = _history;
        PresetPicker.ItemsSource = _presets;
        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromMilliseconds(250);
        _refresh.Tick += RefreshResults;
        _refresh.Start();
        AppWindow.Closing += OnClosing;
        SetControls();
        _ = InitializeFolderAsync();
    }

    private async Task InitializeFolderAsync()
    {
        await UpdateFolderAsync(null);
        if (_closing) return;
        _folderReady = true;
        var naming = await _namingPreferences.LoadAsync();
        if (_closing) return;
        ApplyNamingState(naming.State);
        _namingReady = true;
        if (naming.Warning is not null) ShowNamingWarning(naming.Warning, _monitor.History.Generation);
        if (!_closing) SetControls();
    }

    private void ApplyNamingState(NamingState state)
    {
        _namingState = state;
        _updatingNaming = true;
        try
        {
            _presets.Clear();
            foreach (var preset in state.Presets) _presets.Add(preset);
            var selected = state.Selected;
            PresetPicker.SelectedItem = selected;
            PresetName.Text = selected?.Name ?? "";
            RuleFormula.Text = selected is null ? "" : state.Formula;
        }
        finally { _updatingNaming = false; }
        UpdateRulePreview();
    }

    private NamingState CurrentNamingState() => _namingState.Selected is null ? _namingState : _namingState with
    {
        Formula = RuleFormula.Text,
        SelectedPresetId = (PresetPicker.SelectedItem as NamingPreset)?.Id ?? ""
    };

    private Task<NamingPreference> SaveNamingAsync(NamingState state)
    {
        var previous = _namingUpdate;
        var generation = _monitor.History.Generation;
        return _namingUpdate = SaveAsync();
        async Task<NamingPreference> SaveAsync()
        {
            if (previous is not null) await previous;
            var result = await _namingPreferences.SaveAsync(state);
            if (!_closing && result.Warning is not null) ShowNamingWarning(result.Warning, generation);
            else if (!_closing && _monitor.History.IsCurrent(generation) && SettingsMessage.Title == "Filename preset settings")
            {
                SettingsMessage.IsOpen = false;
                SettingsMessage.Title = "";
                SettingsMessage.Message = "";
            }
            return result;
        }
    }

    private void ShowNamingWarning(string message, long generation)
    {
        if (!_monitor.History.IsCurrent(generation)) return;
        SettingsMessage.Title = "Filename preset settings";
        SettingsMessage.Message = message;
        SettingsMessage.Severity = InfoBarSeverity.Warning;
        SettingsMessage.IsOpen = true;
    }

    private async void Preset_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingNaming || !_namingReady || _watching || _starting || _closing ||
            PresetPicker.SelectedItem is not NamingPreset preset) return;
        RuleFormula.Text = preset.Formula;
        PresetName.Text = preset.Name;
        _namingState = CurrentNamingState();
        try { await SaveNamingAsync(_namingState); }
        catch (Exception exception) { ShowNamingWarning(exception.Message, _monitor.History.Generation); }
        if (!_closing) SetControls();
    }

    private void RuleFormula_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_updatingNaming) return;
        UpdateRulePreview();
        if (_namingReady && !_closing) SetControls();
    }

    private void UpdateRulePreview()
    {
        if (RuleFormula is null || RulePreview is null || FormatPicker is null) return;
        if (_namingState.Selected is null)
        {
            RulePreview.Text = "No profiles. Choose New Profile to create a filename rule.";
            _namingValid = false;
            return;
        }
        try
        {
            var extension = FormatPicker.SelectedIndex switch { 1 => "jpg", 2 => "bmp", _ => "png" };
            var name = FilenameRule.Parse(RuleFormula.Text).Generate(DateTimeOffset.Now, 0) + "." + extension;
            FilenameRule.ValidateName(name);
            RulePreview.Text = "Example: " + name;
            _namingValid = true;
        }
        catch (FormatException exception)
        {
            RulePreview.Text = "Invalid formula: " + exception.Message;
            _namingValid = false;
        }
    }

    private async Task<bool> ConfirmPresetAsync(string title, string message, string action)
    {
        if (_detailsOpen || _closing) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot, Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        AutomationProperties.SetAutomationId(dialog, "PresetConfirmation");
        _presetDialog = dialog;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { _presetDialog = null; }
    }

    private void NewProfile_Click(object sender, RoutedEventArgs args) => _ = ChangeProfileAsync(() => _namingState.NewProfile());

    private void DuplicateProfile_Click(object sender, RoutedEventArgs args) => _ = ChangeProfileAsync(() => _namingState.DuplicateProfile());

    private void SavePreset_Click(object sender, RoutedEventArgs args)
    {
        if (!_namingValid) return;
        _ = ChangeProfileAsync(() => _namingState.UpdateSelected(PresetName.Text, RuleFormula.Text));
    }

    private async Task ChangeProfileAsync(Func<NamingState> change)
    {
        if (_presetBusy || _closing || _watching || _starting || !_namingReady) return;
        _presetBusy = true;
        SetControls();
        var generation = _monitor.History.Generation;
        try
        {
            var state = change();
            await SaveNamingAsync(state);
            if (!_closing) ApplyNamingState(state);
        }
        catch (Exception exception) { ShowResult("Could not change profile", exception.Message, InfoBarSeverity.Error, generation); }
        finally { _presetBusy = false; if (!_closing) SetControls(); }
    }

    private async void DeletePreset_Click(object sender, RoutedEventArgs args)
    {
        if (_presetBusy || _closing || _watching || _starting || !_namingReady ||
            PresetPicker.SelectedItem is not NamingPreset preset) return;
        _presetBusy = true;
        SetControls();
        var generation = _monitor.History.Generation;
        try
        {
            if (!await ConfirmPresetAsync("Delete profile?", $"Delete '{preset.Name}'? Saved images will be kept.", "Delete") || _closing) return;
            var state = _namingState.DeleteSelected();
            await SaveNamingAsync(state);
            if (!_closing) ApplyNamingState(state);
        }
        catch (Exception exception) { ShowResult("Could not delete preset", exception.Message, InfoBarSeverity.Error, generation); }
        finally { _presetBusy = false; if (!_closing) SetControls(); }
    }

    private Task<FolderPreference> UpdateFolderAsync(string? input)
    {
        var previous = _folderUpdate;
        var revision = ++_folderRevision;
        var generation = _monitor.History.Generation;
        return _folderUpdate = ApplyAsync();

        async Task<FolderPreference> ApplyAsync()
        {
            // Serialize settings writes independently of the image-saving worker.
            if (previous is not null) await previous;
            var result = input is null ? await _preferences.LoadAsync() : await _preferences.SaveAsync(input);
            if (!_closing && revision == _folderRevision && (input is null || FolderPath.Text == input))
            {
                FolderPath.Text = result.Folder;
                if (_monitor.History.IsCurrent(generation))
                {
                    SettingsMessage.Title = result.CanUse ? "Save folder settings" : "Save folder unavailable";
                    SettingsMessage.Message = result.Warning ?? "";
                    SettingsMessage.Severity = result.CanUse ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
                    SettingsMessage.IsOpen = result.Warning is not null;
                }
            }
            return result;
        }
    }

    private async void FolderPath_LostFocus(object sender, RoutedEventArgs args)
    {
        if (!_folderReady || _closing || _starting || _watching) return;
        await UpdateFolderAsync(FolderPath.Text);
    }

    private async void Start_Click(object sender, RoutedEventArgs args)
    {
        if (_closing || _starting || _presetBusy || !_folderReady || !_namingReady || !_namingValid) return;
        var generation = _monitor.History.Generation;
        _starting = true;
        SetControls();
        try
        {
            var preference = await UpdateFolderAsync(FolderPath.Text);
            if (_closing) return;
            if (!preference.CanUse) throw new IOException("Choose a usable save folder before starting monitoring.");
            var folder = preference.Folder;
            var options = new SaveOptions(folder, (ImageFormat)FormatPicker.SelectedIndex,
                (int)Math.Round(JpegQuality.Value), RuleFormula.Text);
            _namingState = CurrentNamingState();
            await SaveNamingAsync(_namingState);
            if (_closing) return;
            await _monitor.StartAsync(options);
            FolderPath.Text = folder;
            _watching = true;
            MonitoringStatus.Text = "Monitoring";
            MonitoringHelp.Text = "Copy an image to save it automatically.";
            SetControls();
        }
        catch (Exception exception)
        {
            ShowResult("Could not start monitoring", exception.Message, InfoBarSeverity.Error, generation);
        }
        finally { _starting = false; if (!_closing) SetControls(); }
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        // Keep normal content within the viewport; horizontal scrolling is a last resort.
        RootPanel.Width = Math.Max(280, args.NewSize.Width - 64);
    }

    private async void Stop_Click(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        var generation = _monitor.History.Generation;
        StopButton.IsEnabled = false;
        try
        {
            await _monitor.StopAsync();
            _watching = false;
            MonitoringStatus.Text = "Stopped";
            MonitoringHelp.Text = "Monitoring is stopped. Images already being read or saved will finish.";
        }
        catch (Exception exception) { ShowResult("Could not stop monitoring", exception.Message, InfoBarSeverity.Error, generation); }
        SetControls();
    }

    private void SetControls()
    {
        var editable = _folderReady && _namingReady && !_watching && !_starting && !_presetBusy;
        var hasProfile = PresetPicker.SelectedItem is NamingPreset;
        StartButton.IsEnabled = editable && _namingValid;
        StopButton.IsEnabled = _watching;
        FolderPath.IsEnabled = editable;
        BrowseButton.IsEnabled = editable;
        FormatPicker.IsEnabled = editable;
        JpegQuality.IsEnabled = editable;
        PresetPicker.IsEnabled = editable;
        RuleFormula.IsEnabled = editable && hasProfile;
        PresetName.IsEnabled = editable && hasProfile;
        NewProfileButton.IsEnabled = editable;
        DuplicateProfileButton.IsEnabled = editable && hasProfile;
        SavePresetButton.IsEnabled = editable && hasProfile && _namingValid;
        DeletePresetButton.IsEnabled = editable && hasProfile;
    }

    private void Format_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        UpdateQualityVisibility();
        UpdateRulePreview();
        if (_namingReady && !_closing) SetControls();
    }

    private void UpdateQualityVisibility()
    {
        if (JpegQualityPanel is not null)
            JpegQualityPanel.Visibility = FormatPicker.SelectedIndex == (int)ImageFormat.Jpeg
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        _monitor.History.Clear();
        _previewCancellation?.Cancel();
        _previewAttempt = null;
        _history.Clear();
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Visible;
        EmptyHistory.Visibility = Visibility.Visible;
        PreviewCaption.Text = "The preview updates after an image has been saved.";
        ResultMessage.IsOpen = false;
        ResultMessage.Title = "";
        ResultMessage.Message = "";
        SettingsMessage.IsOpen = false;
        SettingsMessage.Message = "";
        SettingsMessage.Title = "";
        _detailsDialog?.Hide();
    }

    private async void Browse_Click(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        var generation = _monitor.History.Generation;
        try
        {
            var picker = new FolderPicker { CommitButtonText = "Select Folder" };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (_folderUpdate is not null) await _folderUpdate;
            if (_closing) return;
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null && !_closing)
            {
                FolderPath.Text = folder.Path;
                await UpdateFolderAsync(folder.Path);
            }
        }
        catch (Exception exception) { ShowResult("Could not choose a folder", exception.Message, InfoBarSeverity.Error, generation); }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        var generation = _monitor.History.Generation;
        try
        {
            var path = FolderPath.Text.Trim();
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Choose an absolute save folder path.");
            Directory.CreateDirectory(path);
            if (!await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(path)))
                throw new IOException("Windows could not open this folder.");
        }
        catch (Exception exception) { ShowResult("Could not open the folder", exception.Message, InfoBarSeverity.Error, generation); }
    }

    private void RefreshResults(DispatcherQueueTimer sender, object args)
    {
        for (var i = 0; i < 32 && _monitor.Results.TryRead(out var result); i++)
        {
            if (!_monitor.History.IsCurrent(result.Generation)) continue;
            _history.Insert(0, new HistoryItem(result));
            if (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
            EmptyHistory.Visibility = Visibility.Collapsed;
            ShowResult(result.Success ? "Image saved" : "Save failed",
                result.Success ? result.FilePath : result.Error,
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error, result.Generation);
        }
        var latest = _monitor.History.LatestSaved;
        if (!_previewBusy && latest is not null && latest != _previewAttempt)
        {
            _previewAttempt = latest;
            _ = RefreshPreviewAsync(latest);
        }
    }

    private async Task RefreshPreviewAsync(SaveResult result)
    {
        _previewBusy = true;
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        try
        {
            var ratio = Math.Min(1, Math.Min(1024d / result.Width, 768d / result.Height));
            var bitmap = new BitmapImage { DecodePixelWidth = (int)Math.Max(1, Math.Round(result.Width * ratio)) };
            var file = await StorageFile.GetFileFromPathAsync(result.FilePath).AsTask(cancellation.Token);
            using var stream = await file.OpenReadAsync().AsTask(cancellation.Token);
            await bitmap.SetSourceAsync(stream).AsTask(cancellation.Token);
            if (_closing || !_monitor.History.CanPreview(result)) return;
            PreviewImage.Source = bitmap;
            PreviewImage.Visibility = Visibility.Visible;
            EmptyPreview.Visibility = Visibility.Collapsed;
            PreviewCaption.Text = $"{Path.GetFileName(result.FilePath)} · {result.Width} × {result.Height} · {result.Time:HH:mm:ss}";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_closing && _monitor.History.CanPreview(result))
                PreviewCaption.Text = $"Image saved, but the preview could not be loaded: {exception.Message}";
        }
        finally { _previewCancellation = null; _previewBusy = false; }
    }

    private void ShowResult(string title, string message, InfoBarSeverity severity, long generation)
    {
        if (!_monitor.History.IsCurrent(generation)) return;
        ResultMessage.Title = title;
        ResultMessage.Message = message;
        ResultMessage.Severity = severity;
        ResultMessage.IsOpen = true;
    }

    private async void Details_Click(object sender, RoutedEventArgs args)
    {
        if (_detailsOpen || _presetBusy || _closing || ((FrameworkElement)sender).DataContext is not HistoryItem item) return;
        if (!_monitor.History.IsCurrent(item.Result.Generation)) return;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = item.Result.Success ? "Saved image" : "Save failed",
            Content = new TextBlock
            {
                Text = $"{item.Result.FilePath}\n\n{item.Result.Time:yyyy-MM-dd HH:mm:ss zzz}\n\n" +
                    (item.Result.Success ? "The image was saved successfully." : item.Result.Error),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            },
            CloseButtonText = "Close"
        };
        AutomationProperties.SetAutomationId(dialog, "FileDetails");
        _detailsOpen = true;
        _detailsDialog = dialog;
        try { await dialog.ShowAsync(); }
        catch (Exception exception) { ShowResult("Could not show file details", exception.Message, InfoBarSeverity.Error, item.Result.Generation); }
        finally { _detailsDialog = null; _detailsOpen = false; }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        RootPanel.IsHitTestVisible = false;
        MonitoringStatus.Text = "Finishing saves";
        _refresh.Stop();
        _previewCancellation?.Cancel();
        _presetDialog?.Hide();
        try
        {
            if (_folderReady && !_watching && !_starting) await UpdateFolderAsync(FolderPath.Text);
            if (_folderUpdate is not null) await _folderUpdate;
            if (_namingReady && !_watching && !_starting && !_presetBusy && (_namingValid || _namingState.Selected is null)) await SaveNamingAsync(CurrentNamingState());
            if (_namingUpdate is not null) await _namingUpdate;
            await _monitor.DisposeAsync();
        }
        finally { _allowClose = true; Close(); }
    }
}

public sealed class HistoryItem(SaveResult result)
{
    public SaveResult Result { get; } = result;
    public string Name => Path.GetExtension(Result.FilePath) is ".png" or ".jpg" or ".bmp"
        ? Path.GetFileName(Result.FilePath) : "Clipboard image";
    public string Time => Result.Time.ToString("yyyy-MM-dd HH:mm:ss");
    public string Status => Result.Success ? "Saved" : "Failed";
    public string Summary => Result.Success ? Result.FilePath : Result.Error;
    public SolidColorBrush StatusBrush => new(Result.Success ? Microsoft.UI.Colors.ForestGreen : Microsoft.UI.Colors.IndianRed);
}
