using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TarkovAutoShade
{
    internal sealed class MonitorCapabilities
    {
        public string MonitorName = "未检测到物理显示器";
        public bool DdcCiAvailable;
        public bool BrightnessSupported;
        public bool ContrastSupported;
        public int Brightness;
        public int BrightnessMaximum = 100;
        public int Contrast;
        public int ContrastMaximum = 100;
        public string Detail = "当前显示器未公开 DDC/CI 控制。";
    }

    internal sealed class DdcCiController
    {
        private const uint McCapsBrightness = 0x00000002;
        private const uint McCapsContrast = 0x00000004;
        private const byte VcpBrightness = 0x10;
        private const byte VcpContrast = 0x12;

        public MonitorCapabilities Probe(string displayDevice)
        {
            var result = new MonitorCapabilities();
            PhysicalMonitor[] monitors = null;
            try
            {
                if (!TryOpen(displayDevice, out monitors, out result.Detail))
                    return result;
                result.DdcCiAvailable = true;
                result.MonitorName = string.IsNullOrWhiteSpace(
                    monitors[0].Description) ? "DDC/CI 显示器" : monitors[0].Description;

                uint current;
                uint maximum;
                uint type;
                uint capabilities;
                uint ignored;
                bool capabilitiesRead = GetMonitorCapabilities(monitors[0].Handle,
                    out capabilities, out ignored);

                // Some monitors expose VCP 0x10/0x12 but reject the optional
                // capability-list query. Probe the features directly so the
                // hardware controls remain usable in that case.
                if ((capabilitiesRead && (capabilities & McCapsBrightness) != 0) &&
                    TryReadFeature(monitors[0].Handle, VcpBrightness,
                        out type, out current, out maximum))
                {
                    result.BrightnessSupported = true;
                    result.Brightness = (int)current;
                    result.BrightnessMaximum = Math.Max(1, (int)maximum);
                }
                if ((capabilitiesRead && (capabilities & McCapsContrast) != 0) &&
                    TryReadFeature(monitors[0].Handle, VcpContrast,
                        out type, out current, out maximum))
                {
                    result.ContrastSupported = true;
                    result.Contrast = (int)current;
                    result.ContrastMaximum = Math.Max(1, (int)maximum);
                }

                if (!result.BrightnessSupported &&
                    TryReadFeature(monitors[0].Handle, VcpBrightness,
                        out type, out current, out maximum))
                {
                    result.BrightnessSupported = true;
                    result.Brightness = (int)current;
                    result.BrightnessMaximum = Math.Max(1, (int)maximum);
                }
                if (!result.ContrastSupported &&
                    TryReadFeature(monitors[0].Handle, VcpContrast,
                        out type, out current, out maximum))
                {
                    result.ContrastSupported = true;
                    result.Contrast = (int)current;
                    result.ContrastMaximum = Math.Max(1, (int)maximum);
                }

                result.Detail = result.BrightnessSupported || result.ContrastSupported ?
                    "DDC/CI 已连接；只开放显示器实际报告支持的项目。" :
                    (capabilitiesRead ? "显示器已连接，但未报告亮度或对比度控制。" :
                        "已找到 DDC/CI 显示器，但无法读取功能列表。");
                return result;
            }
            catch (DllNotFoundException)
            {
                result.DdcCiAvailable = false;
                result.Detail = "系统没有可用的 DDC/CI 接口。";
                return result;
            }
            catch (EntryPointNotFoundException)
            {
                result.DdcCiAvailable = false;
                result.Detail = "当前系统不提供所需的 DDC/CI 接口。";
                return result;
            }
            finally
            {
                Close(monitors);
            }
        }

        public bool SetBrightness(string displayDevice, int value, out string error)
        {
            return SetFeature(displayDevice, VcpBrightness, value, out error);
        }

        public bool SetContrast(string displayDevice, int value, out string error)
        {
            return SetFeature(displayDevice, VcpContrast, value, out error);
        }

        private static bool SetFeature(string displayDevice, byte code, int value,
            out string error)
        {
            error = "";
            PhysicalMonitor[] monitors = null;
            try
            {
                if (!TryOpen(displayDevice, out monitors, out error)) return false;
                if (!SetVCPFeature(monitors[0].Handle, code, (uint)Math.Max(0, value)))
                {
                    error = "显示器拒绝了 DDC/CI 调整请求。";
                    return false;
                }
                return true;
            }
            catch (DllNotFoundException)
            {
                error = "系统没有可用的 DDC/CI 接口。";
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                error = "系统没有可用的 DDC/CI 接口。";
                return false;
            }
            finally
            {
                Close(monitors);
            }
        }

        private static bool TryOpen(string displayDevice,
            out PhysicalMonitor[] monitors, out string error)
        {
            monitors = null;
            error = "";
            Screen screen = null;
            foreach (Screen item in Screen.AllScreens)
            {
                if (string.Equals(item.DeviceName, displayDevice,
                    StringComparison.OrdinalIgnoreCase))
                {
                    screen = item;
                    break;
                }
            }
            if (screen == null)
            {
                error = "未找到目标显示器。";
                return false;
            }

            Point center = new Point(screen.Bounds.Left + screen.Bounds.Width / 2,
                screen.Bounds.Top + screen.Bounds.Height / 2);
            IntPtr handle = MonitorFromPoint(center, 2);
            uint count;
            if (handle == IntPtr.Zero ||
                !GetNumberOfPhysicalMonitorsFromHMONITOR(handle, out count) ||
                count == 0)
            {
                error = "此显示器没有可访问的 DDC/CI 控制。";
                return false;
            }

            monitors = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(handle, count, monitors))
            {
                error = "无法打开显示器的 DDC/CI 通道。";
                monitors = null;
                return false;
            }
            return true;
        }

        private static void Close(PhysicalMonitor[] monitors)
        {
            if (monitors == null || monitors.Length == 0) return;
            try { DestroyPhysicalMonitors((uint)monitors.Length, monitors); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private static bool TryReadFeature(IntPtr monitor, byte code,
            out uint type, out uint current, out uint maximum)
        {
            type = 0;
            current = 0;
            maximum = 0;
            return GetVCPFeatureAndVCPFeatureReply(monitor, code,
                out type, out current, out maximum) && maximum > 0;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PhysicalMonitor
        {
            public IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point point, uint flags);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
            IntPtr monitor, out uint count);

        [DllImport("dxva2.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(
            IntPtr monitor, uint count, [Out] PhysicalMonitor[] physicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(
            uint count, [In] PhysicalMonitor[] physicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorCapabilities(
            IntPtr monitor, out uint capabilities, out uint colorTemperatures);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetVCPFeatureAndVCPFeatureReply(
            IntPtr monitor, byte code, out uint type, out uint currentValue,
            out uint maximumValue);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetVCPFeature(
            IntPtr monitor, byte code, uint newValue);
    }
}
