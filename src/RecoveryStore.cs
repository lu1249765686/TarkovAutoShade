using System;
using System.IO;
using System.Text;

namespace TarkovAutoShade
{
    internal static class RecoveryStore
    {
        private const string Magic = "TASR1";
        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TarkovAutoShade");
        private static readonly string FilePath = Path.Combine(Folder, "gamma-recovery.bin");

        public static bool Exists
        {
            get { return File.Exists(FilePath); }
        }

        public static void Save(string deviceName, GammaRamp ramp)
        {
            if (Exists || ramp.Red == null || ramp.Green == null || ramp.Blue == null)
                return;

            Directory.CreateDirectory(Folder);
            string temporary = FilePath + ".tmp";
            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(deviceName ?? "");
                WriteChannel(writer, ramp.Red);
                WriteChannel(writer, ramp.Green);
                WriteChannel(writer, ramp.Blue);
            }
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(temporary, FilePath);
        }

        public static bool TryLoad(out string deviceName, out GammaRamp ramp)
        {
            deviceName = "";
            ramp = GammaRamp.CreateEmpty();
            try
            {
                if (!File.Exists(FilePath)) return false;
                using (var stream = File.OpenRead(FilePath))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadString() != Magic) return false;
                    deviceName = reader.ReadString();
                    ReadChannel(reader, ramp.Red);
                    ReadChannel(reader, ramp.Green);
                    ReadChannel(reader, ramp.Blue);
                    return stream.Position == stream.Length;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch
            {
            }
        }

        private static void WriteChannel(BinaryWriter writer, ushort[] channel)
        {
            for (int i = 0; i < 256; i++) writer.Write(channel[i]);
        }

        private static void ReadChannel(BinaryReader reader, ushort[] channel)
        {
            for (int i = 0; i < 256; i++) channel[i] = reader.ReadUInt16();
        }
    }
}
