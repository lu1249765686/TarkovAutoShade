using System;
using System.Threading;
using System.Windows;

namespace TarkovAutoShade
{
    public partial class App : Application
    {
        private const string InstanceMutexName = "Local\\TarkovAutoShade.SingleInstance";
        private Mutex instanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            instanceMutex = new Mutex(true, InstanceMutexName, out createdNew);
            if (!createdNew)
            {
                instanceMutex.Dispose();
                instanceMutex = null;
                MessageBox.Show("TarkovAutoShade 已在运行，无法同时打开多个窗口。",
                    "TarkovAutoShade", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            if (Environment.OSVersion.Version.Major >= 6)
            {
                SetProcessDPIAware();
            }

            base.OnStartup(e);
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (instanceMutex != null)
            {
                try { instanceMutex.ReleaseMutex(); }
                catch (ApplicationException) { }
                instanceMutex.Dispose();
                instanceMutex = null;
            }
            base.OnExit(e);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }
}
