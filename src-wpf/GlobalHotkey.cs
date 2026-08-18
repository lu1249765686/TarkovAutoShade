using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TarkovAutoShade
{
    /// <summary>
    /// Global hotkey registration for WPF
    /// </summary>
    public class GlobalHotkey : IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const uint ModNoRepeat = 0x4000;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public enum Modifiers : uint
        {
            None = 0,
            Alt = 1,
            Control = 2,
            Shift = 4,
            Win = 8,
            NoRepeat = 0x4000
        }

        private readonly Window window;
        private readonly int hotkeyId;
        private Modifiers modifiers;
        private System.Windows.Forms.Keys key;
        private HwndSource hwndSource;
        private bool registered;
        private bool loaded;

        public event EventHandler HotkeyPressed;

        public bool IsRegistered { get { return registered; } }

        public GlobalHotkey(Window window, int hotkeyId, Modifiers modifiers, System.Windows.Forms.Keys key)
        {
            this.window = window;
            this.hotkeyId = hotkeyId;
            this.modifiers = modifiers;
            this.key = key;
            loaded = window.IsLoaded;

            window.Loaded += OnWindowLoaded;

            window.Closed += OnWindowClosed;
            if (loaded) Register();
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            Dispose();
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            loaded = true;
            Register();
        }

        public bool Register()
        {
            if (!loaded || registered) return registered;
            var helper = new WindowInteropHelper(window);
            hwndSource = HwndSource.FromHwnd(helper.Handle);
            if (hwndSource == null) return false;
            hwndSource.AddHook(HwndHook);
            registered = RegisterHotKey(
                helper.Handle,
                hotkeyId,
                (uint)modifiers | ModNoRepeat,
                (uint)key);
            if (!registered) hwndSource.RemoveHook(HwndHook);
            return registered;
        }

        public bool Rebind(Modifiers newModifiers, System.Windows.Forms.Keys newKey)
        {
            modifiers = newModifiers;
            key = newKey;
            if (!loaded) return false;

            UnregisterCurrent();
            return Register();
        }

        public void Suspend()
        {
            UnregisterCurrent();
        }

        public bool Resume()
        {
            return Register();
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && wParam.ToInt32() == hotkeyId)
            {
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            UnregisterCurrent();
            window.Loaded -= OnWindowLoaded;
            window.Closed -= OnWindowClosed;
            loaded = false;
        }

        private void UnregisterCurrent()
        {
            if (hwndSource == null) return;

            var helper = new WindowInteropHelper(window);
            if (registered) UnregisterHotKey(helper.Handle, hotkeyId);
            hwndSource.RemoveHook(HwndHook);
            registered = false;
            hwndSource = null;
        }
    }
}
