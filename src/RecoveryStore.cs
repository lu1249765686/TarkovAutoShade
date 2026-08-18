using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TarkovAutoShade
{
    internal static class RecoveryStore
    {
        private const string LegacyMagic = "TASR1";
        private const string Magic = "TASR2";
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
            var ramps = new Dictionary<string, GammaRamp>(StringComparer.OrdinalIgnoreCase);
            ramps[deviceName ?? ""] = ramp;
            Save(ramps);
        }

        public static void Save(IDictionary<string, GammaRamp> ramps)
        {
            if (Exists || ramps == null || ramps.Count == 0) return;

            var validRamps = new List<KeyValuePair<string, GammaRamp>>();
            foreach (KeyValuePair<string, GammaRamp> item in ramps)
            {
                if (!string.IsNullOrWhiteSpace(item.Key) &&
                    item.Value.Red != null && item.Value.Green != null &&
                    item.Value.Blue != null)
                    validRamps.Add(item);
            }
            if (validRamps.Count == 0) return;

            Directory.CreateDirectory(Folder);
            string temporary = FilePath + ".tmp";
            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(validRamps.Count);
                foreach (KeyValuePair<string, GammaRamp> item in validRamps)
                {
                    writer.Write(item.Key);
                    WriteChannel(writer, item.Value.Red);
                    WriteChannel(writer, item.Value.Green);
                    WriteChannel(writer, item.Value.Blue);
                }
            }
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(temporary, FilePath);
        }

        public static bool TryLoad(out string deviceName, out GammaRamp ramp)
        {
            deviceName = "";
            ramp = GammaRamp.CreateEmpty();
            Dictionary<string, GammaRamp> ramps;
            if (!TryLoadAll(out ramps) || ramps.Count == 0) return false;
            foreach (KeyValuePair<string, GammaRamp> item in ramps)
            {
                deviceName = item.Key;
                ramp = item.Value;
                return true;
            }
            return false;
        }

        public static bool TryLoadAll(out Dictionary<string, GammaRamp> ramps)
        {
            ramps = new Dictionary<string, GammaRamp>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return false;
                using (var stream = File.OpenRead(FilePath))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    string magic = reader.ReadString();
                    if (magic == LegacyMagic)
                    {
                        string deviceName = reader.ReadString();
                        GammaRamp ramp = GammaRamp.CreateEmpty();
                        ReadChannel(reader, ramp.Red);
                        ReadChannel(reader, ramp.Green);
                        ReadChannel(reader, ramp.Blue);
                        if (stream.Position != stream.Length) return false;
                        ramps[deviceName] = ramp;
                        return true;
                    }
                    if (magic != Magic) return false;

                    int count = reader.ReadInt32();
                    if (count <= 0 || count > 32) return false;
                    for (int i = 0; i < count; i++)
                    {
                        string deviceName = reader.ReadString();
                        GammaRamp ramp = GammaRamp.CreateEmpty();
                        ReadChannel(reader, ramp.Red);
                        ReadChannel(reader, ramp.Green);
                        ReadChannel(reader, ramp.Blue);
                        ramps[deviceName] = ramp;
                    }
                    return stream.Position == stream.Length && ramps.Count > 0;
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
