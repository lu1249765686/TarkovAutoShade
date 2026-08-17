using System;

namespace TarkovAutoShade
{
    internal static class ToneCurve
    {
        public static FilterRecommendation Recommend(AnalysisResult result, AppSettings settings)
        {
            double visibility = settings.ShadowTarget / 100.0;
            double protection = settings.HighlightProtection / 100.0;
            double strength = settings.MaxStrength / 100.0;
            double exposureBias = settings.ExposureBias / 20.0;
            double contrastBias = settings.ContrastBias / 20.0;
            double colorCorrection = settings.ColorCorrection / 100.0;
            double saturationBias = settings.SaturationBias / 20.0;
            double indoorComfort = settings.IndoorComfort / 100.0;
            double sceneGuard = settings.SceneGuard / 100.0;
            double blackPointBias = (settings.BlackPoint - 50) / 50.0;

            double darkness = 1.0 - MathUtil.SmoothStep(0.10, 0.46, result.Median);
            double shadowDeficit = 1.0 - MathUtil.SmoothStep(0.055, 0.205, result.P10);
            double darkScore = MathUtil.Clamp(
                darkness * 0.68 + shadowDeficit * 0.32, 0.0, 1.0);
            double brightScore = MathUtil.SmoothStep(0.42, 0.66, result.Median);
            double backlightScore =
                MathUtil.SmoothStep(0.72, 0.94, result.P95) *
                (1.0 - MathUtil.SmoothStep(0.25, 0.50, result.Median));
            double daylightEvidence =
                MathUtil.SmoothStep(0.28, 0.58, result.P75) *
                MathUtil.SmoothStep(0.60, 0.90, result.P95);
            double spatialDaylightRaw =
                MathUtil.SmoothStep(0.10, 0.35, result.UpperMean) * 0.45 +
                MathUtil.SmoothStep(0.015, 0.165, result.BrightFraction) * 0.35 +
                MathUtil.SmoothStep(0.72, 0.94, result.P95) * 0.20;
            double spatialDaylight =
                MathUtil.SmoothStep(0.25, 0.55, spatialDaylightRaw);
            double outdoorEvidence = Math.Max(daylightEvidence, spatialDaylight);
            double brightSceneScore = MathUtil.Clamp(
                Math.Max(
                    brightScore,
                    MathUtil.SmoothStep(0.74, 0.94, result.P95) *
                    (0.45 + 0.55 * MathUtil.SmoothStep(0.35, 0.62, result.Median))),
                0.0,
                1.0);
            double indoorScore = MathUtil.Clamp(
                (1.0 - outdoorEvidence) *
                (0.35 + 0.65 * darkScore) *
                (1.0 - 0.35 * result.NightVisionScore),
                0.0,
                1.0);
            double protectionScore = MathUtil.Clamp(
                sceneGuard * (
                    0.78 * brightSceneScore +
                    0.92 * result.NightVisionScore +
                    0.52 * backlightScore),
                0.0,
                1.0);
            double comfortScore = indoorComfort * indoorScore;
            double flatScore = 1.0 -
                MathUtil.SmoothStep(0.14, 0.42, result.DynamicRange);
            double graySceneGate = MathUtil.SmoothStep(0.07, 0.24, result.Median);
            double automaticGrayRecovery = flatScore * graySceneGate *
                (0.018 + 0.030 * darkScore) *
                (1.0 - 0.35 * protectionScore);
            double blackPointRecovery = MathUtil.Clamp(
                automaticGrayRecovery + 0.045 * blackPointBias *
                (0.30 + 0.70 * graySceneGate),
                -0.035,
                0.090);
            double effectiveDarkScore = darkScore *
                (1.0 - 0.88 * outdoorEvidence) *
                (1.0 - 0.78 * protectionScore);
            double extremeDarkRecovery = 1.0 - MathUtil.SmoothStep(
                0.035, 0.12, result.Median);
            // A dark Tarkov interior often contains a small bright source or
            // sky opening. Recover more shadow detail only when the upper
            // tail is not already bright, so the new lift does not wash out
            // doors, windows, or lamps in the same frame.
            double darkDetailRecovery = extremeDarkRecovery *
                (1.0 - MathUtil.SmoothStep(0.48, 0.82, result.P95)) *
                (1.0 - 0.45 * protectionScore);

            // Calibrated against the user's proven presets:
            // outdoor day ~= gamma 1.30 / brightness 6 / contrast 4
            // dark interior ~= gamma 1.55 / brightness 55 / contrast 21
            double visibilityScale = 0.78 + visibility * 0.34;
            double equivalentGamma = 1.30 - 0.08 * brightScore +
                0.25 * effectiveDarkScore * visibilityScale;
            equivalentGamma += 0.04 * darkDetailRecovery * visibilityScale;
            double brightnessBoost = 6.0 * (1.0 - brightScore * 0.68) +
                49.0 * effectiveDarkScore * visibilityScale;
            // Keep the darkest playable interiors above the visibility floor
            // even when a sparse sample under-represents their shadow detail.
            brightnessBoost += 4.0 * extremeDarkRecovery * shadowDeficit;
            brightnessBoost += 5.0 * darkDetailRecovery * shadowDeficit;
            double contrastBoost = 4.0 + 17.0 * effectiveDarkScore +
                4.0 * flatScore - 3.0 * backlightScore;

            double exposureSceneScale = 0.45 + 0.55 * (1.0 - brightScore);
            double manualLiftGate = 1.0 - 0.82 * protectionScore;
            equivalentGamma += 0.07 * exposureBias * exposureSceneScale *
                manualLiftGate;
            brightnessBoost += 10.0 * exposureBias * exposureSceneScale *
                manualLiftGate;
            brightnessBoost *= 1.0 - 0.38 * comfortScore;
            brightnessBoost *= 1.0 - 0.88 * protectionScore;
            brightnessBoost -= 8.0 * protectionScore;
            contrastBoost += 7.0 * contrastBias;
            contrastBoost -= 8.0 * comfortScore;
            contrastBoost -= 3.0 * protectionScore;
            equivalentGamma -= 0.24 * protectionScore;
            equivalentGamma -= 0.055 * comfortScore;

            equivalentGamma = MathUtil.Clamp(equivalentGamma, 1.01, 1.65);
            brightnessBoost = MathUtil.Clamp(brightnessBoost, 0.0, 60.0);
            contrastBoost = MathUtil.Clamp(contrastBoost, 0.0, 25.0);

            double gamma = 1.0 / equivalentGamma;
            double shadowLift = brightnessBoost / 100.0 * 0.45;
            double contrast = contrastBoost / 100.0 * 0.45;
            double compression = protection *
                (0.055 + 0.20 * backlightScore + 0.17 * brightScore);
            compression += 0.16 * comfortScore + 0.24 * protectionScore;
            compression = MathUtil.Clamp(compression, 0.0, 0.48);
            double warmth = settings.Warmth / 20.0 * 0.020;

            double meanLuma = 0.2126 * result.MeanRed +
                0.7152 * result.MeanGreen + 0.0722 * result.MeanBlue;
            double correctionScale = 0.82 * colorCorrection;
            double redBalance = MathUtil.Clamp(
                (meanLuma - result.MeanRed) * correctionScale,
                -0.075,
                0.075);
            double greenBalance = MathUtil.Clamp(
                (meanLuma - result.MeanGreen) * correctionScale,
                -0.075,
                0.075);
            double blueBalance = MathUtil.Clamp(
                (meanLuma - result.MeanBlue) * correctionScale,
                -0.075,
                0.075);

            // Gamma ramps are per-channel, so this is a restrained saturation
            // control: it expands or contracts each channel's distance from
            // scene luminance without introducing a new hue on neutral scenes.
            double saturationShape = 0.42 * saturationBias;
            redBalance += (result.MeanRed - meanLuma) * saturationShape;
            greenBalance += (result.MeanGreen - meanLuma) * saturationShape;
            blueBalance += (result.MeanBlue - meanLuma) * saturationShape;
            redBalance = MathUtil.Clamp(redBalance, -0.075, 0.075);
            greenBalance = MathUtil.Clamp(greenBalance, -0.075, 0.075);
            blueBalance = MathUtil.Clamp(blueBalance, -0.075, 0.075);

            var recommendation = new FilterRecommendation {
                ProfileName = GetProfileName(
                    result, effectiveDarkScore, brightScore,
                    backlightScore, outdoorEvidence, comfortScore),
                EquivalentGamma = equivalentGamma,
                BrightnessBoost = brightnessBoost,
                ContrastBoost = contrastBoost,
                StrengthBlend = strength,
                Gamma = gamma,
                ShadowLift = shadowLift,
                HighlightCompression = compression,
                BlackPointRecovery = blackPointRecovery,
                Contrast = contrast,
                Warmth = warmth,
                RedBalance = redBalance,
                GreenBalance = greenBalance,
                BlueBalance = blueBalance
            };
            BuildLuts(recommendation);
            return recommendation;
        }

        private static void BuildLuts(FilterRecommendation recommendation)
        {
            recommendation.Red = new ushort[256];
            recommendation.Green = new ushort[256];
            recommendation.Blue = new ushort[256];

            double previousRed = 0.0;
            double previousGreen = 0.0;
            double previousBlue = 0.0;
            double totalChange = 0.0;

            for (int i = 0; i < 256; i++)
            {
                double input = i / 255.0;
                double value = Math.Pow(input, recommendation.Gamma);

                double blackFloor = recommendation.BrightnessBoost / 100.0 * 0.060;
                double toeActivation = MathUtil.SmoothStep(0.0, 0.035, input);
                value += blackFloor * toeActivation * (1.0 - value);

                double shadowMask = 1.0 - MathUtil.SmoothStep(0.42, 0.86, value);
                double toeGate = MathUtil.SmoothStep(0.005, 0.045, input);
                value += recommendation.ShadowLift *
                    4.0 * value * (1.0 - value) * shadowMask * toeGate;

                double midLift = recommendation.BrightnessBoost / 100.0 * 0.090;
                value += midLift * 4.0 * value * (1.0 - value) *
                    (1.0 - MathUtil.SmoothStep(0.72, 0.96, value));

                value = 0.46 + (value - 0.46) *
                    (1.0 + recommendation.Contrast);

                double highlightMask = MathUtil.SmoothStep(0.52, 0.92, value);
                value -= recommendation.HighlightCompression *
                    4.0 * value * (1.0 - value) * highlightMask;

                // Restore a small amount of black separation after lifting
                // shadows. Positive BlackPoint values reduce gray haze while
                // keeping the original black endpoint unchanged.
                double blackPointMask = 1.0 -
                    MathUtil.SmoothStep(0.10, 0.78, value);
                value -= recommendation.BlackPointRecovery *
                    blackPointMask * (0.45 + 0.55 * (1.0 - input));

                value = MathUtil.Clamp(value, 0.0, 1.0);
                value = MathUtil.Lerp(input, value, recommendation.StrengthBlend);
                double colorShape = 4.0 * value * (1.0 - value);
                double appliedWarmth =
                    recommendation.Warmth * recommendation.StrengthBlend;
                double appliedRedBalance =
                    recommendation.RedBalance * recommendation.StrengthBlend;
                double appliedGreenBalance =
                    recommendation.GreenBalance * recommendation.StrengthBlend;
                double appliedBlueBalance =
                    recommendation.BlueBalance * recommendation.StrengthBlend;
                double red = MathUtil.Clamp(
                    value + (appliedWarmth + appliedRedBalance) *
                    colorShape, 0.0, 1.0);
                double green = MathUtil.Clamp(
                    value + appliedGreenBalance * colorShape, 0.0, 1.0);
                double blue = MathUtil.Clamp(
                    value - appliedWarmth * colorShape +
                    appliedBlueBalance * colorShape, 0.0, 1.0);

                // SetDeviceGammaRamp requires a monotonic ramp on many drivers.
                red = Math.Max(previousRed, red);
                green = Math.Max(previousGreen, green);
                blue = Math.Max(previousBlue, blue);
                previousRed = red;
                previousGreen = green;
                previousBlue = blue;

                recommendation.Red[i] = ToWord(red);
                recommendation.Green[i] = ToWord(green);
                recommendation.Blue[i] = ToWord(blue);
                totalChange += Math.Abs(green - input);
            }

            recommendation.Red[0] = 0;
            recommendation.Green[0] = 0;
            recommendation.Blue[0] = 0;
            recommendation.Red[255] = ushort.MaxValue;
            recommendation.Green[255] = ushort.MaxValue;
            recommendation.Blue[255] = ushort.MaxValue;
            recommendation.ChangeStrength = MathUtil.Clamp(
                totalChange / 256.0 * 4.2, 0.0, 1.0);
        }

        private static string GetProfileName(
            AnalysisResult result,
            double darkScore,
            double brightScore,
            double backlightScore,
            double daylightEvidence,
            double comfortScore)
        {
            if (backlightScore > 0.50 && darkScore > 0.45 &&
                daylightEvidence < 0.45)
                return "逆光平衡";
            if (result.NightVisionScore > 0.42)
                return "夜视护眼";
            if (brightScore > 0.60)
                return "强光保护";
            if (darkScore > 0.78)
                return "暗室强增益";
            if (darkScore > 0.48)
                return "暗场增强";
            if (comfortScore > 0.48)
                return "室内柔和";
            if (daylightEvidence > 0.48)
                return "白天柔和";
            if (result.DynamicRange < 0.20)
                return "低对比增强";
            return "白天柔和";
        }

        public static ushort[] Identity()
        {
            var values = new ushort[256];
            for (int i = 0; i < values.Length; i++)
                values[i] = (ushort)(i * 257);
            return values;
        }

        private static ushort ToWord(double value)
        {
            return (ushort)Math.Round(MathUtil.Clamp(value, 0.0, 1.0) * ushort.MaxValue);
        }
    }
}
