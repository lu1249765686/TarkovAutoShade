using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TarkovAutoShade
{
    // The OBS plugin consumes this small, dependency-free contract instead of
    // reading the WPF settings format or depending on the recorder executable.
    internal static class ObsFilterStateStore
    {
        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TarkovAutoShade");
        private static readonly string FilePath = Path.Combine(
            Folder, "obs-filter-state.taslut");

        public static void WriteActive(
            FilterRecommendation recommendation, int transitionMilliseconds = 300)
        {
            if (recommendation == null || recommendation.Red == null ||
                recommendation.Green == null || recommendation.Blue == null)
            {
                WriteDisabled();
                return;
            }

            WriteFile(delegate(StreamWriter writer)
            {
                writer.WriteLine("TAS_OBS_LUT=1");
                writer.WriteLine("enabled=1");
                writer.WriteLine("transition_ms=" + Math.Max(
                    0, transitionMilliseconds).ToString(CultureInfo.InvariantCulture));
                writer.WriteLine("profile=" + (recommendation.ProfileName ?? ""));
                writer.WriteLine("red=" + FormatRamp(recommendation.Red));
                writer.WriteLine("green=" + FormatRamp(recommendation.Green));
                writer.WriteLine("blue=" + FormatRamp(recommendation.Blue));
            });
        }

        public static void WriteDisabled()
        {
            WriteFile(delegate(StreamWriter writer)
            {
                writer.WriteLine("TAS_OBS_LUT=1");
                writer.WriteLine("enabled=0");
            });
        }

        private static string FormatRamp(ushort[] values)
        {
            var builder = new StringBuilder(values.Length * 6);
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private static void WriteFile(Action<StreamWriter> write)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string temporary = FilePath + ".tmp";
                using (var stream = new FileStream(
                    temporary, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    write(writer);
                }
                // Replace the published file atomically so OBS never observes
                // the short delete/move gap while polling during a recording.
                if (File.Exists(FilePath))
                {
                    File.Replace(temporary, FilePath, null);
                }
                else
                {
                    File.Move(temporary, FilePath);
                }
            }
            catch
            {
                // State export must never prevent display Gamma restoration.
            }
        }
    }
}
