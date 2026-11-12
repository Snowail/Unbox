using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace UnzipProxy.Controls
{
    /// <summary>
    /// 高仿 Windows 11 文件复制的水平推进堆积面积图 (Stacking Area Chart)
    /// X 轴代表总体解压进度 (0% 到 100%)
    /// 10x6 网格、8%~9% 超低可见度背景网格线、70% 可见度细边框
    /// 顶部速度曲面保持真实水平波形，杜绝起点斜线三角畸变
    /// 速度数值 500ms 平滑刷新，杜绝高频跳动闪烁
    /// </summary>
    public class SpeedGraphControl : FrameworkElement
    {
        private const int BucketCount = 120;
        private readonly double[] _speedBuckets = new double[BucketCount];
        private int _lastBucket = 0;
        private double _maxScale = 10.0;
        private double _peakRecordedSpeed = 0.0;

        public static readonly DependencyProperty CurrentSpeedProperty =
            DependencyProperty.Register(
                nameof(CurrentSpeed),
                typeof(double),
                typeof(SpeedGraphControl),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double CurrentSpeed
        {
            get => (double)GetValue(CurrentSpeedProperty);
            set => SetValue(CurrentSpeedProperty, value);
        }

        public static readonly DependencyProperty DisplaySpeedProperty =
            DependencyProperty.Register(
                nameof(DisplaySpeed),
                typeof(double),
                typeof(SpeedGraphControl),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double DisplaySpeed
        {
            get => (double)GetValue(DisplaySpeedProperty);
            set => SetValue(DisplaySpeedProperty, value);
        }

        public static readonly DependencyProperty ProgressProperty =
            DependencyProperty.Register(
                nameof(Progress),
                typeof(double),
                typeof(SpeedGraphControl),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Progress
        {
            get => (double)GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        public static readonly DependencyProperty IsPausedProperty =
            DependencyProperty.Register(
                nameof(IsPaused),
                typeof(bool),
                typeof(SpeedGraphControl),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool IsPaused
        {
            get => (bool)GetValue(IsPausedProperty);
            set => SetValue(IsPausedProperty, value);
        }

        public static readonly DependencyProperty IsDarkModeProperty =
            DependencyProperty.Register(
                nameof(IsDarkMode),
                typeof(bool),
                typeof(SpeedGraphControl),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool IsDarkMode
        {
            get => (bool)GetValue(IsDarkModeProperty);
            set => SetValue(IsDarkModeProperty, value);
        }

        public SpeedGraphControl()
        {
            ClipToBounds = true;
        }

        public void PushSample(double progress, double speedMBps, double displaySpeedMBps)
        {
            double p = Math.Clamp(progress, 0.0, 1.0);
            int bucket = (int)(p * (BucketCount - 1));

            if (speedMBps > 0.05)
            {
                if (_peakRecordedSpeed <= 0.0 || _peakRecordedSpeed < speedMBps * 0.4)
                {
                    // 首次获取有效速度，或初期低速伪影被真实高速覆盖，将历史桶全部抬升至平坦基线，杜绝左下角斜坡三角形
                    for (int i = 0; i <= bucket; ++i)
                    {
                        _speedBuckets[i] = speedMBps;
                    }
                }
                else
                {
                    for (int i = _lastBucket; i <= bucket; ++i)
                    {
                        _speedBuckets[i] = speedMBps;
                    }
                }
                _lastBucket = bucket;
                _peakRecordedSpeed = Math.Max(_peakRecordedSpeed, speedMBps);
            }

            double peak = Math.Max(Math.Max(speedMBps, displaySpeedMBps), _peakRecordedSpeed);
            _maxScale = NiceScale(Math.Max(10.0, peak * 1.3));

            CurrentSpeed = speedMBps;
            DisplaySpeed = displaySpeedMBps > 0 ? displaySpeedMBps : speedMBps;
            Progress = p;
            InvalidateVisual();
        }

        private static double NiceScale(double val)
        {
            if (val <= 10) return 10;
            if (val <= 25) return 25;
            if (val <= 50) return 50;
            if (val <= 100) return 100;
            if (val <= 250) return 250;
            if (val <= 500) return 500;
            if (val <= 1000) return 1000;
            return Math.Ceiling(val / 200.0) * 200.0;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 10 || h <= 10) return;

            bool isDark = IsDarkMode;
            bool isPaused = IsPaused;

            // 1. 颜色与透明度定义
            Color solidColor;
            Color tintColor;

            if (isDark)
            {
                if (isPaused)
                {
                    solidColor = Color.FromRgb(252, 225, 0);     // #FCE100 金黄
                    tintColor = Color.FromArgb(40, 252, 225, 0);  // 15% 遮罩
                }
                else
                {
                    solidColor = Color.FromRgb(76, 194, 255);    // #4CC2FF 亮青蓝
                    tintColor = Color.FromArgb(38, 76, 194, 255); // 15% 遮罩
                }
            }
            else
            {
                if (isPaused)
                {
                    solidColor = Color.FromRgb(193, 156, 0);     // #C19C00
                    tintColor = Color.FromArgb(38, 193, 156, 0);
                }
                else
                {
                    solidColor = Color.FromRgb(16, 124, 65);     // #107C41 翠绿
                    tintColor = Color.FromArgb(35, 16, 124, 65);
                }
            }

            Brush bgBrush = new SolidColorBrush(isDark ? Color.FromRgb(32, 32, 32) : Color.FromRgb(255, 255, 255));

            // 2. 70% 可见度细边框
            Color borderColor = isDark 
                ? Color.FromArgb(178, 85, 85, 85)     // 70% 可见度
                : Color.FromArgb(178, 205, 205, 205);
            Pen borderPen = new Pen(new SolidColorBrush(borderColor), 1.0);

            // 绘制背景矩形与外围边框
            dc.DrawRectangle(bgBrush, borderPen, new Rect(0, 0, w, h));

            double prog = Math.Clamp(Progress, 0.0, 1.0);
            double progX = prog * w;

            // 3. 绘制实色堆积面积图推进区域 (0 到 progX)
            if (progX > 0.5)
            {
                // 3.1 顶部半透明遮罩层
                dc.DrawRectangle(new SolidColorBrush(tintColor), null, new Rect(0, 0, progX, h));

                // 3.2 实色面积图波形 (从左侧水平延展至当前推进点)
                int curBucket = (int)(prog * (BucketCount - 1));
                double activeSpd = CurrentSpeed > 0 ? CurrentSpeed : _peakRecordedSpeed;

                StreamGeometry streamGeo = new StreamGeometry();
                using (var ctx = streamGeo.Open())
                {
                    ctx.BeginFigure(new Point(0, h), true, true);

                    for (int i = 0; i <= curBucket; ++i)
                    {
                        double x = (double)i / (BucketCount - 1) * w;
                        if (x > progX) x = progX;

                        double spd = _speedBuckets[i] > 0 ? _speedBuckets[i] : activeSpd;
                        // 细微拟真吞吐微澜 (±2.5%)，形成真实硬盘读写波形
                        double ripple = Math.Sin(i * 0.8) * 0.025 * spd;
                        double val = Math.Clamp(spd + ripple, 0.0, _maxScale);
                        double y = h - (val / _maxScale * (h - 10)) - 4;

                        ctx.LineTo(new Point(x, y), true, false);
                    }

                    // 推进端点
                    double lastVal = Math.Clamp(activeSpd, 0.0, _maxScale);
                    double lastY = h - (lastVal / _maxScale * (h - 10)) - 4;
                    ctx.LineTo(new Point(progX, lastY), true, false);
                    ctx.LineTo(new Point(progX, h), true, false);
                }
                streamGeo.Freeze();

                dc.DrawGeometry(new SolidColorBrush(solidColor), null, streamGeo);

                // 推进位置的垂直分界线
                Pen edgePen = new Pen(new SolidColorBrush(solidColor), 1.0);
                dc.DrawLine(edgePen, new Point(progX, 0), new Point(progX, h));
            }

            // 4. 背景网格线：10x6 (宽10列 x 高6行) 的网格，透明度调低至 ~8% (可见度低且通透)
            Color gridColor = isDark 
                ? Color.FromArgb(22, 255, 255, 255)  // 8.6% 超淡白色网格
                : Color.FromArgb(22, 0, 0, 0);        // 8.6% 超淡黑色网格
            Pen gridPen = new Pen(new SolidColorBrush(gridColor), 1.0);

            // 水平网格线：5 条内部线（将高度等分为 6 行）
            for (int i = 1; i < 6; ++i)
            {
                double y = h * i / 6.0;
                dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }

            // 垂直网格线：9 条内部线（将宽度等分为 10 列）
            for (int i = 1; i < 10; ++i)
            {
                double x = w * i / 10.0;
                dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
            }

            // 5. 瞬时速度水平参考基准线 (60% 可见度) 与右侧速度文本 (500ms 平滑刷新)
            double shownSpeed = DisplaySpeed > 0 ? DisplaySpeed : (CurrentSpeed > 0 ? CurrentSpeed : _peakRecordedSpeed);
            double clampedShown = Math.Clamp(shownSpeed, 0.0, _maxScale);
            double speedY = h - (clampedShown / _maxScale * (h - 10)) - 4;

            Color refLineColor = isDark 
                ? Color.FromArgb(150, 255, 255, 255) 
                : Color.FromArgb(150, 0, 0, 0);
            Pen refLinePen = new Pen(new SolidColorBrush(refLineColor), 1.0);
            dc.DrawLine(refLinePen, new Point(0, speedY), new Point(w, speedY));

            string speedLabel = $"{clampedShown:0} MB/秒";
            if (clampedShown < 10 && clampedShown > 0) speedLabel = $"{clampedShown:0.0} MB/秒";
            string speedText = isPaused ? "已暂停" : $"速度: {speedLabel}";

            var typeface = new Typeface(
                new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
                FontStyles.Normal,
                FontWeights.Normal,
                FontStretches.Normal
            );

            Color textColor = isDark ? Colors.White : Color.FromRgb(20, 20, 20);
            var ft = new FormattedText(
                speedText,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                11.5,
                new SolidColorBrush(textColor),
                VisualTreeHelper.GetDpi(this).PixelsPerDip
            );

            double textX = w - ft.Width - 10;
            double textY = speedY - ft.Height - 2;
            if (textY < 2)
            {
                textY = speedY + 3;
            }

            dc.DrawText(ft, new Point(textX, textY));
        }
    }
}
