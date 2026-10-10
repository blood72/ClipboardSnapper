using System.ComponentModel;
using System.Globalization;
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
    private readonly UiText _text = (UiText)Application.Current.Resources["UiText"];
    private readonly LanguagePreferences _languagePreferences = new(PortableConfig.ExecutableConfigPath);
    private readonly ImagePreferences _imagePreferences = new(PortableConfig.ExecutableConfigPath);
    private readonly DispatcherQueueTimer _imageSave;
    private Task<ImagePreference>? _imageUpdate;
    private bool _imageReady;
    private bool _applyingImageOptions;
    private bool _imageDirty;
    private long _imageRevision;
    private long _imageGeneration;
    private Task<LanguagePreference>? _languageUpdate;
    private bool _languageReady;
    private bool _languageBusy;
    private bool _updatingLanguage;
    private string _statusKey = "Ready";
    private string? _settingsTitle;
    private UiMessage? _settingsNotice;
    private string? _resultTitle;
    private UiMessage? _resultNotice;
    private UiMessage _caption = new("PreviewEmptyCaption");
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
    private bool _stopping;
    private bool _closing;
    private bool _allowClose;
    private bool _detailsOpen;
    private QueueSnapshot? _queueSnapshot;
    private string? _queueStatusKey;
    private readonly ObservableCollection<QueueItem> _queueItems = [];

    public MainWindow()
    {
        _namingPreferences = new NamingPreferences(_preferences.ConfigPath);
        InitializeComponent();
        _imageSave = DispatcherQueue.CreateTimer();
        _imageSave.Interval = TimeSpan.FromMilliseconds(200);
        _imageSave.Tick += (_, _) => { _imageSave.Stop(); _ = FlushImageOptionsAsync(); };
        QueueList.ItemsSource = _queueItems;
        _text.LanguageChanged += LanguageChanged;
        LanguageChanged(this, EventArgs.Empty);
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
        var language = await _languagePreferences.LoadAsync();
        if (_closing) return;
        var code = _text.Catalog.Resolve(language.Code, CultureInfo.CurrentUICulture.Name);
        ApplyLanguagePicker(code);
        _languageReady = true;
        var folder = await UpdateFolderAsync(null);
        if (_closing) return;
        _folderReady = true;
        var naming = await _namingPreferences.LoadAsync();
        if (_closing) return;
        ApplyNamingState(naming.State);
        _namingReady = true;
        var image = await _imagePreferences.LoadAsync();
        if (_closing) return;
        ApplyImageOptions(image.Settings);
        _imageReady = true;
        if (naming.Notice is not null) ShowNamingWarning(naming.Notice, _monitor.History.Generation);
        var languageNotices = _text.Catalog.Notices.ToList();
        if (language.Notice is not null) languageNotices.Add(language.Notice);
        if (language.Code is not null && !language.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
            languageNotices.Add(new("LanguageUnavailable", language.Code));
        if (languageNotices.Count > 0) ShowLanguageNotices(languageNotices, _monitor.History.Generation);
        if (image.Notices.Count > 0)
        {
            var notices = image.Notices.Concat(folder.Notices).Concat(languageNotices).ToList();
            if (naming.Notice is not null) notices.Add(naming.Notice);
            ShowImageNotices(notices, _monitor.History.Generation);
        }
        if (!_closing) SetControls();
    }

    private void ApplyImageOptions(ImageSettings settings)
    {
        _applyingImageOptions = true;
        try
        {
            FormatPicker.SelectedIndex = (int)settings.Format;
            JpegQuality.Value = settings.JpegQuality;
            UpdateQualityVisibility();
        }
        finally { _applyingImageOptions = false; }
    }

    private void ScheduleImageOptionsSave(bool immediate = false)
    {
        if (!_imageReady || _applyingImageOptions || _closing || _watching || _starting || _stopping) return;
        _imageDirty = true;
        _imageRevision++;
        _imageGeneration = _monitor.History.Generation;
        _imageSave.Stop();
        if (immediate) _ = FlushImageOptionsAsync();
        else _imageSave.Start();
    }

    private Task FlushImageOptionsAsync()
    {
        _imageSave.Stop();
        if (!_imageDirty) return (Task?)_imageUpdate ?? Task.CompletedTask;
        _imageDirty = false;
        var settings = new ImageSettings((ImageFormat)FormatPicker.SelectedIndex, (int)Math.Round(JpegQuality.Value));
        var previous = _imageUpdate;
        var revision = _imageRevision;
        var generation = _imageGeneration;
        return _imageUpdate = SaveAsync();
        async Task<ImagePreference> SaveAsync()
        {
            if (previous is not null) await previous;
            var result = await _imagePreferences.SaveAsync(settings);
            if (!_closing && revision == _imageRevision && _monitor.History.IsCurrent(generation))
            {
                if (result.Notices.Count > 0) ShowImageNotices(result.Notices, generation);
                else if (_settingsTitle == "ImageOptionsSettings")
                {
                    _settingsTitle = null;
                    _settingsNotice = null;
                    SettingsMessage.IsOpen = false;
                    SettingsMessage.Title = "";
                    SettingsMessage.Message = "";
                }
            }
            return result;
        }
    }

    private void ShowImageNotices(IReadOnlyList<UiMessage> notices, long generation)
    {
        if (!_monitor.History.IsCurrent(generation)) return;
        _settingsTitle = "ImageOptionsSettings";
        _settingsNotice = new("Raw", new UiMessageList(notices));
        SettingsMessage.Title = _text[_settingsTitle];
        SettingsMessage.Message = _settingsNotice.Render(_text);
        SettingsMessage.Severity = InfoBarSeverity.Warning;
        SettingsMessage.IsOpen = notices.Count > 0;
    }

    private void ApplyLanguagePicker(string code)
    {
        _updatingLanguage = true;
        try
        {
            LanguagePicker.ItemsSource = _text.Catalog.Languages;
            LanguagePicker.SelectedItem = _text.Catalog.Languages.First(l => l.Code == code);
            _text.Select(code);
        }
        finally { _updatingLanguage = false; }
    }

    private void LanguageChanged(object? sender, EventArgs args)
    {
        RootGrid.Language = _text.Language.Code;
        MonitoringStatus.Text = _text[_statusKey];
        MonitoringHelp.Text = _text[_watching ? "MonitoringHelp" : _statusKey == "Stopped" ? "StoppedHelp" : "ReadyHelp"];
        PreviewCaption.Text = _caption.Render(_text);
        if (_settingsTitle is not null) SettingsMessage.Title = _text[_settingsTitle];
        if (_settingsNotice is not null) SettingsMessage.Message = _settingsNotice.Render(_text);
        if (_resultTitle is not null) ResultMessage.Title = _text[_resultTitle];
        if (_resultNotice is not null) ResultMessage.Message = _resultNotice.Render(_text);
        foreach (var item in _history) item.RefreshLanguage();
        RefreshQueueProgress(force: true);
        UpdateRulePreview();
    }

    private Task<LanguagePreference> SaveLanguageAsync(string code)
    {
        var previous = _languageUpdate;
        return _languageUpdate = SaveAsync();
        async Task<LanguagePreference> SaveAsync()
        {
            if (previous is not null) await previous;
            return await _languagePreferences.SaveAsync(code);
        }
    }

    private async void Language_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingLanguage || !_languageReady || _languageBusy || _closing ||
            LanguagePicker.SelectedItem is not LanguageOption language) return;
        _languageBusy = true;
        var generation = _monitor.History.Generation;
        try
        {
            // Finish the selection callback before relabeling/disabling its automation peers.
            await Task.Yield();
            if (_closing) return;
            _text.Select(language.Code);
            SetControls();
            var saved = await SaveLanguageAsync(language.Code);
            if (!_closing && saved.Notice is not null) ShowLanguageNotices([saved.Notice], generation);
        }
        finally { _languageBusy = false; if (!_closing) SetControls(); }
    }

    private void ShowLanguageNotices(IReadOnlyList<UiMessage> notices, long generation)
    {
        if (!_monitor.History.IsCurrent(generation)) return;
        _settingsTitle = "LanguageSettings";
        // Keep structured messages so an existing notice also changes with the selected language.
        _settingsNotice = new("Raw", new UiMessageList(notices));
        SettingsMessage.Title = _text[_settingsTitle];
        SettingsMessage.Message = _settingsNotice.Render(_text);
        SettingsMessage.Severity = InfoBarSeverity.Warning;
        SettingsMessage.IsOpen = notices.Count > 0;
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
            if (!_closing && result.Notice is not null) ShowNamingWarning(result.Notice, generation);
            else if (!_closing && _monitor.History.IsCurrent(generation) && _settingsTitle == "PresetSettings")
            {
                _settingsTitle = null;
                _settingsNotice = null;
                SettingsMessage.IsOpen = false;
                SettingsMessage.Title = "";
                SettingsMessage.Message = "";
            }
            return result;
        }
    }

    private void ShowNamingWarning(UiMessage message, long generation)
    {
        if (!_monitor.History.IsCurrent(generation)) return;
        _settingsTitle = "PresetSettings";
        _settingsNotice = message;
        SettingsMessage.Title = _text[_settingsTitle];
        SettingsMessage.Message = message.Render(_text);
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
        catch (Exception exception) { ShowNamingWarning(UiMessage.FromException(exception), _monitor.History.Generation); }
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
            RulePreview.Text = _text["NoProfiles"];
            _namingValid = false;
            return;
        }
        try
        {
            var extension = FormatPicker.SelectedIndex switch { 1 => "jpg", 2 => "bmp", _ => "png" };
            var name = FilenameRule.Parse(RuleFormula.Text).Generate(DateTimeOffset.Now, 0) + "." + extension;
            FilenameRule.ValidateName(name);
            RulePreview.Text = _text.Format("Example", name);
            _namingValid = true;
        }
        catch (FormatException exception)
        {
            RulePreview.Text = new UiMessage("InvalidFormula", UiMessage.FromException(exception)).Render(_text);
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
            PrimaryButtonText = action, CloseButtonText = _text["Cancel"], DefaultButton = ContentDialogButton.Close
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
        catch (Exception exception) { ShowResult("ProfileChangeFailed", UiMessage.FromException(exception), InfoBarSeverity.Error, generation); }
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
            if (!await ConfirmPresetAsync(_text["DeleteProfileTitle"], _text.Format("DeleteProfileQuestion", preset.Name), _text["Delete"]) || _closing) return;
            var state = _namingState.DeleteSelected();
            await SaveNamingAsync(state);
            if (!_closing) ApplyNamingState(state);
        }
        catch (Exception exception) { ShowResult("ProfileDeleteFailed", UiMessage.FromException(exception), InfoBarSeverity.Error, generation); }
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
                    _settingsTitle = result.CanUse ? "FolderSettings" : "FolderUnavailable";
                    _settingsNotice = new("Raw", new UiMessageList(result.Notices));
                    SettingsMessage.Title = _text[_settingsTitle];
                    SettingsMessage.Message = _settingsNotice.Render(_text);
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
            if (!preference.CanUse) throw UiMessage.Io("UsableFolder");
            var folder = preference.Folder;
            await FlushImageOptionsAsync();
            if (_closing) return;
            var options = new SaveOptions(folder, (ImageFormat)FormatPicker.SelectedIndex,
                (int)Math.Round(JpegQuality.Value), RuleFormula.Text);
            _namingState = CurrentNamingState();
            await SaveNamingAsync(_namingState);
            if (_closing) return;
            await _monitor.StartAsync(options);
            FolderPath.Text = folder;
            _watching = true;
            _statusKey = "Monitoring";
            LanguageChanged(this, EventArgs.Empty);
            SetControls();
        }
        catch (Exception exception)
        {
            ShowResult("StartFailed", UiMessage.FromException(exception), InfoBarSeverity.Error, generation);
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
        if (_closing || _stopping) return;
        var generation = _monitor.History.Generation;
        _stopping = true;
        StopButton.IsEnabled = false;
        RefreshQueueProgress(force: true);
        try
        {
            await _monitor.StopAsync();
            _watching = false;
            _statusKey = "Stopped";
            LanguageChanged(this, EventArgs.Empty);
        }
        catch (Exception exception) { ShowResult("StopFailed", UiMessage.FromException(exception), InfoBarSeverity.Error, generation); }
        finally { _stopping = false; RefreshQueueProgress(force: true); }
        SetControls();
    }

    private void SetControls()
    {
        var editable = _folderReady && _namingReady && _imageReady && !_watching && !_starting && !_presetBusy;
        var hasProfile = PresetPicker.SelectedItem is NamingPreset;
        LanguagePicker.IsEnabled = _languageReady && !_languageBusy && !_closing;
        StartButton.IsEnabled = editable && _namingValid;
        StopButton.IsEnabled = _watching && !_stopping;
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
        ScheduleImageOptionsSave(immediate: true);
        if (_namingReady && !_closing) SetControls();
    }

    private void Quality_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
        => ScheduleImageOptionsSave();

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
        RefreshQueueProgress(force: true);
        _previewCancellation?.Cancel();
        _previewAttempt = null;
        _history.Clear();
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Visible;
        EmptyHistory.Visibility = Visibility.Visible;
        _caption = new("PreviewEmptyCaption");
        PreviewCaption.Text = _caption.Render(_text);
        _settingsTitle = null;
        _settingsNotice = null;
        _resultTitle = null;
        _resultNotice = null;
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
            var picker = new FolderPicker { CommitButtonText = _text["SelectFolder"] };
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
        catch (Exception exception) { ShowResult("ChooseFolderFailed", UiMessage.FromException(exception), InfoBarSeverity.Error, generation); }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        var generation = _monitor.History.Generation;
        try
        {
            var path = FolderPath.Text.Trim();
            if (!Path.IsPathFullyQualified(path)) throw UiMessage.Argument("AbsoluteFolder");
            Directory.CreateDirectory(path);
            if (!await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(path)))
                throw UiMessage.Io("WindowsOpenFolderFailed");
        }
        catch (Exception exception) { ShowResult("OpenFolderFailed", UiMessage.FromException(exception), InfoBarSeverity.Error, generation); }
    }

    private void RefreshResults(DispatcherQueueTimer sender, object args)
    {
        RefreshQueueProgress();
        for (var i = 0; i < 32 && _monitor.Results.TryRead(out var result); i++)
        {
            if (!_monitor.History.IsCurrent(result.Generation)) continue;
            _history.Insert(0, new HistoryItem(result, _text));
            if (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
            EmptyHistory.Visibility = Visibility.Collapsed;
            ShowResult(result.Success ? "ImageSaved" : "SaveFailed",
                result.Success ? new("Raw", result.FilePath) : result.ErrorMessage ?? new("Raw", result.Error),
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error, result.Generation);
        }
        var latest = _monitor.History.LatestSaved;
        if (!_previewBusy && latest is not null && latest != _previewAttempt)
        {
            _previewAttempt = latest;
            _ = RefreshPreviewAsync(latest);
        }
    }

    private void RefreshQueueProgress(bool force = false)
    {
        var snapshot = _monitor.Progress.Snapshot(_monitor.History.Generation);
        var status = snapshot.StatusKey(_watching, _statusKey == "Stopped", _stopping);
        if (!force && snapshot == _queueSnapshot && status == _queueStatusKey) return;
        var view = _monitor.Progress.View(_monitor.History.Generation);
        snapshot = view.Snapshot;
        status = snapshot.StatusKey(_watching, _statusKey == "Stopped", _stopping);
        _queueSnapshot = snapshot;
        _queueStatusKey = status;
        QueueStatus.Text = _text[status];
        QueueActivity.Text = _text.Format("QueueActivity", snapshot.Reading, snapshot.Waiting, snapshot.Saving);
        QueueOutcomes.Text = _text.Format("QueueOutcomes", snapshot.Saved, snapshot.Failed);
        QueueFailures.Text = _text.Format("QueueFailures", snapshot.ReadFailed, snapshot.Rejected, snapshot.SaveFailed);
        QueueFailures.Visibility = snapshot.Failed > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueBusy.IsIndeterminate = snapshot.Active > 0;
        QueueBusy.Visibility = snapshot.Active > 0 ? Visibility.Visible : Visibility.Collapsed;
        var ids = view.Jobs.Select(j => j.Id).ToHashSet();
        for (var i = _queueItems.Count - 1; i >= 0; i--)
            if (!ids.Contains(_queueItems[i].Job.Id)) _queueItems.RemoveAt(i);
        for (var i = 0; i < view.Jobs.Length; i++)
        {
            var job = view.Jobs[i];
            if (i < _queueItems.Count && _queueItems[i].Job.Id == job.Id)
            { if (force || _queueItems[i].Job != job) _queueItems[i].Refresh(job); }
            else _queueItems.Insert(i, new QueueItem(job, _text));
        }
        EmptyQueueVisibility();
    }

    private void EmptyQueueVisibility() => QueueEmpty.Visibility = _queueItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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
            _caption = new("Raw", string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(result.FilePath)} · {result.Width} × {result.Height} · {result.Time:HH:mm:ss}"));
            PreviewCaption.Text = _caption.Render(_text);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_closing && _monitor.History.CanPreview(result))
            {
                _caption = new("PreviewFailed", UiMessage.FromException(exception));
                PreviewCaption.Text = _caption.Render(_text);
            }
        }
        finally { _previewCancellation = null; _previewBusy = false; }
    }

    private void ShowResult(string title, UiMessage message, InfoBarSeverity severity, long generation)
    {
        if (!_monitor.History.IsCurrent(generation)) return;
        _resultTitle = title;
        _resultNotice = message;
        ResultMessage.Title = _text[title];
        ResultMessage.Message = message.Render(_text);
        ResultMessage.Severity = severity;
        ResultMessage.IsOpen = true;
    }

    private async void Details_Click(object sender, RoutedEventArgs args)
    {
        if (_detailsOpen || _presetBusy || _closing) return;
        var result = ((FrameworkElement)sender).DataContext switch
        { HistoryItem history => history.Result, QueueItem queue => queue.Job.Result, _ => null };
        if (result is null) return;
        var item = new HistoryItem(result, _text);
        if (!_monitor.History.IsCurrent(item.Result.Generation)) return;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = _text[item.Result.Success ? "SavedImage" : "SaveFailed"],
            Content = new TextBlock
            {
                Text = string.Create(CultureInfo.InvariantCulture, $"{item.Result.FilePath}\n\n{item.Result.Time:yyyy-MM-dd HH:mm:ss zzz}\n\n") +
                    (item.Result.Success ? _text["SavedSuccessfully"] : item.Summary + "\n\n" + _text["TechnicalDetails"] + ": " + item.Result.Error),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            },
            CloseButtonText = _text["Close"]
        };
        AutomationProperties.SetAutomationId(dialog, "FileDetails");
        _detailsOpen = true;
        _detailsDialog = dialog;
        try { await dialog.ShowAsync(); }
        catch (Exception exception) { ShowResult("DetailsFailed", UiMessage.FromException(exception), InfoBarSeverity.Error, item.Result.Generation); }
        finally { _detailsDialog = null; _detailsOpen = false; }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        RootPanel.IsHitTestVisible = false;
        _statusKey = "Finishing";
        MonitoringStatus.Text = _text[_statusKey];
        _refresh.Stop();
        _imageSave.Stop();
        _previewCancellation?.Cancel();
        _presetDialog?.Hide();
        try
        {
            await FlushImageOptionsAsync();
            if (_folderReady && !_watching && !_starting) await UpdateFolderAsync(FolderPath.Text);
            if (_folderUpdate is not null) await _folderUpdate;
            if (_namingReady && !_watching && !_starting && !_presetBusy && (_namingValid || _namingState.Selected is null)) await SaveNamingAsync(CurrentNamingState());
            if (_namingUpdate is not null) await _namingUpdate;
            if (_languageUpdate is not null) await _languageUpdate;
            await _monitor.DisposeAsync();
        }
        finally { _text.LanguageChanged -= LanguageChanged; _allowClose = true; Close(); }
    }
}

public sealed class HistoryItem(SaveResult result, UiText text) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLanguage()
    {
        foreach (var property in new[] { nameof(Name), nameof(Status), nameof(Summary) })
            PropertyChanged?.Invoke(this, new(property));
    }
    public SaveResult Result { get; } = result;
    public string Name => Path.GetExtension(Result.FilePath) is ".png" or ".jpg" or ".bmp"
        ? Path.GetFileName(Result.FilePath) : text["ClipboardImage"];
    public string Time => Result.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string Status => text[Result.Success ? "Saved" : "Failed"];
    public string Summary => Result.Success ? Result.FilePath : (Result.ErrorMessage ?? new UiMessage("Raw", Result.Error)).Render(text);
    public SolidColorBrush StatusBrush => new(Result.Success ? Microsoft.UI.Colors.ForestGreen : Microsoft.UI.Colors.IndianRed);
}

public sealed class QueueItem(QueueJob job, UiText text) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public QueueJob Job { get; private set; } = job;
    public string Name => Job.Result is { Success: true } result ? Path.GetFileName(result.FilePath) : text.Format("QueueCapture", Job.Id);
    public string Time => Job.AcceptedAt.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public string Format => Job.Format == ImageFormat.Jpeg ? $"JPEG ({Job.JpegQuality})" : Job.Format == ImageFormat.Bmp ? "BMP" : "PNG";
    public string Status => text[Job.Stage switch
    {
        QueueStage.Reading => "QueueReading", QueueStage.Waiting => "QueueWaiting", QueueStage.Saving => "QueueSaving",
        QueueStage.Saved => "Saved", QueueStage.ReadFailed => "QueueReadFailed", QueueStage.Rejected => "QueueRejected", _ => "SaveFailed"
    }];
    public bool CanShowDetails => Job.Result is not null;
    public string Summary => Job.Result is not { } result ? "" : result.Success ? result.FilePath
        : (result.ErrorMessage ?? new UiMessage("Raw", result.Error)).Render(text);
    public SolidColorBrush StatusBrush => new(Job.Stage == QueueStage.Saved ? Microsoft.UI.Colors.ForestGreen
        : Job.IsActive ? Microsoft.UI.Colors.SlateGray : Microsoft.UI.Colors.IndianRed);
    public void Refresh(QueueJob latest)
    {
        Job = latest;
        foreach (var property in new[] { nameof(Name), nameof(Status), nameof(Summary), nameof(StatusBrush), nameof(CanShowDetails) })
            PropertyChanged?.Invoke(this, new(property));
    }
}
