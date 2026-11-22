using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using UnzipProxy;
using UnzipProxy.Core;

namespace Tester
{
    partial class Program
    {
        static void TestPasswordReuse()
        {
            string correct = Guid.NewGuid().ToString("N");
            string other = Guid.NewGuid().ToString("N");
            string inputs = Path.Combine(_testDir, "r10_password_inputs");
            Directory.CreateDirectory(inputs);
            File.WriteAllText(Path.Combine(inputs, "a.txt"), "first");
            File.WriteAllText(Path.Combine(inputs, "b.txt"), "second");
            File.WriteAllText(Path.Combine(inputs, "c.txt"), "third");

            string shared = Path.Combine(_testDir, "r10_shared.zip");
            Run7z($"a \"{shared}\" *.txt -mem=AES256 -p{correct}", inputs);
            string mixed = Path.Combine(_testDir, "r10_mixed.zip");
            Run7z($"a \"{mixed}\" a.txt -mem=AES256 -p{correct}", inputs);
            Run7z($"a \"{mixed}\" b.txt -mem=AES256 -p{other}", inputs);
            string independent = Path.Combine(_testDir, "r10_independent.zip");
            Run7z($"a \"{independent}\" c.txt -mem=AES256 -p{other}", inputs);

            WorkerRun Extract(string archive, string label, string? password)
            {
                return RunWorker(archive, NewStaging("r10_" + label),
                    Path.Combine(_testDir, $"r10_{label}.json"), password: password);
            }

            var same = Extract(shared, "same", correct);
            Assert("R10: 三个同密码 ZIP 条目只请求一次且全部解出",
                same.PasswordRequests == 1 && same.ExitCode == 0 && same.Status == "ok" && same.Extracted == 3);

            var wrong = Extract(shared, "wrong", other);
            Assert("R10: 错误密码只询问一次且禁止交付",
                wrong.PasswordRequests == 1 && wrong.ExitCode == 10 && wrong.Status == "failed" &&
                wrong.ErrorReason == "wrong_password" && !File.Exists(wrong.ManifestPath) && File.Exists(shared),
                wrong.ErrorReason);

            var different = Extract(mixed, "mixed", correct);
            Assert("R10: 混合密码 ZIP 失败退出且保留源包",
                different.PasswordRequests == 1 && different.ExitCode == 10 && different.Status == "failed" &&
                different.ErrorReason == "wrong_password" && !File.Exists(different.ManifestPath) && File.Exists(mixed),
                different.ErrorReason);

            var canceled = Extract(shared, "cancel", null);
            Assert("R10: 取消密码输入后 Worker 立即结束且保留源包",
                canceled.PasswordRequests == 1 && canceled.Lines.Contains("{\"type\":\"canceled\"}") &&
                !File.Exists(canceled.ManifestPath) && File.Exists(shared));

            var fresh = Extract(independent, "fresh", other);
            Assert("R10: 后续独立任务重新询问并使用新的密码",
                fresh.PasswordRequests == 1 && fresh.ExitCode == 0 && fresh.Status == "ok" && fresh.Extracted == 1);
        }

        static MainWindow OpenTestWindow(ExtractionPlan plan, string manifest, Process? worker)
        {
            var window = new MainWindow(plan.SourceArchivePath) { ShowActivated = false, ShowInTaskbar = false };
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            // Skip startup only. The test supplies the same plan, process and frame as the normal listener.
            var onLoaded = typeof(MainWindow).GetMethod("OnWindowLoaded", flags)!;
            window.Loaded -= onLoaded.CreateDelegate<RoutedEventHandler>(window);
            typeof(MainWindow).GetField("_plan", flags)!.SetValue(window, plan);
            typeof(MainWindow).GetField("_manifestPath", flags)!.SetValue(window, manifest);
            typeof(MainWindow).GetField("_workerProcess", flags)!.SetValue(window, worker);
            ((CheckBox)window.FindName("RecycleCheckBox")).IsChecked = true;
            window.Show();
            return window;
        }

        static void AwaitWindowCompletion(Task task)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
            timeout.Tick += (_, _) => frame.Continue = false;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            timeout.Start();
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            if (!task.IsCompleted) throw new TimeoutException("WPF 交付测试超时");
            task.GetAwaiter().GetResult();
        }

        static void TestWindowCompletion()
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                TestPasswordPromptLayout();
                TestStartupErrorStopsProgress();
                foreach (string scenario in new[] { "no-marker", "unknown-status", "wrong-marker", "traversal", "wait-exit", "wait-delivery" })
                {
                    string directory = Path.Combine(_testDir, "r10_window_" + scenario);
                    Directory.CreateDirectory(directory);
                    string archive = Path.Combine(directory, "source.zip");
                    CreateZipStored(archive, ("data.txt", "r10-delivery"));
                    var plan = ArchiveTaskPlanner.Plan(archive);
                    Directory.CreateDirectory(plan.StagingDirectory);
                    string manifest = Path.Combine(directory, "manifest.json");
                    var run = RunWorker(archive, plan.StagingDirectory, manifest);
                    var message = JsonNode.Parse(run.Finished!.Value.GetRawText())!.AsObject();

                    if (scenario == "no-marker") message.Remove("manifest_ok");
                    if (scenario == "wrong-marker") message["manifest_ok"] = "true";
                    if (scenario == "unknown-status") message["status"] = "unexpected";
                    string outside = Path.Combine(directory, "outside");
                    if (scenario == "traversal")
                    {
                        Directory.CreateDirectory(outside);
                        File.Copy(Path.Combine(plan.StagingDirectory, "data.txt"), Path.Combine(outside, "data.txt"));
                        var manifestJson = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
                        manifestJson["delivery_root"] = @"..\outside";
                        message["delivery_root"] = @"..\outside";
                        File.WriteAllText(manifest, manifestJson.ToJsonString());
                    }

                    bool waitForExit = scenario == "wait-exit";
                    using var worker = Process.Start(new ProcessStartInfo("cmd.exe",
                        waitForExit ? "/d /c \"set /p response=& exit /b 0\"" : "/d /c exit /b 0")
                    {
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        CreateNoWindow = true
                    })!;
                    if (!waitForExit) worker.WaitForExit();

                    using var frame = JsonDocument.Parse(message.ToJsonString());
                    var window = OpenTestWindow(plan, manifest, worker);
                    var finish = typeof(MainWindow).GetMethod("HandleFinishedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var task = (Task)finish.Invoke(window, new object[] { frame.RootElement })!;

                    if (waitForExit || scenario == "wait-delivery")
                    {
                        bool recycleSupported = ShellRecycleBin.IsRecycleBinSupported(archive);
                        window.Close();
                        Assert($"R10: 交付中的关闭请求延后 ({scenario})",
                            window.IsVisible && !task.IsCompleted && (!waitForExit || !worker.HasExited));
                        if (waitForExit) worker.StandardInput.Close();
                        AwaitWindowCompletion(task);
                        var statusText = (TextBlock)window.FindName("MainStatusText");
                        bool recycleResult = recycleSupported
                            ? !File.Exists(archive)
                            : File.Exists(archive) && statusText.Text.Contains("源文件回收未完成");
                        string delivered = Path.Combine(plan.FinalTargetDirectory, "data.txt");
                        Assert($"R10: 交付、复核与回收完成后才关闭 ({scenario})",
                            !window.IsVisible && recycleResult && File.Exists(delivered) &&
                            File.ReadAllText(delivered) == "r10-delivery", statusText.Text);
                    }
                    else
                    {
                        AwaitWindowCompletion(task);
                        Assert($"R10: 完成帧拒绝交付并保留源包 ({scenario})",
                            window.IsVisible && File.Exists(archive) && !Directory.Exists(plan.FinalTargetDirectory));
                        if (scenario == "traversal")
                            Assert("R10: 越界交付根不会改动暂存目录外的文件",
                                File.ReadAllText(Path.Combine(outside, "data.txt")) == "r10-delivery");
                        window.Close();
                    }
                }

                string source = Path.Combine(_testDir, "r10_force.zip");
                CreateZipStored(source, ("data.txt", "r10-force"));
                var forcePlan = ArchiveTaskPlanner.Plan(source);
                Directory.CreateDirectory(forcePlan.StagingDirectory);
                using var blockedWorker = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c set /p response=")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                })!;
                var forceWindow = OpenTestWindow(forcePlan, Path.Combine(_testDir, "r10_force.json"), blockedWorker);
                var closed = new TaskCompletionSource();
                forceWindow.Closed += (_, _) => closed.SetResult();
                forceWindow.Close();
                AwaitWindowCompletion(closed.Task);
                Assert("R10: 强制终止未完成的 Worker 只清理暂存并保留源包",
                    blockedWorker.HasExited && !forceWindow.IsVisible && !Directory.Exists(forcePlan.StagingDirectory) &&
                    !Directory.Exists(forcePlan.FinalTargetDirectory) && File.Exists(source));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }

        static void TestStartupErrorStopsProgress()
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var plan = ArchiveTaskPlanner.Plan(Path.Combine(_testDir, "r10_start_failure.zip"));
            var window = OpenTestWindow(plan, "", null);
            try
            {
                var bar = (ProgressBar)window.FindName("ExtractProgressBar");
                bar.IsIndeterminate = true;
                var timer = (DispatcherTimer)typeof(MainWindow).GetField("_speedTimer", flags)!.GetValue(window)!;
                timer.Start();
                const string error = "无法写入解压目录";
                typeof(MainWindow).GetMethod("TriggerError", flags)!.Invoke(window, new object[] { error, plan.DestinationParentDir });
                typeof(MainWindow).GetMethod("OnSpeedTimerTick", flags)!.Invoke(window, new object?[] { null, EventArgs.Empty });
                Assert("R10: 启动错误停止计时且不被 0 B 进度覆盖",
                    ((TextBlock)window.FindName("MainStatusText")).Text == error &&
                    ((TextBlock)window.FindName("TitleText")).Text == error && !timer.IsEnabled && !bar.IsIndeterminate);
            }
            finally { window.Close(); }
        }

        static void TestPasswordPromptLayout()
        {
            var plan = ArchiveTaskPlanner.Plan(Path.Combine(_testDir, "r10_prompt.zip"));
            var showPassword = typeof(MainWindow).GetMethod("ShowPasswordDialog", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (bool expanded in new[] { false, true })
            {
                var window = OpenTestWindow(plan, "", null);
                double originalHeight = expanded ? 306 : 156;
                window.Height = originalHeight;
                window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var dialog = window.OwnedWindows.OfType<ExtractionPrompt>().Single();
                    var content = (FrameworkElement)dialog.Content;
                    var submit = (Button)dialog.FindName("SubmitButton");
                    var cancel = (Button)dialog.FindName("CancelButton");
                    var password = (PasswordBox)dialog.FindName("PasswordInputBox");

                    bool Fits(FrameworkElement control)
                    {
                        var origin = control.TranslatePoint(new Point(), content);
                        return control.IsVisible && control.ActualHeight > 0 &&
                            origin.Y >= 0 && origin.Y + control.ActualHeight <= content.ActualHeight + 0.5;
                    }

                    Assert($"R10: 密码对话框独立布局，输入及操作区完整 (expanded={expanded})",
                        dialog.Owner == window && window.ActualHeight == originalHeight &&
                        Fits(password) && Fits(submit) && Fits(cancel) && submit.IsDefault && cancel.IsCancel);

                    var button = expanded ? cancel : submit;
                    var action = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
                    action.Invoke();
                });

                showPassword.Invoke(window, null);
                Assert($"R10: 密码对话框提交或取消按预期返回 (expanded={expanded})",
                    window.OwnedWindows.Count == 0 && (expanded ? !window.IsVisible : window.IsVisible && window.Height == originalHeight));
                if (window.IsVisible) window.Close();
            }
        }
    }
}
