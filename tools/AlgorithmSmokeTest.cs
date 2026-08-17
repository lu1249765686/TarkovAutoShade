using System;
using System.Collections.Generic;
using System.IO;
using TarkovAutoShade;

internal static class AlgorithmSmokeTest
{
    private sealed class Bucket
    {
        public string Name;
        public int Count;
        public double InputP10;
        public double InputP50;
        public double InputP95;
        public double OutputP10;
        public double OutputP50;
        public double OutputP95;
        public double Gamma;
        public double Brightness;
    }

    private static int Main(string[] args)
    {
        string folder = args.Length > 0 ? args[0] :
            AppSettings.GetDefaultScreenshotFolder();
        int step = args.Length > 1 ? Math.Max(1, int.Parse(args[1])) : 12;
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine("Screenshot folder not found: " + folder);
            return 2;
        }

        string[] files = Directory.GetFiles(folder, "*.png");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        var settings = AppSettings.CreateDefault();
        var buckets = new Dictionary<string, Bucket>();
        int usable = 0;
        int skipped = 0;
        int failures = ValidateSettingsMigration();
        failures += ValidateSceneGuards();
        bool biasChecksRun = false;

        for (int i = 0; i < files.Length; i += step)
        {
            AnalysisResult result;
            try { result = ImageAnalyzer.Analyze(files[i], settings); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Read failed: " + ex.Message);
                failures++;
                continue;
            }

            if (!result.IsUsable)
            {
                skipped++;
                continue;
            }

            usable++;
            FilterRecommendation recommendation = result.Recommendation;
            if (!ValidateLut(recommendation))
            {
                Console.Error.WriteLine("Invalid LUT: " + files[i]);
                failures++;
            }

            if (!biasChecksRun)
            {
                failures += ValidateBiasControls(result);
                biasChecksRun = true;
            }

            string name = BucketName(result.Median);
            Bucket bucket;
            if (!buckets.TryGetValue(name, out bucket))
            {
                bucket = new Bucket { Name = name };
                buckets[name] = bucket;
            }
            bucket.Count++;
            bucket.InputP10 += result.P10;
            bucket.InputP50 += result.Median;
            bucket.InputP95 += result.P95;
            bucket.OutputP10 += Map(recommendation, result.P10);
            bucket.OutputP50 += Map(recommendation, result.Median);
            bucket.OutputP95 += Map(recommendation, result.P95);
            bucket.Gamma += recommendation.EquivalentGamma;
            bucket.Brightness += recommendation.BrightnessBoost;
        }

        if (skipped != 0)
        {
            Console.Error.WriteLine(
                "Playable PNGs must not be rejected as loading or low-information frames.");
            failures++;
        }

        Console.WriteLine("Sampled: " + usable + " usable, " + skipped +
            " skipped, step " + step);
        foreach (string name in new[] {
            "extreme-dark", "dark", "balanced", "daylight", "bright"
        })
        {
            Bucket bucket;
            if (!buckets.TryGetValue(name, out bucket)) continue;
            Print(bucket);
        }

        Bucket extreme;
        if (buckets.TryGetValue("extreme-dark", out extreme) &&
            Average(extreme.OutputP50, extreme.Count) < 0.24)
        {
            Console.Error.WriteLine("Extreme-dark median lift is too weak.");
            failures++;
        }

        Bucket bright;
        if (buckets.TryGetValue("bright", out bright) &&
            Average(bright.OutputP95, bright.Count) > 0.93)
        {
            Console.Error.WriteLine("Bright-scene highlights are insufficiently protected.");
            failures++;
        }

        Console.WriteLine(failures == 0 ? "PASS" : "FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }

    private static bool ValidateLut(FilterRecommendation recommendation)
    {
        if (recommendation.Green[0] != 0 ||
            recommendation.Green[255] != ushort.MaxValue)
            return false;
        for (int i = 1; i < 256; i++)
        {
            if (recommendation.Red[i] < recommendation.Red[i - 1] ||
                recommendation.Green[i] < recommendation.Green[i - 1] ||
                recommendation.Blue[i] < recommendation.Blue[i - 1])
                return false;
        }
        return true;
    }

    private static int ValidateSettingsMigration()
    {
        var legacy = new AppSettings {
            AlgorithmVersion = 2,
            ScreenshotFolder = AppSettings.GetDefaultScreenshotFolder(),
            ShadowTarget = 0,
            HighlightProtection = 0,
            MaxStrength = 0,
            Warmth = -20
        };
        legacy.Normalize();
        if (legacy.AlgorithmVersion == 8 &&
            legacy.ShadowTarget == 70 &&
            legacy.HighlightProtection == 76 &&
            legacy.MaxStrength == 82 &&
            legacy.Warmth == 0 &&
            legacy.ColorCorrection == 72 &&
            legacy.IndoorComfort == 72 &&
            legacy.SceneGuard == 88 &&
            legacy.BlackPoint == 56)
            return 0;

        Console.Error.WriteLine("Legacy zero-strength settings were not recovered.");
        return 1;
    }

    private static int ValidateBiasControls(AnalysisResult result)
    {
        var darker = AppSettings.CreateDefault();
        darker.ExposureBias = -20;
        var brighter = AppSettings.CreateDefault();
        brighter.ExposureBias = 20;
        var flatter = AppSettings.CreateDefault();
        flatter.ContrastBias = -20;
        var punchier = AppSettings.CreateDefault();
        punchier.ContrastBias = 20;

        FilterRecommendation dark = ToneCurve.Recommend(result, darker);
        FilterRecommendation bright = ToneCurve.Recommend(result, brighter);
        FilterRecommendation flat = ToneCurve.Recommend(result, flatter);
        FilterRecommendation punch = ToneCurve.Recommend(result, punchier);

        int failures = 0;
        if (bright.BrightnessBoost <= dark.BrightnessBoost ||
            bright.EquivalentGamma <= dark.EquivalentGamma)
        {
            Console.Error.WriteLine("Exposure bias does not change the curve.");
            failures++;
        }
        if (punch.ContrastBoost <= flat.ContrastBoost)
        {
            Console.Error.WriteLine("Contrast bias does not change the curve.");
            failures++;
        }
        return failures;
    }

    private static int ValidateSceneGuards()
    {
        var bright = new AnalysisResult {
            P10 = 0.24,
            Median = 0.68,
            P75 = 0.78,
            P95 = 0.98,
            P99 = 0.99,
            DynamicRange = 0.74,
            EdgeEnergy = 0.04,
            MeanRed = 0.36,
            MeanGreen = 0.37,
            MeanBlue = 0.36
        };
        var nightVision = new AnalysisResult {
            P10 = 0.04,
            Median = 0.18,
            P75 = 0.32,
            P95 = 0.76,
            P99 = 0.86,
            DynamicRange = 0.72,
            EdgeEnergy = 0.04,
            MeanRed = 0.10,
            MeanGreen = 0.30,
            MeanBlue = 0.09,
            NightVisionScore = 0.90
        };
        var mutedGreenNight = new AnalysisResult {
            P10 = 0.02,
            Median = 0.12,
            P75 = 0.20,
            P95 = 0.52,
            P99 = 0.68,
            DynamicRange = 0.50,
            EdgeEnergy = 0.02,
            MeanRed = 0.08,
            MeanGreen = 0.18,
            MeanBlue = 0.12,
            NightVisionScore = 0.55
        };
        var redCast = new AnalysisResult {
            P10 = 0.05,
            Median = 0.22,
            P75 = 0.35,
            P95 = 0.70,
            P99 = 0.82,
            DynamicRange = 0.65,
            EdgeEnergy = 0.04,
            MeanRed = 0.38,
            MeanGreen = 0.25,
            MeanBlue = 0.20
        };

        FilterRecommendation brightRecommendation = ToneCurve.Recommend(
            bright, AppSettings.CreateDefault());
        FilterRecommendation nightRecommendation = ToneCurve.Recommend(
            nightVision, AppSettings.CreateDefault());
        FilterRecommendation redRecommendation = ToneCurve.Recommend(
            redCast, AppSettings.CreateDefault());

        int failures = 0;
        if (brightRecommendation.BrightnessBoost > 1.0 ||
            brightRecommendation.EquivalentGamma > 1.12)
        {
            Console.Error.WriteLine(
                "Bright-scene guard still permits too much lift.");
            failures++;
        }
        if (nightRecommendation.BrightnessBoost > 1.0 ||
            nightRecommendation.EquivalentGamma > 1.20)
        {
            Console.Error.WriteLine(
                "Night-vision guard still permits too much lift.");
            failures++;
        }
        FilterRecommendation mutedGreenRecommendation = ToneCurve.Recommend(
            mutedGreenNight, AppSettings.CreateDefault());
        mutedGreenNight.NightVisionScore = 0.0;
        FilterRecommendation unguardedGreenRecommendation = ToneCurve.Recommend(
            mutedGreenNight, AppSettings.CreateDefault());
        if (mutedGreenRecommendation.HighlightCompression <= 0.0 ||
            mutedGreenRecommendation.BrightnessBoost >=
                unguardedGreenRecommendation.BrightnessBoost)
        {
            Console.Error.WriteLine(
                "Muted green night scene is not receiving night protection.");
            failures++;
        }
        if (redRecommendation.RedBalance >= 0.0 ||
            redRecommendation.GreenBalance <= 0.0 ||
            redRecommendation.BlueBalance <= 0.0)
        {
            Console.Error.WriteLine(
                "Color correction does not counter a red cast.");
            failures++;
        }

        var grayScene = new AnalysisResult {
            P10 = 0.06,
            Median = 0.17,
            P75 = 0.28,
            P95 = 0.52,
            P99 = 0.63,
            DynamicRange = 0.12,
            EdgeEnergy = 0.03,
            MeanRed = 0.19,
            MeanGreen = 0.19,
            MeanBlue = 0.19
        };
        var balancedBlackPoint = AppSettings.CreateDefault();
        balancedBlackPoint.BlackPoint = 50;
        var strongerBlackPoint = AppSettings.CreateDefault();
        strongerBlackPoint.BlackPoint = 90;
        FilterRecommendation balancedRecommendation = ToneCurve.Recommend(
            grayScene, balancedBlackPoint);
        FilterRecommendation strongerRecommendation = ToneCurve.Recommend(
            grayScene, strongerBlackPoint);
        if (strongerRecommendation.BlackPointRecovery <=
            balancedRecommendation.BlackPointRecovery)
        {
            Console.Error.WriteLine("Black-point control does not reduce gray haze.");
            failures++;
        }
        return failures;
    }

    private static double Map(FilterRecommendation recommendation, double value)
    {
        int index = MathUtil.Clamp((int)Math.Round(value * 255.0), 0, 255);
        return recommendation.Green[index] / 65535.0;
    }

    private static string BucketName(double median)
    {
        if (median < 0.08) return "extreme-dark";
        if (median < 0.20) return "dark";
        if (median < 0.42) return "balanced";
        if (median < 0.58) return "daylight";
        return "bright";
    }

    private static void Print(Bucket bucket)
    {
        Console.WriteLine(
            "{0,-12} n={1,3}  P10 {2:0.000}->{3:0.000}  " +
            "P50 {4:0.000}->{5:0.000}  P95 {6:0.000}->{7:0.000}  " +
            "gamma {8:0.00}  brightness +{9:0}",
            bucket.Name,
            bucket.Count,
            Average(bucket.InputP10, bucket.Count),
            Average(bucket.OutputP10, bucket.Count),
            Average(bucket.InputP50, bucket.Count),
            Average(bucket.OutputP50, bucket.Count),
            Average(bucket.InputP95, bucket.Count),
            Average(bucket.OutputP95, bucket.Count),
            Average(bucket.Gamma, bucket.Count),
            Average(bucket.Brightness, bucket.Count));
    }

    private static double Average(double total, int count)
    {
        return count == 0 ? 0.0 : total / count;
    }
}
