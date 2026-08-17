using System;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfImage = System.Windows.Controls.Image;
using DrawingBitmap = System.Drawing.Bitmap;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfPoint = System.Windows.Point;

namespace TarkovAutoShade
{
    /// <summary>
    /// Same-image before/after comparison. The original and filtered images
    /// share one layout and are clipped on opposite sides of the divider.
    /// </summary>
    public sealed class PreviewControl : UserControl
    {
        private readonly Grid layoutGrid;
        private readonly Grid imageGrid;
        private readonly WpfImage beforeImage;
        private readonly WpfImage afterImage;
        private readonly Grid labelOverlay;
        private readonly Border divider;
        private readonly Canvas imageCorners;
        private readonly TextBlock topLeftCorner;
        private readonly TextBlock topRightCorner;
        private readonly TextBlock bottomLeftCorner;
        private readonly TextBlock bottomRightCorner;
        private readonly GammaCurveGraph graph;
        private double dividerPosition = 0.5;
        private double renderedImageLeft;
        private double renderedImageWidth;
        private bool isDragging;

        public PreviewControl()
        {
            layoutGrid = new Grid { Background = MediaBrushes.Black };
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(126) });

            imageGrid = new Grid { Background = MediaBrushes.Black, ClipToBounds = true };
            beforeImage = CreateImage();
            afterImage = CreateImage();

            imageGrid.Children.Add(beforeImage);
            imageGrid.Children.Add(afterImage);
            labelOverlay = CreateLabelOverlay();
            imageGrid.Children.Add(labelOverlay);

            imageCorners = new Canvas { IsHitTestVisible = false };
            topLeftCorner = CreateCorner("┌");
            topRightCorner = CreateCorner("┐");
            bottomLeftCorner = CreateCorner("└");
            bottomRightCorner = CreateCorner("┘");
            imageCorners.Children.Add(topLeftCorner);
            imageCorners.Children.Add(topRightCorner);
            imageCorners.Children.Add(bottomLeftCorner);
            imageCorners.Children.Add(bottomRightCorner);
            imageGrid.Children.Add(imageCorners);

            divider = new Border
            {
                Width = 4,
                Background = new SolidColorBrush(MediaColor.FromRgb(82, 205, 158)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Cursor = Cursors.SizeWE
            };
            divider.Child = CreateDividerHandle();
            divider.MouseLeftButtonDown += DividerMouseLeftButtonDown;
            divider.MouseLeftButtonUp += DividerMouseLeftButtonUp;
            divider.MouseMove += DividerMouseMove;
            imageGrid.Children.Add(divider);
            imageGrid.MouseLeftButtonDown += ImageGridMouseLeftButtonDown;

            graph = new GammaCurveGraph
            {
                Margin = new Thickness(0, 8, 0, 0),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetRow(imageGrid, 0);
            Grid.SetRow(graph, 1);
            layoutGrid.Children.Add(imageGrid);
            layoutGrid.Children.Add(graph);
            Content = layoutGrid;
            SizeChanged += delegate { UpdateLayoutForSplit(); };
        }

        private static WpfImage CreateImage()
        {
            return new WpfImage
            {
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                SnapsToDevicePixels = true
            };
        }

        private static TextBlock CreateCorner(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = new MediaFontFamily("Consolas"),
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(82, 205, 158)),
                Width = 28,
                Height = 28
            };
        }

        private static Grid CreateLabelOverlay()
        {
            var overlay = new Grid
            {
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Top,
                Height = 34,
                Margin = new Thickness(12, 12, 12, 0)
            };
            overlay.ColumnDefinitions.Add(new ColumnDefinition());
            overlay.ColumnDefinitions.Add(new ColumnDefinition());
            overlay.Children.Add(CreateImageLabel("原图", HorizontalAlignment.Left));
            var filtered = CreateImageLabel("滤镜", HorizontalAlignment.Right);
            Grid.SetColumn(filtered, 1);
            overlay.Children.Add(filtered);
            return overlay;
        }

        private static Border CreateImageLabel(string text, HorizontalAlignment alignment)
        {
            return new Border
            {
                Background = new SolidColorBrush(MediaColor.FromArgb(210, 10, 10, 10)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(82, 205, 158)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                Width = 52,
                Height = 26,
                HorizontalAlignment = alignment,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = new SolidColorBrush(MediaColor.FromRgb(82, 205, 158)),
                    FontFamily = new MediaFontFamily("Noto Sans SC, MiSans, Microsoft YaHei UI"),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        private static Border CreateDividerHandle()
        {
            var handle = new Border
            {
                Width = 34,
                Height = 64,
                Background = new SolidColorBrush(MediaColor.FromRgb(18, 18, 18)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(82, 205, 158)),
                BorderThickness = new Thickness(2),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.SizeWE
            };
            handle.Child = new TextBlock
            {
                Text = "||",
                Foreground = new SolidColorBrush(MediaColor.FromRgb(82, 205, 158)),
                FontFamily = new MediaFontFamily("Consolas"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return handle;
        }

        private void ImageGridMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!UpdateDividerPosition(e.GetPosition(imageGrid))) return;
            UpdateLayoutForSplit();
            e.Handled = true;
        }

        private void DividerMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            isDragging = true;
            divider.CaptureMouse();
            e.Handled = true;
        }

        private void DividerMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!isDragging) return;
            isDragging = false;
            divider.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void DividerMouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging || !UpdateDividerPosition(e.GetPosition(imageGrid))) return;
            UpdateLayoutForSplit();
            e.Handled = true;
        }

        private bool UpdateDividerPosition(WpfPoint position)
        {
            if (renderedImageWidth <= 0) return false;
            dividerPosition = Math.Max(0.05, Math.Min(0.95,
                (position.X - renderedImageLeft) / renderedImageWidth));
            return true;
        }

        private void UpdateLayoutForSplit()
        {
            if (imageGrid.ActualWidth <= 0 || imageGrid.ActualHeight <= 0) return;
            double width = imageGrid.ActualWidth;
            double height = imageGrid.ActualHeight;
            var source = afterImage.Source as BitmapSource ?? beforeImage.Source as BitmapSource;
            double sourceWidth = source == null ? width : Math.Max(1, source.PixelWidth);
            double sourceHeight = source == null ? height : Math.Max(1, source.PixelHeight);
            double scale = Math.Min(width / sourceWidth, height / sourceHeight);
            double imageWidth = Math.Max(1, sourceWidth * scale);
            double imageHeight = Math.Max(1, sourceHeight * scale);
            double imageLeft = (width - imageWidth) / 2.0;
            double imageTop = (height - imageHeight) / 2.0;
            double splitX = imageWidth * dividerPosition;
            renderedImageLeft = imageLeft;
            renderedImageWidth = imageWidth;

            beforeImage.Width = imageWidth;
            beforeImage.Height = imageHeight;
            beforeImage.Margin = new Thickness(imageLeft, imageTop, 0, 0);
            beforeImage.Clip = new RectangleGeometry(
                new Rect(0, 0, splitX, imageHeight));

            afterImage.Width = imageWidth;
            afterImage.Height = imageHeight;
            afterImage.Margin = new Thickness(imageLeft, imageTop, 0, 0);
            afterImage.Clip = new RectangleGeometry(
                new Rect(splitX, 0, imageWidth - splitX, imageHeight));

            divider.Height = imageHeight;
            divider.VerticalAlignment = VerticalAlignment.Top;
            divider.Margin = new Thickness(imageLeft + splitX - divider.Width / 2,
                imageTop, 0, 0);

            labelOverlay.Width = Math.Max(1, imageWidth - 24);
            labelOverlay.Height = 34;
            labelOverlay.HorizontalAlignment = HorizontalAlignment.Left;
            labelOverlay.VerticalAlignment = VerticalAlignment.Top;
            labelOverlay.Margin = new Thickness(imageLeft + 12, imageTop + 12, 0, 0);

            Canvas.SetLeft(topLeftCorner, imageLeft - 1);
            Canvas.SetTop(topLeftCorner, imageTop - 1);
            Canvas.SetLeft(topRightCorner, imageLeft + imageWidth - 27);
            Canvas.SetTop(topRightCorner, imageTop - 1);
            Canvas.SetLeft(bottomLeftCorner, imageLeft - 1);
            Canvas.SetTop(bottomLeftCorner, imageTop + imageHeight - 27);
            Canvas.SetLeft(bottomRightCorner, imageLeft + imageWidth - 27);
            Canvas.SetTop(bottomRightCorner, imageTop + imageHeight - 27);
        }

        internal void SetContent(DrawingBitmap before, DrawingBitmap after,
            AnalysisResult analysis)
        {
            try
            {
                beforeImage.Source = before == null ? null :
                    ConvertBitmapToImageSource(before);
                afterImage.Source = after == null ? null :
                    ConvertBitmapToImageSource(after);
                graph.SetAnalysis(analysis);
            }
            finally
            {
                if (before != null) before.Dispose();
                if (after != null) after.Dispose();
            }
            UpdateLayoutForSplit();
        }

        internal void SetAnalysis(AnalysisResult analysis)
        {
            graph.SetAnalysis(analysis);
        }

        public void SetBeforeImage(DrawingBitmap bitmap)
        {
            if (bitmap != null) beforeImage.Source = ConvertBitmapToImageSource(bitmap);
        }

        public void SetAfterImage(DrawingBitmap bitmap)
        {
            if (bitmap != null) afterImage.Source = ConvertBitmapToImageSource(bitmap);
        }

        public void ClearImages()
        {
            beforeImage.Source = null;
            afterImage.Source = null;
            graph.SetAnalysis(null);
        }

        private static BitmapImage LoadBitmapImage(string filePath)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private static BitmapImage ConvertBitmapToImageSource(DrawingBitmap bitmap)
        {
            using (var memory = new MemoryStream())
            {
                bitmap.Save(memory, ImageFormat.Png);
                memory.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = memory;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        private sealed class GammaCurveGraph : FrameworkElement
        {
            private AnalysisResult analysis;

            public void SetAnalysis(AnalysisResult value)
            {
                analysis = value;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                base.OnRender(drawingContext);
                Rect bounds = new Rect(0, 0, ActualWidth, ActualHeight);
                drawingContext.DrawRectangle(
                    new SolidColorBrush(MediaColor.FromRgb(14, 19, 24)),
                    new Pen(new SolidColorBrush(MediaColor.FromRgb(42, 52, 58)), 1),
                    bounds);

                DrawText(drawingContext, "亮度直方图 / Gamma 曲线", 10, 10,
                    new SolidColorBrush(MediaColor.FromRgb(234, 234, 234)), 11);
                if (analysis == null || analysis.Histogram == null) return;

                Rect plot = new Rect(12, 34,
                    Math.Max(1, ActualWidth - 24), Math.Max(1, ActualHeight - 46));
                var gridPen = new Pen(new SolidColorBrush(
                    MediaColor.FromArgb(70, 74, 101, 94)), 1);
                for (int i = 1; i < 4; i++)
                {
                    double x = plot.Left + plot.Width * i / 4.0;
                    double y = plot.Top + plot.Height * i / 4.0;
                    drawingContext.DrawLine(gridPen, new WpfPoint(x, plot.Top),
                        new WpfPoint(x, plot.Bottom));
                    drawingContext.DrawLine(gridPen, new WpfPoint(plot.Left, y),
                        new WpfPoint(plot.Right, y));
                }

                int maximum = 1;
                for (int i = 0; i < analysis.Histogram.Length; i++)
                    maximum = Math.Max(maximum, analysis.Histogram[i]);
                var histogramBrush = new SolidColorBrush(
                    MediaColor.FromArgb(90, 82, 205, 158));
                for (int i = 0; i < 256; i++)
                {
                    double normalized = Math.Log(1.0 + analysis.Histogram[i]) /
                        Math.Log(1.0 + maximum);
                    double barHeight = normalized * plot.Height;
                    double x = plot.Left + i * plot.Width / 256.0;
                    double nextX = plot.Left + (i + 1) * plot.Width / 256.0;
                    drawingContext.DrawRectangle(histogramBrush, null,
                        new Rect(x, plot.Bottom - barHeight,
                            Math.Max(1, nextX - x), barHeight));
                }

                FilterRecommendation recommendation = analysis.Recommendation;
                if (recommendation == null) return;
                var identityPen = new Pen(new SolidColorBrush(
                    MediaColor.FromArgb(100, 138, 138, 138)), 1);
                drawingContext.DrawLine(identityPen, new WpfPoint(plot.Left, plot.Bottom),
                    new WpfPoint(plot.Right, plot.Top));

                var curvePen = new Pen(new SolidColorBrush(
                    MediaColor.FromRgb(82, 205, 158)), 2);
                WpfPoint previous = new WpfPoint(plot.Left, plot.Bottom);
                for (int i = 1; i < 256; i++)
                {
                    double output = recommendation.Green[i] / 65535.0;
                    WpfPoint current = new WpfPoint(
                        plot.Left + i * plot.Width / 255.0,
                        plot.Bottom - output * plot.Height);
                    drawingContext.DrawLine(curvePen, previous, current);
                    previous = current;
                }
            }

            private static void DrawText(DrawingContext context, string value,
                double x, double y, Brush brush, double size)
            {
                var text = new FormattedText(
                    value,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"),
                    size,
                    brush);
                context.DrawText(text, new WpfPoint(x, y));
            }
        }
    }
}
