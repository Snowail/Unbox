using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        [DllImport("kernel32.dll")]
        private static extern uint GetACP();

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

        // 非阻塞 UI 调度标志
        private volatile bool _isUiUpdatePending = false;
        private string _pendingCurrentFile = "";

        // 编码选择状态
        private uint _pendingPreferredCodePage = 0;

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
            TitleText.Text = "已完成 0%";
            MainStatusText.Text = "已完成 0%";

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

        private Task StartExtractionAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    string appDir = AppDomain.CurrentDomain.BaseDirectory;
                    string workerExe = Path.Combine(appDir, "UnzipWorker.exe");
                    if (!File.Exists(workerExe))
                    {
                        string altWorker = Path.GetFullPath(Path.Combine(appDir, @"..\..\..\..\bin\UnzipWorker.exe"));
                        if (File.Exists(altWorker)) workerExe = altWorker;
                    }

                    if (!File.Exists(workerExe))
                    {
                        Dispatcher.Invoke(() => TriggerError("启动失败", "找不到解压工作组件: UnzipWorker.exe"));
                        return;
                    }

                    if (!Directory.Exists(_plan!.StagingDirectory))
                    {
                        Directory.CreateDirectory(_plan.StagingDirectory);
                    }

                    // 交付清单放在暂存目录之外，避免进入最终交付树
                    _manifestPath = Path.Combine(_plan.DestinationParentDir, $".unbox_manifest_{Guid.NewGuid():N}.json");

                    uint codePage = _settings.ArchiveCodePage != 0
                        ? (uint)_settings.ArchiveCodePage
                        : GetACP();
                    string cpArg = (codePage != 0 && codePage != 65001) ? $" --cp {codePage}" : "";

                    var psi = new ProcessStartInfo
                    {
                        FileName = workerExe,
                        Arguments = $"--archive \"{_plan.SourceArchivePath}\" --staging \"{_plan.StagingDirectory}\" " +
                                    $"--target-root \"{_plan.TargetRootDirName}\"{cpArg} --manifest \"{_manifestPath}\"",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardInputEncoding = System.Text.Encoding.UTF8,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8,
                        CreateNoWindow = true
                    };

                    _jobManager = new JobObjectManager();
                    _workerProcess = new Process { StartInfo = psi };
                    if (!_workerProcess.Start())
                    {
                        Dispatcher.Invoke(() => TriggerError("启动失败", "无法启动工作进程: " + workerExe));
                        return;
                    }
                    if (!_jobManager.AssignProcess(_workerProcess))
                    {
                        Debug.WriteLine("[WARN] Failed to assign worker process to JobObject");
                    }

                    _workerInput = _workerProcess.StandardInput;

                    ListenWorkerOutput(_workerProcess.StandardOutput);

                    _workerProcess.WaitForExit();
                    if (!_finishedHandled && !_workerErrorShown && !_isCompleted)
                    {
                        string stdErr = _workerProcess.StandardError.ReadToEnd();
                        int exitCode = _workerProcess.ExitCode;
                        Dispatcher.Invoke(() =>
                        {
                            TriggerError("解压中断", $"工作进程异常退出 (退出代码: {exitCode})" +
                                                     (string.IsNullOrEmpty(stdErr) ? "" : ": " + stdErr));
                        });
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => TriggerError("解压启动出错", ex.Message));
                }
            });
        }

        private void ListenWorkerOutput(StreamReader reader)
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

                    if (type == "progress")
                    {
                        _pendingCurrentFile = root.TryGetProperty("current_file", out var cf) ? (cf.GetString() ?? "") : "";
                        _completedBytes = root.TryGetProperty("completed_bytes", out var cb) ? cb.GetInt64() : _completedBytes;
                        long reportedTotal = root.TryGetProperty("total_bytes", out var tb) ? tb.GetInt64() : 0;
                        if (reportedTotal > 0) _totalBytes = reportedTotal;

                        if (!_isUiUpdatePending)
                        {
                            _isUiUpdatePending = true;
                            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                            {
                                _isUiUpdatePending = false;
                                UpdateProgressUi(_pendingCurrentFile);
                            });
                        }
                    }
                    else
                    {
                        Dispatcher.Invoke(() => HandleWorkerMessage(type, root));
                    }
                }
                catch
                {
                    // 单行解析失败不影响其余状态流
                }
            }
        }

        private void HandleWorkerMessage(string type, JsonElement root)
        {
            switch (type)
            {
                case "init":
                    _totalItems = root.TryGetProperty("total_items", out var ti) ? ti.GetInt32() : _totalItems;
                    _totalBytes = root.TryGetProperty("total_bytes", out var tb) ? tb.GetInt64() : _totalBytes;
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

                case "error":
                    _workerErrorShown = true;
                    string msg = root.TryGetProperty("message", out var m) ? (m.GetString() ?? "未知错误") : "未知错误";
                    TriggerError("解压失败", msg);
                    break;
            }
        }

        private void TriggerError(string title, string message)
        {
            MainStatusText.Text = title;
            PathInfoText.Text = message;
            PathInfoText.ToolTip = message;
            ExtractProgressBar.Foreground = (Brush)Resources["Win11Red"];
            RunningActionPanel.Visibility = Visibility.Collapsed;
            FinishedActionPanel.Visibility = Visibility.Visible;

            Topmost = true;
            Activate();
            Focus();
            var hwnd = new WindowInteropHelper(this).Handle;
            SystemEffects.FlashWindowForError(hwnd);
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

            if (_totalBytes <= 0)
            {
                ExtractProgressBar.IsIndeterminate = true;
                return;
            }

            ExtractProgressBar.IsIndeterminate = false;
            double progress = Math.Clamp((double)_completedBytes / _totalBytes, 0.0, 1.0);
            double pct = progress * 100.0;
            ExtractProgressBar.Value = pct;

            string statusText = _isPaused ? $"已暂停 - 已完成 {pct:0}%" : $"已完成 {pct:0}%";
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
            if (_isCompleted || _isPaused) return;
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
            InvalidItemsListText.Text = string.Join("\n", previewLines);

            ModalTitleText.Text = "文件名包含非法字符";
            ModalRecycleAlertPanel.Visibility = Visibility.Collapsed;
            ModalPasswordPanel.Visibility = Visibility.Collapsed;
            ModalEncodingPanel.Visibility = Visibility.Collapsed;
            ModalInvalidCharsPanel.Visibility = Visibility.Visible;
            ModalOverlay.Visibility = Visibility.Visible;
        }

        private void OnInvalidCharsSubmitClick(object sender, RoutedEventArgs e)
        {
            string action = RadioBatchRename.IsChecked == true ? "batch_rename" : "skip";

            if (RememberInvalidCharsCheck.IsChecked == true)
            {
                _settings.InvalidCharsAction = action;
                _settings.RememberInvalidCharsAction = true;
                ConfigManager.Save(_settings);
            }

            ModalOverlay.Visibility = Visibility.Collapsed;
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

            _pendingPreferredCodePage = options[0].CodePage;

            // 已有记忆偏好时直接静默回答，不再打断用户
            if (_settings.RememberArchiveCodePage && _settings.ArchiveCodePage != 0)
            {
                SendWorkerMessage($"{{\"type\":\"encoding_response\",\"cp\":{_settings.ArchiveCodePage}}}");
                return;
            }

            EncodingOptionsPanel.Children.Clear();
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
                radio.Checked += OnEncodingOptionChecked;
                EncodingOptionsPanel.Children.Add(radio);

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
                    EncodingOptionsPanel.Children.Add(sampleText);
                }
                first = false;
            }

            RememberEncodingCheck.IsChecked = false;
            ModalTitleText.Text = "选择文件名编码";
            ModalRecycleAlertPanel.Visibility = Visibility.Collapsed;
            ModalPasswordPanel.Visibility = Visibility.Collapsed;
            ModalInvalidCharsPanel.Visibility = Visibility.Collapsed;
            ModalEncodingPanel.Visibility = Visibility.Visible;
            ModalOverlay.Visibility = Visibility.Visible;
        }

        private void OnEncodingOptionChecked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: uint cp })
            {
                _pendingPreferredCodePage = cp;
            }
        }

        private void OnEncodingSubmitClick(object sender, RoutedEventArgs e)
        {
            uint selected = _pendingPreferredCodePage;
            foreach (var child in EncodingOptionsPanel.Children)
            {
                if (child is RadioButton { IsChecked: true, Tag: uint cp })
                {
                    selected = cp;
                    break;
                }
            }

            if (RememberEncodingCheck.IsChecked == true && selected != 0)
            {
                _settings.ArchiveCodePage = (int)selected;
                _settings.RememberArchiveCodePage = true;
                ConfigManager.Save(_settings);
            }

            ModalOverlay.Visibility = Visibility.Collapsed;
            SendWorkerMessage($"{{\"type\":\"encoding_response\",\"cp\":{selected}}}");
        }

        // 密码弹窗
        private void ShowPasswordDialog()
        {
            ModalTitleText.Text = "输入密码";
            ModalRecycleAlertPanel.Visibility = Visibility.Collapsed;
            ModalInvalidCharsPanel.Visibility = Visibility.Collapsed;
            ModalEncodingPanel.Visibility = Visibility.Collapsed;
            ModalPasswordPanel.Visibility = Visibility.Visible;
            PasswordInputBox.Password = "";
            ModalOverlay.Visibility = Visibility.Visible;
            PasswordInputBox.Focus();
        }

        private void OnPasswordSubmitClick(object sender, RoutedEventArgs e)
        {
            string pwd = PasswordInputBox.Password;
            ModalOverlay.Visibility = Visibility.Collapsed;
            var payload = new { type = "password_response", password = pwd };
            SendWorkerMessage(JsonSerializer.Serialize(payload));
        }

        private void OnPasswordCancelClick(object sender, RoutedEventArgs e)
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
            var payload = new { type = "password_response", canceled = true };
            SendWorkerMessage(JsonSerializer.Serialize(payload));
            OnCancelClick(sender, e);
        }

        private void OnPasswordInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                OnPasswordSubmitClick(sender, new RoutedEventArgs());
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                OnPasswordCancelClick(sender, new RoutedEventArgs());
            }
        }

        // D2 移入回收站告警与偏好记忆
        private void OnRecycleCheckBoxClick(object sender, RoutedEventArgs e)
        {
            if (RecycleCheckBox.IsChecked == true)
            {
                ModalTitleText.Text = "移入回收站提示";
                ModalInvalidCharsPanel.Visibility = Visibility.Collapsed;
                ModalPasswordPanel.Visibility = Visibility.Collapsed;
                ModalEncodingPanel.Visibility = Visibility.Collapsed;
                ModalRecycleAlertPanel.Visibility = Visibility.Visible;
                ModalOverlay.Visibility = Visibility.Visible;
            }
            _settings.RecycleOnSuccess = RecycleCheckBox.IsChecked == true;
            ConfigManager.Save(_settings);
        }

        private void OnRecycleAlertAckClick(object sender, RoutedEventArgs e)
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
        }

        // 暂停 / 继续
        private void OnPauseResumeClick(object sender, RoutedEventArgs e)
        {
            if (_isCompleted) return;

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
            if (!_isCompleted)
            {
                SendWorkerMessage("{\"type\":\"cancel\"}");
                if (_plan != null)
                {
                    ArchiveTaskPlanner.CleanupStaging(_plan);
                }
            }
            Close();
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
            double targetCollapsedOpacity = _isDetailsExpanded ? 1.0 : 0.0;

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
            _isCompleted = true;
            _speedTimer.Stop();

            // 必需字段一律不得缺省；缺失即视为失败，绝不默认成功
            if (!TryReadRequired(root, "status", out string status) ||
                !TryReadRequired(root, "manifest", out string manifestPath) ||
                !TryReadRequired(root, "delivery_root", out string deliveryRoot) ||
                !root.TryGetProperty("total_items", out _) ||
                !root.TryGetProperty("extracted_items", out _) ||
                !root.TryGetProperty("skipped_items", out _) ||
                !root.TryGetProperty("completed_bytes", out _))
            {
                FailWithoutCommit("Worker 完成消息不完整，已中止交付并保留源包。", true);
                return;
            }

            int policyFiltered = root.TryGetProperty("policy_filtered_items", out var pf) ? pf.GetInt32() : 0;
            int skippedItems = root.GetProperty("skipped_items").GetInt32();
            int unsafeItems = root.TryGetProperty("unsafe_items", out var ui) ? ui.GetInt32() : 0;
            string errorReason = root.TryGetProperty("error_reason", out var er) ? (er.GetString() ?? "") : "";
            bool emptyArchive = root.TryGetProperty("empty_archive", out var ea) && ea.ValueKind == JsonValueKind.True;
            bool manifestOk = !root.TryGetProperty("manifest_ok", out var mo) || mo.ValueKind == JsonValueKind.True;

            var volumes = ParseVolumeIdentities(root);

            // 等待工作进程彻底退出并获取退出码
            bool workerExited = false;
            if (_workerProcess != null)
            {
                if (!_workerProcess.HasExited)
                {
                    workerExited = await Task.Run(() => _workerProcess.WaitForExit(15000));
                }
                else
                {
                    workerExited = true;
                }
            }
            if (!workerExited && _workerProcess != null)
            {
                try { _workerProcess.Kill(); } catch { }
            }
            int exitCode = (workerExited && _workerProcess != null) ? _workerProcess.ExitCode : -1;

            // 硬失败：状态、退出码、错误原因、清单任何一项异常都不得提交
            bool hardFailure = status == "failed"
                               || !string.IsNullOrEmpty(errorReason)
                               || !manifestOk
                               || !workerExited;
            if (status == "ok" && exitCode != 0) hardFailure = true;
            if (status == "partial" && exitCode != 0 && exitCode != 11) hardFailure = true;

            if (hardFailure)
            {
                FailWithoutCommit(DescribeFailure(errorReason, exitCode, status), false);
                CleanupManifest();
                return;
            }

            // 读取交付清单并做逐项对账 + 提交（在后台线程执行，避免阻塞 UI）
            DeliveryManifest manifest;
            try
            {
                manifest = DeliveryManifest.Load(manifestPath);
            }
            catch (Exception ex)
            {
                FailWithoutCommit("无法读取交付清单，已中止交付并保留源包：" + ex.Message, true);
                CleanupManifest();
                return;
            }

            bool recycleRequested = RecycleCheckBox.IsChecked == true && status == "ok";
            var request = new CompletionRequest
            {
                Plan = _plan!,
                DeliveryRootRelative = deliveryRoot,
                Manifest = manifest,
                RecycleRequested = recycleRequested,
                Volumes = volumes,
                PropagateZoneIdentifier = true
            };

            CompletionResult result;
            try
            {
                result = await Task.Run(() => ExtractionCompleter.Complete(request));
            }
            catch (Exception ex)
            {
                FailWithoutCommit("完成处理异常，已保留暂存目录：" + ex.Message, true);
                CleanupManifest();
                return;
            }

            CleanupManifest();

            if (!result.Delivered)
            {
                string detail = result.FailureReason;
                if (!string.IsNullOrEmpty(result.KeptStagingDirectory))
                {
                    detail += $"\n暂存目录已保留以便恢复：{result.KeptStagingDirectory}";
                }
                FailWithoutCommit(detail, result.KeptStagingDirectory.Length > 0);
                return;
            }

            // 交付成功
            ExtractProgressBar.Value = 100;
            DetailEtaText.Text = "完成";
            DetailRemainingText.Text = "0 (0 B)";
            SpeedGraph.PushSample(1.0, _currentSpeedMBps, _displayedSpeedMBps);

            string notes = result.Summary;
            if (policyFiltered > 0) notes += $"；已按产品规则过滤 {policyFiltered} 个 macOS 元数据项";
            if (skippedItems > 0) notes += $"；{skippedItems} 项被跳过";
            if (unsafeItems > 0) notes += $"；{unsafeItems} 项因路径不安全被拒绝";
            if (emptyArchive) notes = "归档为空，已创建空目标目录";
            if (result.ZoneMarked > 0) notes += $"；已为 {result.ZoneMarked} 个文件保留下载来源标记";

            bool partial = status == "partial" || skippedItems > 0 || unsafeItems > 0;
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
                SystemEffects.PlayFinishedSound();
                UpdateProgressUi();
                return;
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
                SystemEffects.PlayFinishedSound();
                return;
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

            SystemEffects.PlayFinishedSound();

            await Task.Delay(800);
            Close();
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
                    list.Add(new VolumeIdentity
                    {
                        Path = element.TryGetProperty("path", out var p) ? (p.GetString() ?? "") : "",
                        VolumeSerial = element.TryGetProperty("volume", out var v) ? v.GetUInt64() : 0,
                        FileIdHigh = element.TryGetProperty("file_id_high", out var fh) ? fh.GetUInt64() : 0,
                        FileIdLow = element.TryGetProperty("file_id_low", out var fl) ? fl.GetUInt64() : 0,
                        Size = element.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                        LastWriteTime = element.TryGetProperty("mtime", out var m) ? m.GetInt64() : 0,
                        Valid = element.TryGetProperty("valid", out var va) && va.ValueKind == JsonValueKind.True
                    });
                }
                catch
                {
                    // 单条身份解析失败：该卷会在回收时被判为“无法确认身份”而保留
                }
            }
            return list;
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

        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _speedTimer.Stop();
            try
            {
                if (_workerProcess != null && !_workerProcess.HasExited)
                {
                    SendWorkerMessage("{\"type\":\"cancel\"}");
                    _workerProcess.Kill();
                }
            }
            catch { }
            _jobManager?.Dispose();
            CleanupManifest();
        }
    }
}
