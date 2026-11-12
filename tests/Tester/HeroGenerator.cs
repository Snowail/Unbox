using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Tester
{
    public static class HeroGenerator
    {
        public static void Generate(string workspaceDir)
        {
            int canvasW = 1920;
            int canvasH = 1080;

            string bgPath = Path.Combine(workspaceDir, "screenshot", "background.jpg");
            string img1Path = Path.Combine(workspaceDir, "screenshot", "1.png");
            string img2Path = Path.Combine(workspaceDir, "screenshot", "2.png");
            string img3Path = Path.Combine(workspaceDir, "screenshot", "3.png");

            if (!File.Exists(bgPath) || !File.Exists(img1Path) || !File.Exists(img2Path) || !File.Exists(img3Path))
            {
                Console.WriteLine("[ERROR] Missing required screenshot images!");
                return;
            }

            var bgBmp = new BitmapImage();
            bgBmp.BeginInit();
            bgBmp.UriSource = new Uri(bgPath, UriKind.Absolute);
            bgBmp.CacheOption = BitmapCacheOption.OnLoad;
            bgBmp.EndInit();

            BitmapSource LoadBmp(string path)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                return bmp;
            }

            var img1 = LoadBmp(img1Path);
            var img2 = LoadBmp(img2Path);
            var img3 = LoadBmp(img3Path);

            var canvas = new Canvas
            {
                Width = canvasW,
                Height = canvasH,
                Background = new ImageBrush(bgBmp)
                {
                    Stretch = Stretch.UniformToFill,
                    Viewbox = new Rect(0, 0.05, 1, 0.9),
                    ViewboxUnits = BrushMappingMode.RelativeToBoundingBox
                }
            };

            Border CreateWindowCard(BitmapSource source, double width, double height, double shadowBlur, double shadowDepth, double shadowOpacity)
            {
                var imgControl = new Image
                {
                    Source = source,
                    Width = width,
                    Height = height,
                    Stretch = Stretch.Fill,
                    Clip = new RectangleGeometry(new Rect(0, 0, width, height), 10, 10)
                };

                var border = new Border
                {
                    Width = width,
                    Height = height,
                    CornerRadius = new CornerRadius(10),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    Child = imgControl,
                    Effect = new DropShadowEffect
                    {
                        Color = Colors.Black,
                        BlurRadius = shadowBlur,
                        ShadowDepth = shadowDepth,
                        Direction = 270,
                        Opacity = shadowOpacity
                    }
                };

                return border;
            }

            // 1. 后层：img2 (暂停视图) - 820 x 523
            double w2 = 820;
            double h2 = w2 * (612.0 / 960.0);
            var card2 = CreateWindowCard(img2, w2, h2, shadowBlur: 45, shadowDepth: 16, shadowOpacity: 0.50);
            Canvas.SetLeft(card2, 980);
            Canvas.SetTop(card2, 100);
            canvas.Children.Add(card2);

            // 2. 主层：img1 (详细解压视图) - 880 x 561
            double w1 = 880;
            double h1 = w1 * (612.0 / 960.0);
            var card1 = CreateWindowCard(img1, w1, h1, shadowBlur: 60, shadowDepth: 22, shadowOpacity: 0.65);
            Canvas.SetLeft(card1, 520);
            Canvas.SetTop(card1, 260);
            canvas.Children.Add(card1);

            // 3. 前层：img3 (折叠简略视图) - 820 x 266
            double w3 = 820;
            double h3 = w3 * (312.0 / 960.0);
            var card3 = CreateWindowCard(img3, w3, h3, shadowBlur: 55, shadowDepth: 24, shadowOpacity: 0.72);
            Canvas.SetLeft(card3, 140);
            Canvas.SetTop(card3, 580);
            canvas.Children.Add(card3);

            canvas.Measure(new Size(canvasW, canvasH));
            canvas.Arrange(new Rect(0, 0, canvasW, canvasH));
            canvas.UpdateLayout();

            var rtb = new RenderTargetBitmap(canvasW, canvasH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(canvas);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            string assetsDir = Path.Combine(workspaceDir, "assets");
            if (!Directory.Exists(assetsDir)) Directory.CreateDirectory(assetsDir);
            string outPath1 = Path.Combine(assetsDir, "hero.png");
            string outPath2 = Path.Combine(workspaceDir, "screenshot", "hero.png");

            using (var fs = File.Create(outPath1))
            {
                encoder.Save(fs);
            }
            File.Copy(outPath1, outPath2, true);

            Console.WriteLine($"[OK] Hero showcase image successfully created: {outPath1}");
        }
    }
}
