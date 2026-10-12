using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// Звуки, встроенные в SidiuszCustomItems.Items.dll (EmbeddedResource в CustomItems.Items.csproj),
    /// а не в бандл: чтобы поменять звук, хватает dotnet build, Unity не нужен.
    /// Ресурс называется SidiuszCustomItems.Sounds.&lt;Имя&gt;.wav, формат WAV PCM 16 бит.
    /// </summary>
    public static class EmbeddedAudio
    {
        private const string Prefix = "SidiuszCustomItems.Sounds.";
        private static readonly Dictionary<string, AudioClip> Cache = new Dictionary<string, AudioClip>();

        /// <summary>Клип по имени файла без .wav; null, если такого нет или файл не читается.</summary>
        public static AudioClip Get(string name)
        {
            if (Cache.TryGetValue(name, out var clip) && clip != null)
                return clip;
            try
            {
                using var stream = typeof(EmbeddedAudio).Assembly.GetManifestResourceStream(Prefix + name + ".wav");
                if (stream == null)
                {
                    Debug.LogWarning($"[CustomItems] Нет встроенного звука {name}");
                    return null;
                }
                clip = Read(stream, name);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CustomItems] Не прочитал звук {name}: {e.Message}");
                clip = null;
            }
            Cache[name] = clip;
            return clip;
        }

        private static AudioClip Read(Stream stream, string name)
        {
            using var r = new BinaryReader(stream);
            if (new string(r.ReadChars(4)) != "RIFF")
                throw new InvalidDataException("не RIFF");
            r.ReadInt32();
            if (new string(r.ReadChars(4)) != "WAVE")
                throw new InvalidDataException("не WAVE");

            int channels = 0, rate = 0, bits = 0;
            while (stream.Position < stream.Length)
            {
                var id = new string(r.ReadChars(4));
                var size = r.ReadInt32();
                if (id == "fmt ")
                {
                    var format = r.ReadInt16();
                    channels = r.ReadInt16();
                    rate = r.ReadInt32();
                    r.ReadInt32();
                    r.ReadInt16();
                    bits = r.ReadInt16();
                    if (format != 1 || bits != 16)
                        throw new InvalidDataException("нужен PCM 16 бит");
                    stream.Position += size - 16;
                }
                else if (id == "data")
                {
                    if (channels == 0)
                        throw new InvalidDataException("data раньше fmt");
                    var count = size / 2;
                    var data = new float[count];
                    for (var i = 0; i < count; i++)
                        data[i] = r.ReadInt16() / 32768f;
                    var clip = AudioClip.Create(name, count / channels, channels, rate, false);
                    clip.SetData(data, 0);
                    return clip;
                }
                else
                {
                    stream.Position += size + (size & 1);
                }
            }
            throw new InvalidDataException("нет data");
        }
    }
}
