using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using UnzipProxy.Core;

namespace UnzipProxy
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        private readonly string _archivePath;
        private ExtractionPlan? _plan;
        private JobObjectManager? _jobManager;
        private Process? _workerProcess;
        private StreamWriter? _workerInput;
        private string _manifestPath = "";

        private UserSettings _settings;
        private bool _isDarkMode = true;
        private bool _isPaused = false;
        private bool _isCompleted = false;
        private bool _isDetailsExpanded = false;

        // 完成 / 错误消息只允许被处理一次，杜绝 EOF 兜底覆盖具体错误
        private bool _finishedHandled = false;
        private bool _workerErrorShown = false;

        // 精确匹配原生 Win11 尺寸
        private const double CollapsedHeight = 156.0;
        private const double ExpandedHeight = 306.0;
        private const double ExpandedDetailsHeight = 158.0;

        // 速度采样与指标
        private readonly DispatcherTimer _speedTimer;
        private long _lastBytes = 0;
        private DateTime _startTime = DateTime.UtcNow;
        private DateTime _lastTime = DateTime.UtcNow;
        private double _currentSpeedMBps = 0.0;
        private double _displayedSpeedMBps = 0.0;
        private DateTime _lastSpeedDisplayTime = DateTime.UtcNow;
        private long _totalBytes = 0;
        private long _completedBytes = 0;
        private int _totalItems = 1;

        private ExtractionPrompt? _activePrompt;
        private bool _closeRequested;
        private bool _closingWorker;
        private bool _sizeKnown;
        private string _progressPhase = "";
        private VolumeIdentity? _sourceIdentity;
        private AttemptOptions _attemptOptions = new(null, null);
        private sealed record AttemptOptions(ulong? OuterLimit, ulong? InnerLimit);
        private sealed record BudgetFailure(string Phase, ulong Limit, ulong Written, ulong IntermediateBytes,
            List<VolumeIdentity> Volumes);

        public MainWindow(string archivePath)
        {
            InitializeComponent();
            _archivePath = archivePath;
            _settings = ConfigManager.Load();

            ApplyWin11Theme();
            InitWindowCorner();

            RecycleCheckBox.IsChecked = _settings.RecycleOnSuccess;
            _isDetailsExpanded = _settings.IsDetailsExpanded;

            if (_isDetailsExpanded)
            {
                Height = ExpandedHeight;
                CollapsedContainer.Height = 0;
                CollapsedContainer.Opacity = 0;
                ExpandedContainer.Height = ExpandedDetailsHeight;
                ExpandedContainer.Opacity = 1;
                ToggleChevronPath.Data = Geometry.Parse("M 1 6 L 5 2 L 9 6");
                ToggleDetailsText.Text = "简略信息";
            }
            else
            {
                Height = CollapsedHeight;
                CollapsedContainer.Height = 12;
                CollapsedContainer.Opacity = 1;
                ExpandedContainer.Height = 0;
                ExpandedContainer.Opacity = 0;
                ToggleChevronPath.Data = Geometry.Parse("M 1 2 L 5 6 L 9 2");
                ToggleDetailsText.Text = "详细信息";
            }

            _speedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _speedTimer.Tick += OnSpeedTimerTick;

            Loaded += OnWindowLoaded;
            Closing += OnWindowClosing;
            Activated += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                SystemEffects.StopFlashing(hwnd);
            };
        }

        private void InitWindowCorner()
        {
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            };
        }

        private void ApplyWin11Theme()
        {
            _isDarkMode = false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null)
                {
                    var val = key.GetValue("AppsUseLightTheme");
                    if (val is int lightInt && lightInt == 0)
                    {
                        _isDarkMode = true;
                    }
                }
            }
            catch { }

            if (_isDarkMode)
            {
                Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(32, 32, 32));
                Resources["WindowBorderBrush"] = new SolidColorBrush(Color.FromRgb(56, 56, 56));
                Resources["CardBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(43, 43, 43));
                Resources["TextForegroundBrush"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                Resources["SubTextBrush"] = new SolidColorBrush(Color.FromRgb(185, 185, 185));
                Resources["TitleDisabledBrush"] = new SolidColorBrush(Color.FromRgb(90, 90, 90));
                Resources["TitleButtonHoverBrush"] = new SolidColorBrush(Color.FromRgb(56, 56, 56));
                Resources["ButtonHoverBrush"] = new SolidColorBrush(Color.FromRgb(56, 56, 56));
                Resources["ButtonPressedBrush"] = new SolidColorBrush(Color.FromRgb(66, 66, 66));
                Resources["ProgressTrackBrush"] = new SolidColorBrush(Color.FromRgb(55, 55, 55));

                Resources["AccentProgressBrush"] = _isPaused
                    ? (SolidColorBrush)Resources["Win11Yellow"]
                    : (SolidColorBrush)Resources["Win11Cyan"];

                SpeedGraph.IsDarkMode = true;
            }
            else
            {
                Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                Resources["WindowBorderBrush"] = new SolidColorBrush(Color.FromRgb(215, 215, 215));
                Resources["CardBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(249, 249, 249));
                Resources["TextForegroundBrush"] = new SolidColorBrush(Color.FromRgb(25, 25, 25));
                Resources["SubTextBrush"] = new SolidColorBrush(Color.FromRgb(100, 100, 100));
                Resources["TitleDisabledBrush"] = new SolidColorBrush(Color.FromRgb(190, 190, 190));
                Resources["TitleButtonHoverBrush"] = new SolidColorBrush(Color.FromRgb(235, 235, 235));
                Resources["ButtonHoverBrush"] = new SolidColorBrush(Color.FromRgb(235, 235, 235));
                Resources["ButtonPressedBrush"] = new SolidColorBrush(Color.FromRgb(225, 225, 225));
                Resources["ProgressTrackBrush"] = new SolidColorBrush(Color.FromRgb(230, 230, 230));

                Resources["AccentProgressBrush"] = _isPaused
                    ? (SolidColorBrush)Resources["Win11Amber"]
                    : (SolidColorBrush)Resources["Win11Green"];

                SpeedGraph.IsDarkMode = false;
            }

            SpeedGraph.IsPaused = _isPaused;
        }

        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_archivePath) || !File.Exists(_archivePath))
            {
                TriggerError("文件不存在", _archivePath ?? "");
                return;
            }

            _plan = ArchiveTaskPlanner.Plan(_archivePath);
            _sourceIdentity = VolumeIdentity.Capture(_plan.SourceArchivePath);
            TitleText.Text = "正在打开压缩包…";
            MainStatusText.Text = "正在打开压缩包…";

            string srcName = Path.GetFileName(_archivePath);
            string targetName = _plan.TargetRootDirName;
            string fullPathInfo = $"正在将 1 个项目从 {srcName} 解压到 {targetName}";
            PathInfoText.Text = fullPathInfo;
            PathInfoText.ToolTip = fullPathInfo;

            _startTime = DateTime.UtcNow;
            _lastTime = DateTime.UtcNow;
            _lastSpeedDisplayTime = DateTime.UtcNow;
            _speedTimer.Start();

            await StartExtractionAsync();
        }

        private async Task StartExtractionAsync()
        {
            Process? worker = null;
            bool started = false;
            try
            {
                var plan = _plan ?? throw new InvalidOperationException("缺少解压计划");
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string workerExe = Path.Combine(appDir, "UnzipWorker.exe");
                if (!File.Exists(workerExe))
                    throw new FileNotFoundException("找不到解压工作组件 UnzipWorker.exe");

                try
                {
                    Directory.CreateDirectory(plan.StagingDirectory);
                }
                catch (UnauthorizedAccessException)
                {
                    TriggerError("无法写入解压目录",
                        $"Windows 拒绝在「{plan.DestinationParentDir}」创建暂存目录。解压尚未开始，源压缩包已保留。");
                    return;
                }
                _manifestPath = Path.Combine(plan.DestinationParentDir, $".unbox_manifest_{Guid.NewGuid():N}.json");

                var utf8 = new UTF8Encoding(false);
                var psi = new ProcessStartInfo
                {
                    FileName = workerExe,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = utf8,
                    StandardOutputEncoding = utf8,
                    StandardErrorEncoding = utf8,
                    CreateNoWindow = true
                };
                foreach (var argument in new[] { "--managed", "--archive", plan.SourceArchivePath,
                    "--staging", plan.StagingDirectory, "--manifest", _manifestPath })
                    psi.ArgumentList.Add(argument);
                if (_settings.ArchiveCodePage != 0)
                {
                    psi.ArgumentList.Add("--cp");
                    psi.ArgumentList.Add(_settings.ArchiveCodePage.ToString(CultureInfo.InvariantCulture));
                }
                AddLimit("--max-output-bytes", _attemptOptions.OuterLimit);
                AddLimit("--max-inner-output-bytes", _attemptOptions.InnerLimit);

                void AddLimit(string option, ulong? value)
                {
                    if (!value.HasValue) return;
                    psi.ArgumentList.Add(option);
                    psi.ArgumentList.Add(value.Value.ToString(CultureInfo.InvariantCulture));
                }

                _jobManager = new JobObjectManager();
                worker = new Process { StartInfo = psi };
                started = worker.Start();
                if (!started) throw new InvalidOperationException("无法启动解压工作进程");
                if (!_jobManager.AssignProcess(worker))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将解压进程加入任务对象");

                _workerProcess = worker;
                _workerInput = worker.StandardInput;
                _workerInput.NewLine = "\n";
                _workerInput.WriteLine("{\"type\":\"start\"}");
                _workerInput.Flush();

                var drainError = worker.StandardError.BaseStream.CopyToAsync(Stream.Null);
                await Task.Run(() => ListenWorkerOutput(worker, worker.StandardOutput));
                await worker.WaitForExitAsync();
                await drainError;
                if (ReferenceEquals(_workerProcess, worker) && !_finishedHandled && !_workerErrorShown && !_isCompleted && !_closingWorker)
                {
                    TriggerError("解压中断", $"工作进程异常退出（退出代码 {worker.ExitCode}）");
                }
            }
            catch (Exception ex)
            {
                if (started && worker != null)
                {
                    try
                    {
                        worker.StandardInput.Close();
                        if (!worker.HasExited) worker.Kill();
                        await worker.WaitForExitAsync();
                    }
                    catch (InvalidOperationException) { }
                }
                if (ReferenceEquals(_workerProcess, worker) || _workerProcess == null)
                {
                    _workerProcess = null;
                    _workerInput = null;
                    _jobManager?.Dispose();
                    _jobManager = null;
                    if (!_closingWorker)
                    {
                        StopWorkerAndCleanup();
                        TriggerError("解压启动出错", ex.Message);
                    }
                }
                worker?.Dispose();
            }
        }

        private void ListenWorkerOutput(Process worker, StreamReader reader)
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement.Clone();
                    string type = root.GetProperty("type").GetString() ?? "";

                    Dispatcher.Invoke(() =>
                    {
                        if (!ReferenceEquals(_workerProcess, worker) || _closingWorker ||
                            _finishedHandled || _workerErrorShown || _isCompleted) return;
                        HandleWorkerMessage(type, root);
                    });
                }
                catch
                {
                    // 单行解析失败不影响其余状态流
                }
            }
        }

        private void HandleWorkerMessage(string type, JsonElement root)
        {
            if (_finishedHandled || _isCompleted) return;

            switch (type)
            {
                case "progress":
                    string phase = root.TryGetProperty("phase", out var phaseValue) ? phaseValue.GetString() ?? "" : "";
                    if (_progressPhase != phase)
                    {
                        _progressPhase = phase;
                        _lastBytes = 0;
                        _currentSpeedMBps = 0;
                    }
                    _completedBytes = root.TryGetProperty("completed_bytes", out var completed) && completed.TryGetInt64(out long count) ? count : 0;
                    ReadProgressTotal(root);
                    UpdateProgressUi(root.TryGetProperty("current_file", out var file) ? file.GetString() : null);
                    break;

                case "init":
                    _totalItems = root.TryGetProperty("total_items", out var ti) ? ti.GetInt32() : _totalItems;
                    ReadProgressTotal(root);
                    if (_plan != null)
                    {
                        string srcName = Path.GetFileName(_archivePath);
                        string targetName = _plan.TargetRootDirName;
                        string fullPathInfo = $"正在将 {_totalItems} 个项目从 {srcName} 解压到 {targetName}";
                        PathInfoText.Text = fullPathInfo;
                        PathInfoText.ToolTip = fullPathInfo;
                    }
                    UpdateProgressUi();
                    break;

                case "invalid_chars_found":
                    HandleInvalidChars(root);
                    break;

                case "encoding_options":
                    HandleEncodingOptions(root);
                    break;

                case "password_request":
                    ShowPasswordDialog();
                    break;

                case "finished":
                    _ = HandleFinishedAsync(root);
                    break;

                case "canceled":
                    Close();
                    break;

                case "error":
                    string msg = root.TryGetProperty("message", out var m) ? (m.GetString() ?? "未知错误") : "未知错误";
                    if (TryReadRequired(root, "error_reason", out string reason))
                        msg = DescribeFailure(reason, -1, "failed");
                    TriggerError("解压失败", msg);
                    break;
            }
        }

        private void TriggerError(string title, string message)
        {
            _workerErrorShown = true;
            _speedTimer.Stop();
            TitleText.Text = title;
            MainStatusText.Text = title;
            PathInfoText.Text = message;
            PathInfoText.ToolTip = message;
            ExtractProgressBar.IsIndeterminate = false;
            ExtractProgressBar.Foreground = (Brush)Resources["Win11Red"];
            RunningActionPanel.Visibility = Visibility.Collapsed;
            FinishedActionPanel.Visibility = Visibility.Visible;

            Topmost = true;
            Activate();
            Focus();
            var hwnd = new WindowInteropHelper(this).Handle;
            SystemEffects.FlashWindowForError(hwnd);
        }

        private void ReadProgressTotal(JsonElement root)
        {
            _totalBytes = root.TryGetProperty("total_bytes", out var total) && total.TryGetInt64(out long bytes) ? bytes : 0;
            _sizeKnown = root.TryGetProperty("size_known", out var known) && known.ValueKind == JsonValueKind.True;
        }

        // 统一且流畅的实时进度更新
        private void UpdateProgressUi(string? currentFile = null)
        {
            if (!string.IsNullOrEmpty(currentFile))
            {
                // 名称由 Worker 唯一决定（显示、写盘、对账共用同一份名称），UI 不再二次纠偏
                DetailFileNameText.Text = currentFile;
                DetailFileNameText.ToolTip = currentFile;
            }

            bool determinate = _sizeKnown && _totalBytes > 0;
            ExtractProgressBar.IsIndeterminate = !determinate;
            double progress = determinate ? Math.Clamp((double)_completedBytes / _totalBytes, 0.0, 1.0) : 0;
            double pct = progress * 100;
            ExtractProgressBar.Value = pct;

            string statusText = determinate ? $"已完成 {pct:0}%" : $"已解出 {FormatBytes(_completedBytes)}";
            if (_isPaused) statusText = "已暂停 · " + statusText;
            TitleText.Text = statusText;
            MainStatusText.Text = statusText;

            DateTime now = DateTime.UtcNow;
            double elapsedSec = (now - _lastTime).TotalSeconds;
            if (elapsedSec >= 0.03)
            {
                long bytesDelta = _completedBytes - _lastBytes;
                if (bytesDelta >= 0)
                {
                    double instantMBps = (bytesDelta / (1024.0 * 1024.0)) / elapsedSec;
                    _currentSpeedMBps = _currentSpeedMBps > 0 ? (_currentSpeedMBps * 0.75 + instantMBps * 0.25) : instantMBps;
                }
                _lastBytes = _completedBytes;
                _lastTime = now;
            }

            if ((now - _lastSpeedDisplayTime).TotalMilliseconds >= 500 || _displayedSpeedMBps <= 0.0 || _isPaused)
            {
                _displayedSpeedMBps = _currentSpeedMBps;
                _lastSpeedDisplayTime = now;
            }

            SpeedGraph.PushSample(progress, _currentSpeedMBps, _displayedSpeedMBps);

            if (!determinate)
            {
                DetailRemainingText.Text = "已解出 " + FormatBytes(_completedBytes);
                DetailEtaText.Text = "—";
                return;
            }
            long remainingBytes = Math.Max(0, _totalBytes - _completedBytes);
            int estimatedRemainingItems = Math.Max(0, (int)Math.Ceiling((1.0 - progress) * _totalItems));
            if (remainingBytes > 0 && estimatedRemainingItems == 0) estimatedRemainingItems = 1;
            DetailRemainingText.Text = $"{estimatedRemainingItems} ({FormatBytes(remainingBytes)})";

            if (progress >= 1.0 || remainingBytes == 0)
            {
                DetailEtaText.Text = "完成";
            }
            else if (_currentSpeedMBps > 0.1)
            {
                double remainSec = (remainingBytes / (1024.0 * 1024.0)) / _currentSpeedMBps;
                DetailEtaText.Text = $"大约 {FormatTimeSpan(TimeSpan.FromSeconds(remainSec))}";
            }
            else if (progress > 0.01)
            {
                DetailEtaText.Text = "大约 几秒钟";
            }
            else
            {
                DetailEtaText.Text = "正在计算...";
            }
        }

        private void OnSpeedTimerTick(object? sender, EventArgs e)
        {
            if (_isCompleted || _isPaused || _workerErrorShown || _closingWorker) return;
            UpdateProgressUi();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB";
        }

        private static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalSeconds < 5) return "几秒钟";
            if (ts.TotalMinutes < 1) return $"{Math.Ceiling(ts.TotalSeconds)} 秒";
            if (ts.TotalHours < 1) return $"{ts.Minutes} 分 {ts.Seconds} 秒";
            return $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分钟";
        }

        // 处理非法字符交互 (D6)
        private void HandleInvalidChars(JsonElement root)
        {
            if (_settings.RememberInvalidCharsAction && _settings.InvalidCharsAction != "ask")
            {
                SendWorkerMessage($"{{\"type\":\"invalid_chars_response\",\"action\":\"{_settings.InvalidCharsAction}\"}}");
                return;
            }

            var previewLines = new List<string>();
            if (root.TryGetProperty("items", out var itemsArray) && itemsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in itemsArray.EnumerateArray())
                {
                    string raw = it.TryGetProperty("raw", out var r) ? (r.GetString() ?? "") : "";
                    string sanitized = it.TryGetProperty("sanitized", out var s) ? (s.GetString() ?? "") : "";
                    previewLines.Add($"• 原名: {raw}  ->  建议: {sanitized}");
                }
            }
            var prompt = CreatePrompt("文件名包含非法字符", "确定应用");
            prompt.InvalidItemsListText.Text = string.Join("\n", previewLines);
            prompt.ModalInvalidCharsPanel.Visibility = Visibility.Visible;
            if (!ShowPrompt(prompt, prompt.RadioBatchRename))
            {
                Close();
                return;
            }

            string action = prompt.RadioBatchRename.IsChecked == true ? "batch_rename" : "skip";
            if (prompt.RememberInvalidCharsCheck.IsChecked == true)
            {
                _settings.InvalidCharsAction = action;
                _settings.RememberInvalidCharsAction = true;
                ConfigManager.Save(_settings);
            }

            SendWorkerMessage($"{{\"type\":\"invalid_chars_response\",\"action\":\"{action}\"}}");
        }

        // 文件名编码选择（编码不确定时）
        private void HandleEncodingOptions(JsonElement root)
        {
            var options = new List<(uint CodePage, string Label, List<string> Samples)>();
            if (root.TryGetProperty("options", out var optionsArray) && optionsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in optionsArray.EnumerateArray())
                {
                    uint cp = option.TryGetProperty("cp", out var cpElem) ? (uint)cpElem.GetInt32() : 0;
                    string label = option.TryGetProperty("label", out var labelElem) ? (labelElem.GetString() ?? ("代码页 " + cp)) : ("代码页 " + cp);
                    var samples = new List<string>();
                    if (option.TryGetProperty("samples", out var sampleArray) && sampleArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sample in sampleArray.EnumerateArray())
                        {
                            string? text = sample.GetString();
                            if (!string.IsNullOrEmpty(text)) samples.Add(text);
                        }
                    }
                    options.Add((cp, label, samples));
                }
            }
            if (options.Count == 0)
            {
                SendWorkerMessage("{\"type\":\"encoding_response\",\"cp\":0}");
                return;
            }

            // 已有记忆偏好时直接静默回答，不再打断用户
            if (_settings.RememberArchiveCodePage && _settings.ArchiveCodePage != 0)
            {
                SendWorkerMessage($"{{\"type\":\"encoding_response\",\"cp\":{_settings.ArchiveCodePage}}}");
                return;
            }

            var prompt = CreatePrompt("选择文件名编码", "应用并继续");
            bool first = true;
            foreach (var option in options)
            {
                var radio = new RadioButton
                {
                    GroupName = "EncodingOptions",
                    Content = option.Label,
                    Tag = option.CodePage,
                    IsChecked = first,
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 4),
                    Foreground = (Brush)Resources["TextForegroundBrush"]
                };
                prompt.EncodingOptionsPanel.Children.Add(radio);

                if (option.Samples.Count > 0)
                {
                    var sampleText = new TextBlock
                    {
                        Text = "示例：" + string.Join("  /  ", option.Samples),
                        FontSize = 11,
                        Margin = new Thickness(18, 0, 0, 8),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Brush)Resources["SubTextBrush"]
                    };
                    prompt.EncodingOptionsPanel.Children.Add(sampleText);
                }
                first = false;
            }

            prompt.ModalEncodingPanel.Visibility = Visibility.Visible;
            if (!ShowPrompt(prompt, prompt.EncodingOptionsPanel.Children[0]))
            {
                Close();
                return;
            }

            uint selected = (uint)prompt.EncodingOptionsPanel.Children.OfType<RadioButton>()
                .First(radio => radio.IsChecked == true).Tag;
            if (prompt.RememberEncodingCheck.IsChecked == true && selected != 0)
            {
                _settings.ArchiveCodePage = (int)selected;
                _settings.RememberArchiveCodePage = true;
                ConfigManager.Save(_settings);
            }

            SendWorkerMessage($"{{\"type\":\"encoding_response\",\"cp\":{selected}}}");
        }

        private void ShowPasswordDialog()
        {
            var prompt = CreatePrompt("输入密码", "确定");
            prompt.ModalPasswordPanel.Visibility = Visibility.Visible;
            if (!ShowPrompt(prompt, prompt.PasswordInputBox))
            {
                SendWorkerMessage("{\"type\":\"password_response\",\"canceled\":true}");
                Close();
                return;
            }

            var payload = new { type = "password_response", password = prompt.PasswordInputBox.Password };
            prompt.PasswordInputBox.Clear();
            SendWorkerMessage(JsonSerializer.Serialize(payload));
        }

        private ExtractionPrompt CreatePrompt(string title, string submit)
        {
            var prompt = new ExtractionPrompt { Owner = (Window?)_activePrompt ?? this, Title = title };
            prompt.Resources.MergedDictionaries.Add(Resources);
            prompt.Foreground = (Brush)Resources["TextForegroundBrush"];
            prompt.SubmitButton.Content = submit;
            return prompt;
        }

        private bool ShowPrompt(ExtractionPrompt prompt, UIElement initialFocus)
        {
            var previous = _activePrompt;
            _activePrompt = prompt;
            prompt.ContentRendered += (_, _) => initialFocus.Focus();
            try
            {
                return prompt.ShowDialog() == true;
            }
            finally
            {
                _activePrompt = previous;
            }
        }

        // D2 移入回收站告警与偏好记忆
        private void OnRecycleCheckBoxClick(object sender, RoutedEventArgs e)
        {
            if (RecycleCheckBox.IsChecked == true)
            {
                RecycleCheckBox.IsChecked = false;
                var prompt = CreatePrompt("移入回收站提示", "我知道了");
                prompt.ModalRecycleAlertPanel.Visibility = Visibility.Visible;
                RecycleCheckBox.IsChecked = ShowPrompt(prompt, prompt.SubmitButton);
            }
            _settings.RecycleOnSuccess = RecycleCheckBox.IsChecked == true;
            ConfigManager.Save(_settings);
        }

        // 暂停 / 继续
        private void OnPauseResumeClick(object sender, RoutedEventArgs e)
        {
            if (_finishedHandled || _isCompleted) return;

            if (!_isPaused)
            {
                _isPaused = true;
                PauseResumeIcon.Text = "▶";
                SpeedGraph.IsPaused = true;
                ApplyWin11Theme();
                UpdateProgressUi();
                SendWorkerMessage("{\"type\":\"pause\"}");
            }
            else
            {
                _isPaused = false;
                PauseResumeIcon.Text = "❚❚";
                SpeedGraph.IsPaused = false;
                _lastTime = DateTime.UtcNow;
                _lastBytes = _completedBytes;
                _lastSpeedDisplayTime = DateTime.UtcNow;
                ApplyWin11Theme();
                UpdateProgressUi();
                SendWorkerMessage("{\"type\":\"resume\"}");
            }
        }

        // 取消 / 关闭
        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 取消或关闭窗口时：先等待工作进程真正退出（必要时强杀），再清理暂存目录，
        /// 避免在文件仍被占用时清理而留下残留。
        /// </summary>
        private void StopWorkerAndCleanup()
        {
            try
            {
                if (_workerProcess != null && !_workerProcess.HasExited)
                {
                    if (!_workerProcess.WaitForExit(3000))
                    {
                        _workerProcess.Kill();
                        if (!_workerProcess.WaitForExit(2000)) return;
                    }
                }
            }
            catch { return; }

            if (_plan != null)
            {
                ArchiveTaskPlanner.CleanupStaging(_plan);
            }
            CleanupManifest();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            OnCancelClick(sender, e);
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        // 详细信息平滑动画展开/折叠与偏好记忆
        private void OnToggleDetailsClick(object sender, RoutedEventArgs e)
        {
            _isDetailsExpanded = !_isDetailsExpanded;

            _settings.IsDetailsExpanded = _isDetailsExpanded;
            ConfigManager.Save(_settings);

            double targetWindowHeight = _isDetailsExpanded ? ExpandedHeight : CollapsedHeight;
            double targetExpandedHeight = _isDetailsExpanded ? ExpandedDetailsHeight : 0.0;
            double targetExpandedOpacity = _isDetailsExpanded ? 1.0 : 0.0;
            double targetCollapsedHeight = _isDetailsExpanded ? 0.0 : 12.0;
            double targetCollapsedOpacity = _isDetailsExpanded ? 0.0 : 1.0;

            if (_isDetailsExpanded)
            {
                ToggleChevronPath.Data = Geometry.Parse("M 1 6 L 5 2 L 9 6");
                ToggleDetailsText.Text = "简略信息";
            }
            else
            {
                ToggleChevronPath.Data = Geometry.Parse("M 1 2 L 5 6 L 9 2");
                ToggleDetailsText.Text = "详细信息";
            }

            var duration = TimeSpan.FromMilliseconds(220);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            var animWindow = new DoubleAnimation(targetWindowHeight, duration) { EasingFunction = ease };
            BeginAnimation(HeightProperty, animWindow);

            CollapsedContainer.BeginAnimation(HeightProperty, new DoubleAnimation(targetCollapsedHeight, duration) { EasingFunction = ease });
            CollapsedContainer.BeginAnimation(OpacityProperty, new DoubleAnimation(targetCollapsedOpacity, duration) { EasingFunction = ease });

            ExpandedContainer.BeginAnimation(HeightProperty, new DoubleAnimation(targetExpandedHeight, duration) { EasingFunction = ease });
            ExpandedContainer.BeginAnimation(OpacityProperty, new DoubleAnimation(targetExpandedOpacity, duration) { EasingFunction = ease });
        }

        /// <summary>
        /// 完成处理：先取回 Worker 的完整结果与退出码，再做清单对账、目录提交与（可选）源包回收。
        /// 未通过任何一项即不提交、不回收，并如实说明原因。
        /// </summary>
        private async Task HandleFinishedAsync(JsonElement root)
        {
            if (_finishedHandled) return;
            _finishedHandled = true;
            _speedTimer.Stop();
            PauseResumeButton.IsEnabled = false;
            RecycleCheckBox.IsEnabled = false;
            MainStatusText.Text = "正在校验和交付…";

            bool autoClose = false;
            AttemptOptions? retry = null;
            try
            {
                if (ReadBudgetFailure(root) is { } budget)
                    retry = await TryRetryBudgetAsync(budget);
                if (retry == null)
                    autoClose = await FinalizeExtractionAsync(root);
            }
            catch (Exception ex)
            {
                FailWithoutCommit("完成处理异常，已保留源包和可用的暂存内容：" + ex.Message, true);
            }
            finally
            {
                CleanupManifest();
                _isCompleted = retry == null;
            }

            if (retry != null && !_closeRequested)
            {
                _workerInput = null;
                _jobManager?.Dispose();
                _jobManager = null;
                _workerProcess?.Dispose();
                _workerProcess = null;
                _plan = ArchiveTaskPlanner.Plan(_archivePath);
                _attemptOptions = retry;
                _finishedHandled = false;
                _workerErrorShown = false;
                _sizeKnown = false;
                _progressPhase = "";
                _isPaused = false;
                _totalBytes = _completedBytes = _lastBytes = 0;
                _displayedSpeedMBps = _currentSpeedMBps = 0;
                _lastTime = _startTime = _lastSpeedDisplayTime = DateTime.UtcNow;
                PauseResumeButton.IsEnabled = true;
                RecycleCheckBox.IsEnabled = true;
                _speedTimer.Start();
                await StartExtractionAsync();
                return;
            }

            _isCompleted = true;
            if (_closeRequested)
            {
                Close();
            }
            else if (autoClose)
            {
                await Task.Delay(800);
                if (IsVisible) Close();
            }
        }

        private BudgetFailure? ReadBudgetFailure(JsonElement root)
        {
            if (!TryReadRequired(root, "status", out string status) || status != "failed" ||
                !TryReadRequired(root, "error_reason", out string reason) || reason != "output_budget_exceeded" ||
                !TryReadRequired(root, "manifest", out string manifest) || !string.Equals(manifest, _manifestPath, StringComparison.Ordinal) ||
                !TryReadRequired(root, "phase", out string phase) || phase is not ("outer" or "inner") ||
                !root.TryGetProperty("size_known", out var known) || known.ValueKind != JsonValueKind.False ||
                !root.TryGetProperty("limit_bytes", out var limit) || !limit.TryGetUInt64(out ulong bytes) ||
                !root.TryGetProperty("written_bytes", out var written) || !written.TryGetUInt64(out ulong used) ||
                used > bytes) return null;
            ulong intermediate = root.TryGetProperty("intermediate_bytes", out var payload) &&
                payload.TryGetUInt64(out ulong length) ? length : 0;
            return new BudgetFailure(phase, bytes, used, intermediate, ParseVolumeIdentities(root));
        }

        private async Task<AttemptOptions?> TryRetryBudgetAsync(BudgetFailure failure)
        {
            var worker = _workerProcess;
            var plan = _plan;
            if (worker == null || plan == null || !await Task.Run(() => worker.WaitForExit(15000)) || worker.ExitCode != 10)
                return null;

            bool SourcesMatch() => _sourceIdentity != null &&
                VolumeIdentity.Capture(plan.SourceArchivePath) is { } source && _sourceIdentity.Matches(source) &&
                failure.Volumes.Count > 0 && failure.Volumes.All(
                    original => VolumeIdentity.Capture(original.Path) is { } current && original.Matches(current));
            if (_closeRequested || !SourcesMatch()) return null;

            _workerInput?.Dispose();
            _workerInput = null;
            ArchiveTaskPlanner.CleanupStaging(plan);
            CleanupManifest();
            if (Directory.Exists(plan.StagingDirectory) || File.Exists(_manifestPath)) return null;

            const ulong MiB = 1024 * 1024;
            ulong FreeMiB()
            {
                ulong free = (ulong)Math.Max(0, new DriveInfo(Path.GetPathRoot(plan.DestinationParentDir)!).AvailableFreeSpace);
                return free > failure.IntermediateBytes ? (free - failure.IntermediateBytes) / MiB : 0;
            }
            ulong available = FreeMiB();
            ulong minimum = failure.Limit / MiB + 1;
            if (minimum > available) return null;

            var prompt = CreatePrompt("解压超出本次上限", "重新解压");
            prompt.ModalBudgetPanel.Visibility = Visibility.Visible;
            prompt.BudgetDetailText.Text = $"已解出 {failure.Written / (double)MiB:N1} MiB，上限为 {failure.Limit / (double)MiB:N1} MiB。" +
                $"磁盘可用 {available:N0} MiB。设置更大的上限后将从头解压。";
            prompt.BudgetLimitInput.Text = Math.Min(available, Math.Max(minimum, 2 * (failure.Limit / MiB))).ToString(CultureInfo.InvariantCulture);
            ulong entered = 0;
            prompt.ValidateSubmission = () =>
            {
                if (ulong.TryParse(prompt.BudgetLimitInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out entered) &&
                    entered >= minimum && entered <= FreeMiB()) return true;
                prompt.BudgetValidationText.Text = $"请输入 {minimum:N0} 到 {FreeMiB():N0} MiB 之间的整数。";
                return false;
            };
            if (!ShowPrompt(prompt, prompt.BudgetLimitInput) || _closeRequested || !SourcesMatch() || entered > FreeMiB())
                return null;

            return failure.Phase == "inner" ? _attemptOptions with { InnerLimit = entered * MiB } :
                _attemptOptions with { OuterLimit = entered * MiB };
        }

        private async Task<bool> FinalizeExtractionAsync(JsonElement root)
        {
            var worker = _workerProcess;
            bool workerExited = worker != null && (worker.HasExited ||
                await Task.Run(() => worker.WaitForExit(15000)));
            if (!workerExited && worker != null)
            {
                SendWorkerMessage("{\"type\":\"cancel\"}");
                await Task.Run(StopWorkerAndCleanup);
            }
            int exitCode = workerExited ? worker!.ExitCode : -1;

            // 等待 Worker 退出后才验证完成帧，失败路径也不会与 Worker 同时清理暂存区。
            if (!TryReadRequired(root, "status", out string status) ||
                !TryReadRequired(root, "manifest", out string manifestPath) ||
                !TryReadRequired(root, "delivery_root", out string deliveryRoot) ||
                !TryReadRequired(root, "error_reason", out string errorReason) ||
                !root.TryGetProperty("total_items", out var total) || !total.TryGetUInt32(out _) ||
                !root.TryGetProperty("extracted_items", out var extracted) || !extracted.TryGetUInt32(out _) ||
                !root.TryGetProperty("skipped_items", out var skipped) || !skipped.TryGetUInt32(out uint skippedItems) ||
                !root.TryGetProperty("completed_bytes", out var bytes) || !bytes.TryGetUInt64(out _))
            {
                FailWithoutCommit("Worker 完成消息不完整，已中止交付并保留源包。", true);
                return false;
            }

            bool manifestOk = root.TryGetProperty("manifest_ok", out var mo) && mo.ValueKind == JsonValueKind.True;
            bool exitMatches = status switch
            {
                "ok" => exitCode == 0,
                "partial" => exitCode == 11,
                _ => false
            };

            if (!workerExited || !exitMatches || !manifestOk || errorReason.Length != 0)
            {
                FailWithoutCommit(DescribeFailure(errorReason, exitCode, status), !workerExited);
                return false;
            }

            if (!string.Equals(manifestPath, _manifestPath, StringComparison.OrdinalIgnoreCase))
            {
                FailWithoutCommit("Worker 返回的交付清单路径与本次任务不符，已保留源包。", true);
                return false;
            }

            uint policyFiltered = root.TryGetProperty("policy_filtered_items", out var pf) ? pf.GetUInt32() : 0;
            uint unsafeItems = root.TryGetProperty("unsafe_items", out var ui) ? ui.GetUInt32() : 0;
            uint missingItems = root.TryGetProperty("missing_items", out var mi) ? mi.GetUInt32() : 0;
            bool emptyArchive = root.TryGetProperty("empty_archive", out var ea) && ea.ValueKind == JsonValueKind.True;
            bool partial = status == "partial" || skippedItems > 0 || unsafeItems > 0 || missingItems > 0;
            bool recycleRequested = RecycleCheckBox.IsChecked == true && !partial;
            var volumes = ParseVolumeIdentities(root);
            var plan = _plan!;

            CompletionResult result = await Task.Run(() =>
            {
                var manifest = DeliveryManifest.Load(manifestPath);
                if (!string.Equals(manifest.DeliveryRootRelative, deliveryRoot, StringComparison.Ordinal))
                    throw new InvalidDataException("交付清单与完成消息的交付根不一致");

                return ExtractionCompleter.Complete(new CompletionRequest
                {
                    Plan = plan,
                    DeliveryRootRelative = deliveryRoot,
                    Manifest = manifest,
                    RecycleRequested = recycleRequested,
                    Volumes = volumes,
                    PropagateZoneIdentifier = true
                });
            });

            if (!result.Delivered)
            {
                string detail = result.FailureReason;
                if (!string.IsNullOrEmpty(result.KeptStagingDirectory))
                {
                    detail += $"\n暂存目录已保留以便恢复：{result.KeptStagingDirectory}";
                }
                FailWithoutCommit(detail, result.KeptStagingDirectory.Length > 0);
                return false;
            }

            // 交付成功
            ExtractProgressBar.IsIndeterminate = false;
            ExtractProgressBar.Value = 100;
            DetailEtaText.Text = "完成";
            DetailRemainingText.Text = "0 (0 B)";
            SpeedGraph.PushSample(1.0, _currentSpeedMBps, _displayedSpeedMBps);

            string notes = result.Summary;
            if (policyFiltered > 0) notes += $"；已按产品规则过滤 {policyFiltered} 个 macOS 元数据项";
            if (skippedItems > 0) notes += $"；{skippedItems} 项被跳过";
            if (unsafeItems > 0) notes += $"；{unsafeItems} 项因路径不安全被拒绝";
            if (missingItems > 0) notes += $"；{missingItems} 项未交付（计划条目缺失）";
            if (emptyArchive) notes = "归档为空，已创建空目标目录";
            if (result.ZoneMarked > 0) notes += $"；已为 {result.ZoneMarked} 个文件保留下载来源标记";

            bool recycleProblem = result.Recycle != null && !result.Recycle.Success;

            if (partial)
            {
                TitleText.Text = "部分完成";
                MainStatusText.Text = "部分完成";
                MainStatusText.Foreground = (Brush)Resources["Win11Amber"];
                PathInfoText.Text = notes + "；源压缩包已保留。";
                PathInfoText.ToolTip = PathInfoText.Text;
                RunningActionPanel.Visibility = Visibility.Collapsed;
                FinishedActionPanel.Visibility = Visibility.Visible;
                return false;
            }

            if (recycleProblem)
            {
                MainStatusText.Text = "解压完成（源文件回收未完成）";
                MainStatusText.Foreground = (Brush)Resources["Win11Amber"];
                TitleText.Text = "已完成 100%";
                PathInfoText.Text = "解压已完成并已逐项校验。" + result.Recycle!.Message + "。"
                                    + DescribeRecycleItems(result.Recycle);
                PathInfoText.ToolTip = PathInfoText.Text;
                RunningActionPanel.Visibility = Visibility.Collapsed;
                FinishedActionPanel.Visibility = Visibility.Visible;
                await SystemEffects.PlayFinishedSound();
                return false;
            }

            TitleText.Text = "已完成 100%";
            MainStatusText.Text = "已完成 100%";
            MainStatusText.Foreground = (Brush)Resources["TextForegroundBrush"];
            string finalText = notes;
            if (result.Recycle != null && result.Recycle.Success)
            {
                finalText += "；" + result.Recycle.Message;
            }
            PathInfoText.Text = finalText;
            PathInfoText.ToolTip = finalText;

            await SystemEffects.PlayFinishedSound();

            return true;
        }

        private static string DescribeRecycleItems(RecycleResult recycle)
        {
            var failures = recycle.Items.Where(i => !i.Recycled).Select(i => $"{Path.GetFileName(i.Path)}：{i.Detail}").ToList();
            if (failures.Count == 0) return "";
            return "（未回收：" + string.Join("；", failures) + "）";
        }

        private static List<VolumeIdentity> ParseVolumeIdentities(JsonElement root)
        {
            var list = new List<VolumeIdentity>();
            if (!root.TryGetProperty("volume_identities", out var identities) || identities.ValueKind != JsonValueKind.Array)
            {
                return list;
            }
            foreach (var element in identities.EnumerateArray())
            {
                try
                {
                    var identity = new VolumeIdentity
                    {
                        Path = element.TryGetProperty("path", out var p) ? (p.GetString() ?? "") : "",
                        VolumeSerial = element.TryGetProperty("volume", out var v) ? v.GetUInt64() : 0,
                        Size = element.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                        LastWriteTime = element.TryGetProperty("mtime", out var m) ? m.GetInt64() : 0,
                        Valid = element.TryGetProperty("valid", out var va) && va.ValueKind == JsonValueKind.True
                    };

                    // 128 位文件 ID 优先使用原始 16 字节的规范表示（Worker 端 file_id 十六进制），
                    // 避免高低 64 位定义相反；缺少该字段时退回 file_id_high / file_id_low（旧协议兼容）。
                    if (element.TryGetProperty("file_id", out var fid) && fid.ValueKind == JsonValueKind.String)
                    {
                        string hex = fid.GetString() ?? "";
                        if (hex.Length == 32)
                        {
                            try
                            {
                                var fileId = Convert.FromHexString(hex);
                                identity.FileId = fileId;
                                identity.FileIdHigh = ReadUInt64LittleEndian(fileId, 0);
                                identity.FileIdLow = ReadUInt64LittleEndian(fileId, 8);
                            }
                            catch
                            {
                                identity.Valid = false;
                            }
                        }
                        else
                        {
                            identity.Valid = false;
                        }
                    }
                    else
                    {
                        identity.FileIdHigh = element.TryGetProperty("file_id_high", out var fh) ? fh.GetUInt64() : 0;
                        identity.FileIdLow = element.TryGetProperty("file_id_low", out var fl) ? fl.GetUInt64() : 0;
                        var fileId = new byte[16];
                        WriteUInt64LittleEndian(fileId, 0, identity.FileIdHigh);
                        WriteUInt64LittleEndian(fileId, 8, identity.FileIdLow);
                        identity.FileId = fileId;
                    }
                    list.Add(identity);
                }
                catch
                {
                    // 单条身份解析失败：该卷会在回收时被判为“无法确认身份”而保留
                }
            }
            return list;
        }

        private static ulong ReadUInt64LittleEndian(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (int i = 7; i >= 0; i--) value = (value << 8) | bytes[offset + i];
            return value;
        }

        private static void WriteUInt64LittleEndian(byte[] bytes, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++) bytes[offset + i] = (byte)(value >> (8 * i));
        }

        private static bool TryReadRequired(JsonElement root, string name, out string value)
        {
            value = "";
            if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
            value = element.GetString() ?? "";
            return true;
        }

        private void FailWithoutCommit(string detail, bool keepStaging)
        {
            MainStatusText.Text = "解压未完成";
            PathInfoText.Text = detail;
            PathInfoText.ToolTip = detail;
            ExtractProgressBar.Foreground = (Brush)Resources["Win11Red"];
            RunningActionPanel.Visibility = Visibility.Collapsed;
            FinishedActionPanel.Visibility = Visibility.Visible;
            TitleText.Text = $"解压失败 - {Path.GetFileName(_archivePath)}";

            if (!keepStaging && _plan != null)
            {
                ArchiveTaskPlanner.CleanupStaging(_plan);
            }

            Topmost = true;
            Activate();
            var hwnd = new WindowInteropHelper(this).Handle;
            SystemEffects.FlashWindowForError(hwnd);
        }

        private static string DescribeFailure(string errorReason, int exitCode, string status)
        {
            switch (errorReason)
            {
                case "password_or_data_error":
                    return "解压失败：密码错误或数据损坏，源压缩包已保留。";
                case "wrong_password":
                    return "解压失败：归档中至少有一个条目的密码不匹配，源压缩包已保留。";
                case "crc_error":
                    return "解压失败：文件数据校验 (CRC) 错误，已放弃交付，源压缩包已保留。";
                case "data_error":
                    return "解压失败：条目数据损坏或归档不完整，已放弃交付，源压缩包已保留。";
                case "unsupported_method":
                    return "解压失败：压缩算法不受支持，已放弃交付，源压缩包已保留。";
                case "corrupted_archive":
                case "unrecognized_or_corrupted_inner_archive":
                case "corrupted_inner_archive":
                    return "解压失败：归档已损坏或缺少后续分卷，已放弃交付，源压缩包已保留。";
                case "inner_not_tar":
                    return "解压失败：复合 TAR 归档的内层不是有效的 TAR 内容，已放弃交付，源压缩包已保留。";
                case "inner_payload_missing":
                    return "解压失败：复合 TAR 归档缺少可展开的内层载荷，已放弃交付，源压缩包已保留。";
                case "insufficient_disk_space":
                    return "解压失败：目标磁盘可用空间不足，已安全终止，源压缩包已保留。";
                case "prompt_timeout":
                    return "解压失败：等待输入超时，源压缩包已保留。";
                case "output_budget_exceeded":
                    return "解压失败：实际写出数据超出预算（疑似声明大小失真），已安全终止，源压缩包已保留。";
                case "extract_error":
                    return "解压失败：写入目标文件时出错，已放弃交付，源压缩包已保留。";
                default:
                    return $"解压未完整结束 (状态: {status}, Worker 退出码: {exitCode})，源压缩包已安全保留。";
            }
        }

        private void CleanupManifest()
        {
            try
            {
                if (!string.IsNullOrEmpty(_manifestPath) && File.Exists(_manifestPath))
                {
                    File.Delete(_manifestPath);
                }
            }
            catch
            {
                // 清单清理失败不影响交付结果
            }
        }

        private void OnOpenFolderClick(object sender, RoutedEventArgs e)
        {
            if (_plan != null && Directory.Exists(_plan.FinalTargetDirectory))
            {
                Process.Start("explorer.exe", $"\"{_plan.FinalTargetDirectory}\"");
            }
            Close();
        }

        private void OnFinishedCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SendWorkerMessage(string json)
        {
            try
            {
                if (_workerInput != null)
                {
                    _workerInput.WriteLine(json);
                    _workerInput.Flush();
                }
            }
            catch { }
        }

        private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_finishedHandled && !_isCompleted)
            {
                _closeRequested = true;
                e.Cancel = true;
                return;
            }

            _speedTimer.Stop();
            if (!_isCompleted && _workerProcess != null)
            {
                e.Cancel = true;
                if (_closingWorker) return;
                _closingWorker = true;
                SendWorkerMessage("{\"type\":\"cancel\"}");
                await Task.Run(StopWorkerAndCleanup);
                _isCompleted = true;
                Close();
                return;
            }

            if (!_isCompleted) StopWorkerAndCleanup();
            _isCompleted = true;
            _jobManager?.Dispose();
        }
    }
}
