using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
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

        private UserSettings _settings;
        private bool _isDarkMode = true;
        private bool _isPaused = false;
        private bool _isCompleted = false;
        private bool _hasSkippedItems = false;
        private bool _isDetailsExpanded = false;

        // 精确匹配原生 Win11 尺寸：宽度 480，折叠 156，展开 306，全系统 DPI 缩放下绝不截断
        private const double CollapsedHeight = 156.0;
        private const double ExpandedHeight = 306.0;
        private const double ExpandedDetailsHeight = 158.0;

        // 速度采样与指标
        private readonly DispatcherTimer _speedTimer;
        private long _lastBytes = 0;
        private DateTime _lastTime = DateTime.UtcNow;
        private DateTime _startTime = DateTime.UtcNow;
        private double _currentSpeedMBps = 0.0;
        private double _displayedSpeedMBps = 0.0;
        private DateTime _lastSpeedDisplayTime = DateTime.UtcNow;
        private long _totalBytes = 0;
        private long _completedBytes = 0;
        private int _totalItems = 1;

        // 非阻塞 UI 调度标志
        private volatile bool _isUiUpdatePending = false;
        private string _pendingCurrentFile = "";

        public MainWindow(string archivePath)
        {
            InitializeComponent();
            _archivePath = archivePath;
            _settings = ConfigManager.Load();

            ApplyWin11Theme();
            InitWindowCorner();

            // 恢复配置与记忆展开状态
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

            // 周期性速度与 ETA 辅助刷新定时器 (100ms)
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

                // 暗色模式：运行中为 Win11 亮青蓝 (#4CC2FF)，暂停为亮金黄 (#FCE100)
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

                // 浅色模式：运行中为 Win11 翠绿 (#107C41)，暂停为琥珀金 (#C19C00)
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

            // 规划目录结构 (D1 规则)
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
                        Dispatcher.Invoke(() =>
                        {
                            TriggerError("启动失败", "找不到解压工作组件: UnzipWorker.exe");
                        });
                        return;
                    }

                    // 创建独占暂存目录
                    if (!Directory.Exists(_plan!.StagingDirectory))
                    {
                        Directory.CreateDirectory(_plan.StagingDirectory);
                    }

                    uint acp = GetACP();
                    string cpArg = (acp != 0 && acp != 65001) ? $" --cp {acp}" : "";
                    var psi = new ProcessStartInfo
                    {
                        FileName = workerExe,
                        Arguments = $"--archive \"{_plan.SourceArchivePath}\" --staging \"{_plan.StagingDirectory}\" --target-root \"{_plan.TargetRootDirName}\"{cpArg}",
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

                    // 管道读完后等待工作进程退出
                    _workerProcess.WaitForExit();
                    if (!_isCompleted && !_isPaused)
                    {
                        string stdErr = _workerProcess.StandardError.ReadToEnd();
                        int exitCode = _workerProcess.ExitCode;
                        Dispatcher.Invoke(() =>
                        {
                            TriggerError("解压中断", $"工作进程异常退出 (退出代码: {exitCode}) {(string.IsNullOrEmpty(stdErr) ? "" : ": " + stdErr)}");
                        });
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        TriggerError("解压启动出错", ex.Message);
                    });
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
                        // 高频 progress 消息：异步非阻塞投递，保证 UI 线程绝对流畅
                        _pendingCurrentFile = root.GetProperty("current_file").GetString() ?? "";
                        _completedBytes = root.GetProperty("completed_bytes").GetInt64();
                        long reportedTotal = root.GetProperty("total_bytes").GetInt64();
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
                        // 状态类消息：立即同步处理
                        Dispatcher.Invoke(() => HandleWorkerMessage(type, root));
                    }
                }
                catch { }
            }
        }

        private void HandleWorkerMessage(string type, JsonElement root)
        {
            switch (type)
            {
                case "init":
                    _totalItems = root.GetProperty("total_items").GetInt32();
                    _totalBytes = root.GetProperty("total_bytes").GetInt64();
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

                case "password_request":
                    ShowPasswordDialog();
                    break;

                case "finished":
                    HandleFinished(root);
                    break;

                case "error":
                    string msg = root.GetProperty("message").GetString() ?? "未知错误";
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
                string cleanFile = FixPotentialMojibake(currentFile);
                DetailFileNameText.Text = cleanFile;
                DetailFileNameText.ToolTip = cleanFile;
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

            // 实时瞬时速度计算 (EMA 滤波)
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

            // 速度数值每 500ms 刷新一次，彻底解决数字一直跳跃晃动的问题
            if ((now - _lastSpeedDisplayTime).TotalMilliseconds >= 500 || _displayedSpeedMBps <= 0.0 || _isPaused)
            {
                _displayedSpeedMBps = _currentSpeedMBps;
                _lastSpeedDisplayTime = now;
            }

            // 实时向面积图推点，波形水平连续展开，数值平滑不跳动
            SpeedGraph.PushSample(progress, _currentSpeedMBps, _displayedSpeedMBps);

            // 实时更新剩余项目与剩余大小 (1:1 Win11 格式: 8 (5.04 GB))
            long remainingBytes = Math.Max(0, _totalBytes - _completedBytes);
            int estimatedRemainingItems = Math.Max(0, (int)Math.Ceiling((1.0 - progress) * _totalItems));
            if (remainingBytes > 0 && estimatedRemainingItems == 0) estimatedRemainingItems = 1;
            DetailRemainingText.Text = $"{estimatedRemainingItems} ({FormatBytes(remainingBytes)})";

            // 实时更新剩余时间，绝不卡死在“正在计算...”
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

            var itemsArray = root.GetProperty("items");
            var previewLines = new List<string>();
            foreach (var it in itemsArray.EnumerateArray())
            {
                string raw = it.GetProperty("raw").GetString() ?? "";
                string sanitized = it.GetProperty("sanitized").GetString() ?? "";
                previewLines.Add($"• 原名: {raw}  ->  建议: {sanitized}");
            }
            InvalidItemsListText.Text = string.Join("\n", previewLines);

            ModalTitleText.Text = "文件名包含非法字符";
            ModalRecycleAlertPanel.Visibility = Visibility.Collapsed;
            ModalPasswordPanel.Visibility = Visibility.Collapsed;
            ModalInvalidCharsPanel.Visibility = Visibility.Visible;
            ModalOverlay.Visibility = Visibility.Visible;
        }

        private void OnInvalidCharsSubmitClick(object sender, RoutedEventArgs e)
        {
            string action = RadioBatchRename.IsChecked == true ? "batch_rename" : "skip";
            if (RadioSkipInvalid.IsChecked == true)
            {
                _hasSkippedItems = true;
            }

            if (RememberInvalidCharsCheck.IsChecked == true)
            {
                _settings.InvalidCharsAction = action;
                _settings.RememberInvalidCharsAction = true;
                ConfigManager.Save(_settings);
            }

            ModalOverlay.Visibility = Visibility.Collapsed;
            SendWorkerMessage($"{{\"type\":\"invalid_chars_response\",\"action\":\"{action}\"}}");
        }

        // 密码弹窗
        private void ShowPasswordDialog()
        {
            ModalTitleText.Text = "输入密码";
            ModalRecycleAlertPanel.Visibility = Visibility.Collapsed;
            ModalInvalidCharsPanel.Visibility = Visibility.Collapsed;
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

            // 记忆用户偏好
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

            // 窗口高度平滑动画
            var animWindow = new DoubleAnimation(targetWindowHeight, duration) { EasingFunction = ease };
            BeginAnimation(HeightProperty, animWindow);

            // 折叠条容器动画 (展开时收起进度条)
            CollapsedContainer.BeginAnimation(HeightProperty, new DoubleAnimation(targetCollapsedHeight, duration) { EasingFunction = ease });
            CollapsedContainer.BeginAnimation(OpacityProperty, new DoubleAnimation(targetCollapsedOpacity, duration) { EasingFunction = ease });

            // 展开图表容器动画
            ExpandedContainer.BeginAnimation(HeightProperty, new DoubleAnimation(targetExpandedHeight, duration) { EasingFunction = ease });
            ExpandedContainer.BeginAnimation(OpacityProperty, new DoubleAnimation(targetExpandedOpacity, duration) { EasingFunction = ease });
        }

        // 完成处理 (严格核验全部条件：Worker 退出码 0、无错误、无跳过、输出落盘完整，方可提交与回收)
        private async void HandleFinished(JsonElement root)
        {
            _isCompleted = true;
            _speedTimer.Stop();

            string status = root.TryGetProperty("status", out var stElem) ? (stElem.GetString() ?? "ok") : "ok";
            _completedBytes = root.TryGetProperty("completed_bytes", out var cbElem) ? cbElem.GetInt64() : _totalBytes;
            string errorReason = root.TryGetProperty("error_reason", out var erElem) ? (erElem.GetString() ?? "") : "";
            int skippedItems = root.TryGetProperty("skipped_items", out var skpElem) ? skpElem.GetInt32() : 0;
            int totalItemsReported = root.TryGetProperty("total_items", out var tiElem) ? tiElem.GetInt32() : 0;
            int extractedItemsReported = root.TryGetProperty("extracted_items", out var eiElem) ? eiElem.GetInt32() : 0;

            var openedVols = new List<string>();
            if (root.TryGetProperty("opened_volumes", out var volsElem) && volsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in volsElem.EnumerateArray())
                {
                    string? s = v.GetString();
                    if (!string.IsNullOrEmpty(s)) openedVols.Add(s);
                }
            }
            if (openedVols.Count == 0 && _plan != null)
            {
                openedVols.Add(_plan.SourceArchivePath);
            }

            // 等待工作进程彻底退出并获取退出码，杜绝进程挂死与文件锁未释放
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

            // 严格综合判定：必须同时满足状态 ok、退出码 0、无错误原因、无跳过项、进程正常退出
            bool isFailedOrPartial = (status != "ok") || (exitCode != 0) || !string.IsNullOrEmpty(errorReason) || _hasSkippedItems || (skippedItems > 0) || !workerExited;

            // 严格输出落盘核验：确保解压文件真实写入目标磁盘
            if (!isFailedOrPartial && _plan != null)
            {
                if (totalItemsReported > 0 || extractedItemsReported > 0 || _totalBytes > 0)
                {
                    if (!Directory.Exists(_plan.StagingDirectory) || !Directory.EnumerateFileSystemEntries(_plan.StagingDirectory).Any())
                    {
                        isFailedOrPartial = true;
                        errorReason = "output_verification_failed";
                    }
                }
            }

            if (isFailedOrPartial)
            {
                // 立即清理暂存残损目录，绝不提交正式目录，绝不回收源包
                if (_plan != null && Directory.Exists(_plan.StagingDirectory))
                {
                    try { Directory.Delete(_plan.StagingDirectory, true); } catch { }
                }

                MainStatusText.Text = "解压未完成";
                if (errorReason == "crc_error")
                {
                    PathInfoText.Text = "解压失败：文件数据校验 (CRC) 错误，源压缩包已保留。";
                }
                else if (errorReason == "data_error")
                {
                    PathInfoText.Text = "解压失败：条目数据损坏或归档不完整，源压缩包已保留。";
                }
                else if (errorReason == "unsupported_method")
                {
                    PathInfoText.Text = "解压失败：压缩算法不受支持，源压缩包已保留。";
                }
                else if (errorReason == "corrupted_archive" || errorReason == "unrecognized_or_corrupted_inner_archive")
                {
                    PathInfoText.Text = "解压失败：归档已损坏或缺少后续分卷，源压缩包已保留。";
                }
                else if (errorReason == "insufficient_disk_space")
                {
                    PathInfoText.Text = "解压失败：目标磁盘可用空间不足，已安全终止。";
                }
                else if (_hasSkippedItems || skippedItems > 0)
                {
                    PathInfoText.Text = "部分条目因字符非法或路径不安全已被跳过，源压缩包已保留。";
                }
                else
                {
                    PathInfoText.Text = $"解压未完整结束 (Worker 退出码: {exitCode})，源压缩包已安全保留。";
                }

                PathInfoText.ToolTip = PathInfoText.Text;
                TitleText.Text = $"解压失败 - {Path.GetFileName(_archivePath)}";
                RunningActionPanel.Visibility = Visibility.Collapsed;
                FinishedActionPanel.Visibility = Visibility.Visible;
                return;
            }

            // 提交暂存目录
            if (_plan != null && !ArchiveTaskPlanner.CommitStagingDirectory(_plan, out string commitErr))
            {
                TriggerError("提交最终目录失败", commitErr);
                return;
            }

            // 来源标记传递 (Mark of the Web)
            if (_plan != null && !string.IsNullOrEmpty(_plan.SourceArchivePath) && !string.IsNullOrEmpty(_plan.FinalTargetDirectory))
            {
                PropagateZoneIdentifier(_plan.SourceArchivePath, _plan.FinalTargetDirectory);
            }

            // 仅在完全通过时才按用户选择回收源卷
            string recycleErrMsg = "";
            bool recycleFailed = false;
            if (RecycleCheckBox.IsChecked == true && openedVols.Count > 0)
            {
                var (recOk, recMsg) = ShellRecycleBin.RecycleOpenedVolumes(openedVols);
                if (!recOk)
                {
                    recycleFailed = true;
                    recycleErrMsg = recMsg;
                }
            }

            if (recycleFailed)
            {
                // 阻断项修复：回收失败必须明确向用户反馈，窗口保持打开供用户核实，严禁静默吞错！
                MainStatusText.Text = "解压完成（源文件回收失败）";
                MainStatusText.Foreground = (Brush)FindResource("AccentOrangeBrush");
                DetailEtaText.Text = "已完成";
                DetailRemainingText.Text = "源文件保留";
                PathInfoText.Text = $"解压已完成，但无法移入回收站: {recycleErrMsg}。源压缩包已保留在原位。";
                PathInfoText.ToolTip = PathInfoText.Text;
                RunningActionPanel.Visibility = Visibility.Collapsed;
                FinishedActionPanel.Visibility = Visibility.Visible;
                SystemEffects.PlayFinishedSound();
            }
            else
            {
                ExtractProgressBar.Value = 100;
                TitleText.Text = "已完成 100%";
                MainStatusText.Text = "已完成 100%";
                DetailEtaText.Text = "完成";
                DetailRemainingText.Text = "0 (0 B)";
                SpeedGraph.PushSample(1.0, _currentSpeedMBps, _displayedSpeedMBps);

                SystemEffects.PlayFinishedSound();

                // 停留 800ms，让用户看清完成动画与 100% 状态
                await Task.Delay(800);
                Close();
            }
        }

        // 来源标记传递：若源压缩包带有 Windows 下载安全标记 (:Zone.Identifier)，将其传递给所有解压文件
        private static void PropagateZoneIdentifier(string sourcePath, string targetDirectory)
        {
            try
            {
                string zoneStreamPath = sourcePath + ":Zone.Identifier";
                if (!File.Exists(zoneStreamPath)) return;

                byte[] zoneData = File.ReadAllBytes(zoneStreamPath);
                if (zoneData == null || zoneData.Length == 0) return;

                foreach (var filePath in Directory.EnumerateFiles(targetDirectory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.WriteAllBytes(filePath + ":Zone.Identifier", zoneData);
                    }
                    catch
                    {
                        // 忽略单个文件的非致命属性写入异常
                    }
                }
            }
            catch
            {
                // 来源标记传递防御性异常隔离
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
        }

        // 智能修复可能残留的乱码 (如 UTF-8 字节被误按系统代码页如 GBK 解析的字形，或 7-Zip PUA 私有区映射字符)
        public static string FixPotentialMojibake(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            }
            catch { }

            // 1. 如果包含 7z PUA 私有区字符 (0xEF80 - 0xEFFF)，说明是未解码原始单字节映射
            bool hasPua = false;
            foreach (char c in text)
            {
                if (c >= 0xEF80 && c <= 0xEFFF)
                {
                    hasPua = true;
                    break;
                }
            }

            if (hasPua)
            {
                try
                {
                    var rawBytes = new List<byte>();
                    foreach (char c in text)
                    {
                        if (c >= 0xEF80 && c <= 0xEFFF)
                        {
                            rawBytes.Add((byte)(c - 0xEF00));
                        }
                        else if (c < 0x80)
                        {
                            rawBytes.Add((byte)c);
                        }
                        else
                        {
                            byte[] u8 = System.Text.Encoding.UTF8.GetBytes(new char[] { c });
                            rawBytes.AddRange(u8);
                        }
                    }

                    var gbk = System.Text.Encoding.GetEncoding("GBK");
                    string decoded = gbk.GetString(rawBytes.ToArray());
                    if (!string.IsNullOrEmpty(decoded) && !decoded.Contains('\uFFFD'))
                    {
                        return decoded;
                    }
                }
                catch { }
            }

            // 2. 逐段检查是否为 UTF-8 误按 GBK 解码的乱码（如 "甯屾嵎涓撲慨"）
            try
            {
                var gbk = System.Text.Encoding.GetEncoding("GBK");
                string[] parts = text.Split('\\');
                bool modified = false;
                for (int p = 0; p < parts.Length; ++p)
                {
                    string seg = parts[p];
                    if (string.IsNullOrEmpty(seg)) continue;

                    byte[] bytes = gbk.GetBytes(seg);
                    if (IsValidUtf8Sequence(bytes))
                    {
                        string recovered = System.Text.Encoding.UTF8.GetString(bytes);
                        if (!string.IsNullOrEmpty(recovered) && !recovered.Contains('\uFFFD') && recovered != seg)
                        {
                            parts[p] = recovered;
                            modified = true;
                        }
                    }
                }

                if (modified)
                {
                    return string.Join('\\', parts);
                }
            }
            catch { }

            return text;
        }

        private static bool IsValidUtf8Sequence(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;
            int i = 0;
            bool hasMultiByte = false;
            while (i < bytes.Length)
            {
                byte b = bytes[i];
                if (b < 0x80)
                {
                    i++;
                }
                else if ((b & 0xE0) == 0xC0)
                {
                    if (i + 1 >= bytes.Length || (bytes[i + 1] & 0xC0) != 0x80) return false;
                    hasMultiByte = true;
                    i += 2;
                }
                else if ((b & 0xF0) == 0xE0)
                {
                    if (i + 2 >= bytes.Length || (bytes[i + 1] & 0xC0) != 0x80 || (bytes[i + 2] & 0xC0) != 0x80) return false;
                    hasMultiByte = true;
                    i += 3;
                }
                else if ((b & 0xF8) == 0xF0)
                {
                    if (i + 3 >= bytes.Length || (bytes[i + 1] & 0xC0) != 0x80 || (bytes[i + 2] & 0xC0) != 0x80 || (bytes[i + 3] & 0xC0) != 0x80) return false;
                    hasMultiByte = true;
                    i += 4;
                }
                else
                {
                    return false;
                }
            }
            return hasMultiByte;
        }
    }
}
