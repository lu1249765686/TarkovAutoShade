using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TarkovAutoShade
{
    internal static class ImageAnalyzer
    {
        private const int AnalysisWidth = 320;
        private const int AnalysisHeight = 180;

        public static AnalysisResult Analyze(string filePath, AppSettings settings)
        {
            using (Bitmap source = LoadStableBitmap(filePath))
            using (var sample = new Bitmap(
                AnalysisWidth, AnalysisHeight, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(sample))
                {
                    graphics.Clear(Color.Black);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(source, new Rectangle(0, 0, sample.Width, sample.Height));
                }

                var result = Measure(sample);
                result.FilePath = filePath;
                result.CapturedAt = File.GetLastWriteTime(filePath);
                Classify(result);
                if (result.IsUsable)
                    result.Recommendation = ToneCurve.Recommend(result, settings);
                return result;
            }
        }

        public static Bitmap LoadStableBitmap(string filePath)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt < 16; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(
                        filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var image = Image.FromStream(stream, true, true))
                    {
                        return new Bitmap(image);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Thread.Sleep(180);
                }
            }
            throw new IOException("截图尚未写入完成。", lastError);
        }

        public static Bitmap BuildPreview(Bitmap source, FilterRecommendation recommendation)
        {
            Bitmap output = BuildOriginalPreview(source);
            ApplyLut(output, recommendation);
            return output;
        }

        public static Bitmap BuildOriginalPreview(Bitmap source)
        {
            int maximumWidth = 900;
            int maximumHeight = 520;
            double scale = Math.Min(
                maximumWidth / (double)source.Width,
                maximumHeight / (double)source.Height);
            scale = Math.Min(1.0, scale);
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));

            var output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(output))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            }
            return output;
        }

        private static AnalysisResult Measure(Bitmap bitmap)
        {
            int[] histogram = new int[256];
            int left = bitmap.Width * 5 / 100;
            int right = bitmap.Width * 95 / 100;
            int top = bitmap.Height * 5 / 100;
            int bottom = bitmap.Height * 84 / 100;
            int regionWidth = right - left;
            int regionHeight = bottom - top;
            var luma = new byte[regionWidth * regionHeight];

            Rectangle rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(
                rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            int byteCount = Math.Abs(data.Stride) * data.Height;
            byte[] pixels = new byte[byteCount];
            Marshal.Copy(data.Scan0, pixels, 0, byteCount);
            bitmap.UnlockBits(data);

            long redTotal = 0;
            long greenTotal = 0;
            long blueTotal = 0;
            long upperTotal = 0;
            long lowerTotal = 0;
            int upperCount = 0;
            int lowerCount = 0;
            int brightCount = 0;
            int count = 0;
            int lumaIndex = 0;
            double edgeTotal = 0.0;
            int edgeCount = 0;

            for (int y = top; y < bottom; y++)
            {
                int row = y * data.Stride;
                for (int x = left; x < right; x++)
                {
                    int index = row + x * 3;
                    int blue = pixels[index];
                    int green = pixels[index + 1];
                    int red = pixels[index + 2];
                    int brightness = MathUtil.Clamp(
                        (int)Math.Round(0.0722 * blue + 0.7152 * green + 0.2126 * red),
                        0,
                        255);

                    histogram[brightness]++;
                    luma[lumaIndex] = (byte)brightness;
                    redTotal += red;
                    greenTotal += green;
                    blueTotal += blue;
                    if (y < top + regionHeight * 45 / 100)
                    {
                        upperTotal += brightness;
                        upperCount++;
                    }
                    else
                    {
                        lowerTotal += brightness;
                        lowerCount++;
                    }
                    if (brightness >= 153) brightCount++;
                    count++;

                    if (x > left)
                    {
                        edgeTotal += Math.Abs(brightness - luma[lumaIndex - 1]) / 255.0;
                        edgeCount++;
                    }
                    if (y > top)
                    {
                        edgeTotal += Math.Abs(brightness - luma[lumaIndex - regionWidth]) / 255.0;
                        edgeCount++;
                    }
                    lumaIndex++;
                }
            }

            var result = new AnalysisResult {
                Histogram = histogram,
                P01 = Percentile(histogram, count, 0.01),
                P05 = Percentile(histogram, count, 0.05),
                P10 = Percentile(histogram, count, 0.10),
                P25 = Percentile(histogram, count, 0.25),
                Median = Percentile(histogram, count, 0.50),
                P75 = Percentile(histogram, count, 0.75),
                P90 = Percentile(histogram, count, 0.90),
                P95 = Percentile(histogram, count, 0.95),
                P99 = Percentile(histogram, count, 0.99),
                EdgeEnergy = edgeCount == 0 ? 0.0 : edgeTotal / edgeCount,
                MeanRed = count == 0 ? 0.0 : redTotal / (255.0 * count),
                MeanGreen = count == 0 ? 0.0 : greenTotal / (255.0 * count),
                MeanBlue = count == 0 ? 0.0 : blueTotal / (255.0 * count),
                UpperMean = upperCount == 0 ? 0.0 :
                    upperTotal / (255.0 * upperCount),
                LowerMean = lowerCount == 0 ? 0.0 :
                    lowerTotal / (255.0 * lowerCount),
                BrightFraction = count == 0 ? 0.0 :
                    brightCount / (double)count
            };
            result.DynamicRange = result.P95 - result.P05;
            return result;
        }

        private static void Classify(AnalysisResult result)
        {
            result.IsUsable = true;
            result.SkipReason = "";

            double redExcess = result.MeanRed -
                (result.MeanGreen + result.MeanBlue) * 0.5;
            double greenExcess = result.MeanGreen -
                (result.MeanRed + result.MeanBlue) * 0.5;
            double blueExcess = result.MeanBlue -
                (result.MeanRed + result.MeanGreen) * 0.5;

            result.RedCast = redExcess;
            result.GreenCast = greenExcess;
            result.BlueCast = blueExcess;

            // Night vision can be a muted green rather than a vivid green.
            // The old test compared green with the average of red and blue,
            // which missed dark scenes where blue was already elevated by
            // the game lighting. Use the strongest channel gap as a second,
            // still-dark-gated signal so ordinary daylight foliage remains
            // outside the protection path.
            double greenDominance = result.MeanGreen -
                Math.Max(result.MeanRed, result.MeanBlue);
            double darkSceneGate = 1.0 -
                MathUtil.SmoothStep(0.22, 0.50, result.Median);
            double averageGreenSignal =
                MathUtil.SmoothStep(0.025, 0.095, greenExcess) *
                MathUtil.SmoothStep(0.06, 0.34, result.MeanGreen) *
                darkSceneGate;
            double dominantGreenSignal =
                MathUtil.SmoothStep(0.012, 0.055, greenDominance) *
                MathUtil.SmoothStep(0.04, 0.22, result.MeanGreen) *
                darkSceneGate;
            result.NightVisionScore = MathUtil.Clamp(
                Math.Max(averageGreenSignal, dominantGreenSignal),
                0.0,
                1.0);

            // Tarkov playable interiors can be nearly black and still contain
            // useful geometry. Do not classify dark or low-edge frames as a
            // loading page; stable file decoding above is the only gate.
            result.IsUsable = true;
            result.SkipReason = "";

            if (result.Median < 0.115)
                result.SceneLabel = "极暗场景";
            else if (result.Median < 0.245)
                result.SceneLabel = "暗场景";
            else if (result.Median < 0.520)
                result.SceneLabel = "均衡场景";
            else
                result.SceneLabel = "明亮场景";

            if (result.P95 > 0.90 && result.Median < 0.30)
                result.SceneLabel += " / 强逆光";
            if (result.NightVisionScore > 0.42)
                result.SceneLabel += " / 夜视保护";
        }

        private static double Percentile(int[] histogram, int count, double percentile)
        {
            if (count <= 0) return 0.0;
            int target = Math.Max(1, (int)Math.Ceiling(count * percentile));
            int cumulative = 0;
            for (int i = 0; i < histogram.Length; i++)
            {
                cumulative += histogram[i];
                if (cumulative >= target) return i / 255.0;
            }
            return 1.0;
        }

        private static void ApplyLut(Bitmap bitmap, FilterRecommendation recommendation)
        {
            Rectangle rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(
                rectangle, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            int byteCount = Math.Abs(data.Stride) * data.Height;
            byte[] pixels = new byte[byteCount];
            Marshal.Copy(data.Scan0, pixels, 0, byteCount);

            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * data.Stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int index = row + x * 3;
                    pixels[index] = (byte)(recommendation.Blue[pixels[index]] >> 8);
                    pixels[index + 1] = (byte)(recommendation.Green[pixels[index + 1]] >> 8);
                    pixels[index + 2] = (byte)(recommendation.Red[pixels[index + 2]] >> 8);
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, byteCount);
            bitmap.UnlockBits(data);
        }
    }
}
