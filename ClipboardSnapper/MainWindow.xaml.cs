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
    private readonly DispatcherQueueTimer _refresh;
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
        InitializeComponent();
        UpdateQualityVisibility();
        WindowPlacement.Apply(AppWindow);
        FolderPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ClipboardSnapper");
        HistoryList.ItemsSource = _history;
        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromMilliseconds(250);
        _refresh.Tick += RefreshResults;
        _refresh.Start();
        AppWindow.Closing += OnClosing;
    }

    private async void Start_Click(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        var generation = _monitor.History.Generation;
        StartButton.IsEnabled = false;
        try
        {
            var folder = FolderPath.Text.Trim();
            if (!Path.IsPathFullyQualified(folder))
                throw new ArgumentException("Choose an absolute save folder path.");
            folder = Path.GetFullPath(folder);
            var options = new SaveOptions(folder, (ImageFormat)FormatPicker.SelectedIndex,
                (int)Math.Round(JpegQuality.Value));
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
            SetControls();
        }
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
        StartButton.IsEnabled = !_watching;
        StopButton.IsEnabled = _watching;
        FolderPath.IsEnabled = !_watching;
        BrowseButton.IsEnabled = !_watching;
        FormatPicker.IsEnabled = !_watching;
        JpegQuality.IsEnabled = !_watching;
    }

    private void Format_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateQualityVisibility();

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
        _detailsDialog?.Hide();
    }

    private async void Browse_Click(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        var generation = _monitor.History.Generation;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) FolderPath.Text = folder.Path;
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
        if (_detailsOpen || _closing || ((FrameworkElement)sender).DataContext is not HistoryItem item) return;
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
        try { await _monitor.DisposeAsync(); }
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
