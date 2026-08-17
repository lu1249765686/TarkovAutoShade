using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TarkovAutoShade
{
    internal sealed class GammaRampController : IDisposable
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, GammaRamp> baselines =
            new Dictionary<string, GammaRamp>(StringComparer.OrdinalIgnoreCase);
        private GammaRamp currentRamp;
        private bool hasCurrentRamp;
        private string activeDevice;
        private System.Threading.Timer transitionTimer;
        private int transitionStep;
        private int transitionSteps;
        private GammaRamp transitionFrom;
        private GammaRamp transitionTo;
        private bool hasTransition;
        private bool disposed;

        public string ActiveDevice { get { return activeDevice; } }
        public bool HasActiveFilter { get { return (hasCurrentRamp || hasTransition) && activeDevice != null; } }

        public static List<DisplayTarget> EnumerateDisplays()
        {
            var result = new List<DisplayTarget>();
            foreach (Screen screen in Screen.AllScreens)
            {
                result.Add(new DisplayTarget {
                    DeviceName = screen.DeviceName,
                    FriendlyName = screen.DeviceName.Replace(@"\\.\", ""),
                    Primary = screen.Primary
                });
            }
            return result;
        }

        public bool CaptureBaseline(string deviceName, out string error)
        {
            error = "";
            lock (sync)
            {
                if (baselines.ContainsKey(deviceName)) return true;
                GammaRamp ramp;
                if (!TryGet(deviceName, out ramp, out error)) return false;
                baselines[deviceName] = ramp.Clone();
                return true;
            }
        }

        public bool TransitionTo(
            string deviceName,
            FilterRecommendation recommendation,
            int durationMilliseconds,
            out string error)
        {
            error = "";
            if (recommendation == null)
            {
                error = "没有可应用的滤镜建议。";
                return false;
            }

            lock (sync)
            {
                if (!CaptureBaseline(deviceName, out error)) return false;
                GammaRamp target = GammaRamp.FromRecommendation(recommendation);
                GammaRamp from = currentRamp;
                if (!hasCurrentRamp || !string.Equals(
                    activeDevice, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    if (activeDevice != null &&
                        !string.Equals(activeDevice, deviceName, StringComparison.OrdinalIgnoreCase))
                        RestoreInternal(activeDevice);
                    from = baselines[deviceName].Clone();
                }

                StopTransition();
                if (durationMilliseconds <= 0)
                {
                    if (!TrySet(deviceName, target, out error)) return false;
                    currentRamp = target.Clone();
                    hasCurrentRamp = true;
                    activeDevice = deviceName;
                    return true;
                }
                transitionFrom = from.Clone();
                transitionTo = target;
                transitionStep = 0;
                // Keep the original 50 ms cadence so scene changes settle
                // quickly enough for an FPS without a long visual lag.
                transitionSteps = Math.Max(1, durationMilliseconds / 50);
                activeDevice = deviceName;
                hasTransition = true;

                transitionTimer = new System.Threading.Timer(delegate {
                    TickTransition();
                }, null, 0, 50);
                return true;
            }
        }

        public bool Reapply(out string error)
        {
            lock (sync)
            {
                error = "";
                if (!hasCurrentRamp || activeDevice == null) return true;
                return TrySet(activeDevice, currentRamp, out error);
            }
        }

        public bool RestoreAll(out string error)
        {
            lock (sync)
            {
                StopTransition();
                error = "";
                bool success = true;
                foreach (KeyValuePair<string, GammaRamp> item in baselines)
                {
                    string itemError;
                    if (!TrySet(item.Key, item.Value, out itemError))
                    {
                        success = false;
                        if (error.Length == 0) error = itemError;
                    }
                }
                hasCurrentRamp = false;
                activeDevice = null;
                return success;
            }
        }

        public GammaRamp? GetBaseline(string deviceName)
        {
            lock (sync)
            {
                GammaRamp ramp;
                return baselines.TryGetValue(deviceName, out ramp) ?
                    (GammaRamp?)ramp.Clone() : null;
            }
        }

        public bool ApplyDirect(string deviceName, GammaRamp ramp, out string error)
        {
            lock (sync)
            {
                return TrySet(deviceName, ramp, out error);
            }
        }

        public bool Probe(string deviceName, out string error)
        {
            GammaRamp ignored;
            return TryGet(deviceName, out ignored, out error);
        }

        private void TickTransition()
        {
            lock (sync)
            {
                if (transitionTimer == null || !hasTransition)
                    return;

                transitionStep++;
                double amount = MathUtil.Clamp(
                    transitionStep / (double)transitionSteps, 0.0, 1.0);
                amount = amount * amount * (3.0 - 2.0 * amount);
                GammaRamp ramp = GammaRamp.Lerp(transitionFrom, transitionTo, amount);
                string ignored;
                if (!TrySet(activeDevice, ramp, out ignored))
                {
                    StopTransition();
                    return;
                }
                currentRamp = ramp;
                hasCurrentRamp = true;

                if (transitionStep >= transitionSteps)
                {
                    currentRamp = transitionTo.Clone();
                    hasCurrentRamp = true;
                    StopTransition();
                }
            }
        }

        private bool RestoreInternal(string deviceName)
        {
            GammaRamp baseline;
            if (!baselines.TryGetValue(deviceName, out baseline)) return true;
            string ignored;
            return TrySet(deviceName, baseline, out ignored);
        }

        private void StopTransition()
        {
            if (transitionTimer != null)
            {
                transitionTimer.Dispose();
                transitionTimer = null;
            }
            hasTransition = false;
        }

        private static bool TryGet(string deviceName, out GammaRamp ramp, out string error)
        {
            ramp = GammaRamp.CreateEmpty();
            error = "";
            IntPtr dc = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero)
            {
                error = "无法创建设备上下文：" + deviceName;
                return false;
            }
            try
            {
                var value = GammaRamp.CreateEmpty();
                if (!GetDeviceGammaRamp(dc, ref value))
                {
                    error = "读取系统 Gamma 失败（错误 " +
                        Marshal.GetLastWin32Error().ToString() + "）。";
                    return false;
                }
                ramp = value;
                return true;
            }
            finally
            {
                DeleteDC(dc);
            }
        }

        private static bool TrySet(
            string deviceName, GammaRamp ramp, out string error)
        {
            error = "";
            IntPtr dc = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero)
            {
                error = "无法访问显示器：" + deviceName;
                return false;
            }
            try
            {
                GammaRamp copy = ramp.Clone();
                if (!SetDeviceGammaRamp(dc, ref copy))
                {
                    error = "应用 Gamma 曲线失败。请关闭 HDR，并检查显卡色彩设置。";
                    return false;
                }
                return true;
            }
            finally
            {
                DeleteDC(dc);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            string ignored;
            RestoreAll(out ignored);
        }

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDC(
            string driver, string device, string output, IntPtr initData);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool GetDeviceGammaRamp(IntPtr dc, ref GammaRamp ramp);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool SetDeviceGammaRamp(IntPtr dc, ref GammaRamp ramp);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct GammaRamp
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Blue;

        public static GammaRamp CreateEmpty()
        {
            return new GammaRamp {
                Red = new ushort[256],
                Green = new ushort[256],
                Blue = new ushort[256]
            };
        }

        public static GammaRamp FromRecommendation(FilterRecommendation recommendation)
        {
            return new GammaRamp {
                Red = (ushort[])recommendation.Red.Clone(),
                Green = (ushort[])recommendation.Green.Clone(),
                Blue = (ushort[])recommendation.Blue.Clone()
            };
        }

        public GammaRamp Clone()
        {
            return new GammaRamp {
                Red = Red == null ? new ushort[256] : (ushort[])Red.Clone(),
                Green = Green == null ? new ushort[256] : (ushort[])Green.Clone(),
                Blue = Blue == null ? new ushort[256] : (ushort[])Blue.Clone()
            };
        }

        public static GammaRamp Lerp(GammaRamp from, GammaRamp to, double amount)
        {
            GammaRamp result = CreateEmpty();
            for (int i = 0; i < 256; i++)
            {
                result.Red[i] = Interpolate(from.Red[i], to.Red[i], amount);
                result.Green[i] = Interpolate(from.Green[i], to.Green[i], amount);
                result.Blue[i] = Interpolate(from.Blue[i], to.Blue[i], amount);
            }
            return result;
        }

        private static ushort Interpolate(ushort from, ushort to, double amount)
        {
            return (ushort)Math.Round(from + (to - from) * amount);
        }
    }
}
