using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace UnzipProxy
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    MessageBox.Show($"程序发生未处理异常: {ex.Message}", "UnzipProxy 错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            string? targetArchive = null;
            if (e.Args.Length > 0 && !string.IsNullOrWhiteSpace(e.Args[0]))
            {
                targetArchive = e.Args[0];
            }
            else
            {
                // 如果没有传入参数，打开文件选择对话框
                var dlg = new OpenFileDialog
                {
                    Title = "选择要解压的压缩包",
                    Filter = "常用压缩文件 (*.zip;*.7z;*.rar;*.tar;*.gz;*.bz2;*.xz;*.001)|*.zip;*.7z;*.rar;*.tar;*.gz;*.bz2;*.xz;*.001|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true)
                {
                    targetArchive = dlg.FileName;
                }
                else
                {
                    Shutdown();
                    return;
                }
            }

            var mainWindow = new MainWindow(targetArchive);
            mainWindow.Show();
        }
    }
}
