using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

internal static class IntegrationSmokeTest
{
    private const int WmHotkey = 0x0312;
    private const int ToggleHotkeyId = 0x5441;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(
        IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "Usage: IntegrationSmokeTest <app.exe> <source.png>");
            return 2;
        }

        string executable = Path.GetFullPath(args[0]);
        string sourceScreenshot = Path.GetFullPath(args[1]);
        string project = Directory.GetParent(
            Directory.GetParent(executable).FullName).FullName;
        string testRoot = Path.Combine(
            project,
            "test-results",
            "watcher-integration-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(testRoot);

        string settingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TarkovAutoShade");
        string settingsPath = Path.Combine(settingsFolder, "settings.json");
        string backupPath = Path.Combine(testRoot, "settings.backup.json");
        bool hadSettings = File.Exists(settingsPath);
        Process process = null;

        try
        {
            Directory.CreateDirectory(settingsFolder);
            if (hadSettings) File.Copy(settingsPath, backupPath, true);
            File.WriteAllText(
                settingsPath,
                BuildSettingsJson(testRoot),
                new UTF8Encoding(false));

            process = Process.Start(new ProcessStartInfo {
                FileName = executable,
                UseShellExecute = false
            });
            if (process == null)
                throw new InvalidOperationException("Application did not start.");

            IntPtr window = WaitForWindow(process, 10000);
            string[] initial = ReadAutomationTexts(window);
            bool hotkeyRegistered = !Contains(initial, "注册失败");
            bool configuredHotkeyVisible = Contains(initial, "F7");

            PostMessage(
                window, WmHotkey, new IntPtr(ToggleHotkeyId), IntPtr.Zero);
            Thread.Sleep(500);
            string[] normal = ReadAutomationTexts(window);
            bool normalModeVisible = Contains(normal, "未运行");

            string copiedScreenshot = Path.Combine(
                testRoot, "integration-shot.png");
            File.Copy(sourceScreenshot, copiedScreenshot, true);

            bool screenshotAnalyzed = false;
            bool stayedNormal = false;
            string status = "";
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            do
            {
                Thread.Sleep(400);
                string[] afterScreenshot = ReadAutomationTexts(window);
                screenshotAnalyzed = Contains(
                    afterScreenshot, "已应用：") || Contains(
                    afterScreenshot, "分析完成：");
                stayedNormal = Contains(afterScreenshot, "运行中");
                status = Find(afterScreenshot, "已应用：");
                if (status.Length == 0) status = Find(afterScreenshot, "分析完成：");
            }
            while (!screenshotAnalyzed && DateTime.UtcNow < deadline);

            PostMessage(
                window, WmHotkey, new IntPtr(ToggleHotkeyId), IntPtr.Zero);
            Thread.Sleep(1200);
            string[] filtered = ReadAutomationTexts(window);
            bool filterModeRestored = Contains(filtered, "开启滤镜");

            bool analyzeButtonFound = FindAutomationElement(window, "分析截图") != null;
            bool manualAnalyzePreviewed = false;
            bool manualAnalyzeAppliedBeforeClick = false;
            bool manualApplyApplied = false;
            string manualStatus = "";
            if (analyzeButtonFound)
            {
                InvokeAutomationElement(window, "分析截图");
                deadline = DateTime.UtcNow.AddSeconds(15);
                do
                {
                    Thread.Sleep(300);
                    string[] afterManualAnalyze = ReadAutomationTexts(window);
                    manualAnalyzePreviewed = Contains(
                        afterManualAnalyze, "分析完成：");
                    if (manualAnalyzePreviewed)
                        manualAnalyzeAppliedBeforeClick = Contains(
                            afterManualAnalyze, "已应用：");
                    manualStatus = Find(afterManualAnalyze, "分析完成：");
                }
                while (!manualAnalyzePreviewed && DateTime.UtcNow < deadline);
            }

            bool applyButtonFound = FindAutomationElement(window, "开启滤镜") != null;
            if (applyButtonFound)
            {
                InvokeAutomationElement(window, "开启滤镜");
                deadline = DateTime.UtcNow.AddSeconds(15);
                do
                {
                    Thread.Sleep(300);
                    string[] afterManualApply = ReadAutomationTexts(window);
                    manualApplyApplied = Contains(afterManualApply, "已应用：");
                    if (manualApplyApplied)
                        manualStatus = Find(afterManualApply, "已应用：");
                }
                while (!manualApplyApplied && DateTime.UtcNow < deadline);
            }

            Console.WriteLine("Process responding:       " + process.Responding);
            Console.WriteLine("Hotkey registered:       " + hotkeyRegistered);
            Console.WriteLine("Configured hotkey F7:    " + configuredHotkeyVisible);
            Console.WriteLine("Normal mode visible:     " + normalModeVisible);
            Console.WriteLine("Screenshot auto-analyzed:" + screenshotAnalyzed);
            Console.WriteLine("Stayed normal:           " + stayedNormal);
            Console.WriteLine("Filter mode restored:    " + filterModeRestored);
            Console.WriteLine("Manual preview ready:    " + manualAnalyzePreviewed);
            Console.WriteLine("Applied before click:    " + manualAnalyzeAppliedBeforeClick);
            Console.WriteLine("Manual apply applied:    " + manualApplyApplied);
            Console.WriteLine("Preview button found:    " + analyzeButtonFound);
            Console.WriteLine("Apply button found:      " + applyButtonFound);
            Console.WriteLine("Status:                  " + status);
            Console.WriteLine("Manual status:           " + manualStatus);
            Console.WriteLine("Test folder:             " + testRoot);

            return process.Responding && hotkeyRegistered && configuredHotkeyVisible &&
                normalModeVisible && screenshotAnalyzed &&
                stayedNormal && filterModeRestored &&
                manualAnalyzePreviewed && !manualAnalyzeAppliedBeforeClick &&
                manualApplyApplied ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.CloseMainWindow();
                        if (!process.WaitForExit(5000)) process.Kill();
                    }
                }
                catch
                {
                }
            }

            try
            {
                if (hadSettings)
                    File.Copy(backupPath, settingsPath, true);
                else if (File.Exists(settingsPath))
                    File.Delete(settingsPath);
            }
            catch
            {
            }
        }
    }

    private static string BuildSettingsJson(string screenshotFolder)
    {
        return "{" +
            "\"AlgorithmVersion\":6," +
            "\"AutoWatch\":true," +
            "\"BlackPoint\":56," +
            "\"ColorCorrection\":72," +
            "\"ContrastBias\":0," +
            "\"DisplayDevice\":\"\"," +
            "\"ExposureBias\":0," +
            "\"HighlightProtection\":76," +
            "\"IndoorComfort\":72," +
            "\"MaxStrength\":82," +
            "\"ScreenshotFolder\":\"" + EscapeJson(screenshotFolder) + "\"," +
            "\"ShadowTarget\":70," +
            "\"Warmth\":0," +
            "\"SceneGuard\":88" +
            ",\"HotkeyKeyCode\":118," +
            "\"HotkeyModifiers\":0" +
            "}";
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static IntPtr WaitForWindow(Process process, int timeoutMilliseconds)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        do
        {
            process.Refresh();
            if (process.HasExited)
                throw new InvalidOperationException(
                    "Application exited before opening a window.");
            if (process.MainWindowHandle != IntPtr.Zero)
                return process.MainWindowHandle;
            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException("Application window did not appear.");
    }

    private static string[] ReadAutomationTexts(IntPtr parent)
    {
        var result = new List<string>();
        AutomationElement root = AutomationElement.FromHandle(parent);
        AutomationElementCollection elements = root.FindAll(
            TreeScope.Descendants, Condition.TrueCondition);
        foreach (AutomationElement element in elements)
        {
            try
            {
                string name = element.Current.Name;
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
            }
            catch (ElementNotAvailableException) { }
        }
        return result.ToArray();
    }

    private static AutomationElement FindAutomationElement(
        IntPtr parent, string expected)
    {
        AutomationElement root = AutomationElement.FromHandle(parent);
        AutomationElement exact = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, expected));
        if (exact != null) return exact;

        AutomationElementCollection elements = root.FindAll(
            TreeScope.Descendants, Condition.TrueCondition);
        foreach (AutomationElement element in elements)
        {
            try
            {
                if (element.Current.Name.IndexOf(
                    expected, StringComparison.Ordinal) >= 0)
                    return element;
            }
            catch (ElementNotAvailableException) { }
        }
        return null;
    }

    private static void InvokeAutomationElement(IntPtr parent, string expected)
    {
        AutomationElement element = FindAutomationElement(parent, expected);
        if (element == null)
            throw new InvalidOperationException("UI element not found: " + expected);
        InvokePattern pattern = element.GetCurrentPattern(InvokePattern.Pattern)
            as InvokePattern;
        if (pattern == null)
            throw new InvalidOperationException("UI element is not invokable: " + expected);
        pattern.Invoke();
    }

    private static bool Contains(string[] values, string expected)
    {
        return Find(values, expected).Length > 0;
    }

    private static string Find(string[] values, string expected)
    {
        foreach (string value in values)
            if (value.IndexOf(expected, StringComparison.Ordinal) >= 0)
                return value;
        return "";
    }
}
