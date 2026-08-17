using System;
using System.Collections.Generic;
using System.IO;

namespace TarkovAutoShade
{
    internal static class ScreenshotFolderLocator
    {
        public static string Find()
        {
            var roots = new List<string>();
            AddRoot(roots, Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments));
            AddRoot(roots, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents"));
            AddRoot(roots, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "OneDrive", "Documents"));

            foreach (string root in roots)
            {
                string standard = Path.Combine(root,
                    "Escape from Tarkov", "Screenshots");
                if (Directory.Exists(standard)) return standard;

                try
                {
                    foreach (string gameFolder in Directory.GetDirectories(
                        root, "Escape from Tarkov", SearchOption.TopDirectoryOnly))
                    {
                        string screenshots = Path.Combine(gameFolder, "Screenshots");
                        if (Directory.Exists(screenshots)) return screenshots;
                    }
                }
                catch
                {
                    // An inaccessible Documents location is simply skipped.
                }
            }
            return null;
        }

        private static void AddRoot(List<string> roots, string root)
        {
            if (string.IsNullOrWhiteSpace(root) ||
                !Directory.Exists(root) || roots.Contains(root)) return;
            roots.Add(root);
        }
    }
}
