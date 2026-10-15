using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace CustomItems.Items
{
    /// <summary>
    /// Фразы Алисы из папки alice-phrases рядом с DLL, а не из бандла: новый пак записей подключается без Unity.
    /// Путь подпапки — тема, как в Phrases («alice-phrases/IsAlive/Yes/Живой!.wav» → тема IsAlive/Yes),
    /// имя файла без расширения — текст фразы. WAV (PCM 8/16/24/32 бит или float), OGG, MP3.
    /// Клипы добавляются к темам бандла по порядку имён: у всех игроков с одинаковой папкой индексы совпадают.
    /// </summary>
    public static class LoosePhrases
    {
        /// <summary>Папка с фразами (Plugin задаёт: &lt;папка мода&gt;/alice-phrases).</summary>
        public static string Dir;

        private static Dictionary<string, List<AudioClip>> loaded;

        /// <summary>Тема → клипы (читаются один раз, при первой колонке).</summary>
        public static Dictionary<string, List<AudioClip>> All
        {
            get
            {
                if (loaded == null)
                    loaded = Load();
                return loaded;
            }
        }

        private static Dictionary<string, List<AudioClip>> Load()
        {
            var result = new Dictionary<string, List<AudioClip>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(Dir) || !Directory.Exists(Dir))
                return result;
            var files = Directory.GetFiles(Dir, "*.*", SearchOption.AllDirectories)
                .Where(f => IsAudio(f))
                .OrderBy(f => f.Replace('\\', '/'), StringComparer.Ordinal)
                .ToList();
            var count = 0;
            foreach (var file in files)
            {
                var folder = Path.GetDirectoryName(file);
                if (folder == null || folder.Length <= Dir.TrimEnd('/', '\\').Length)
                    continue; // файл прямо в alice-phrases — без темы
                var topic = folder.Substring(Dir.TrimEnd('/', '\\').Length + 1).Replace('\\', '/');
                if (topic.StartsWith("_"))
                    continue; // _unsorted и т.п. — записи без темы, лежат до разбора
                var name = Path.GetFileNameWithoutExtension(file);
                AudioClip clip;
                try
                {
                    clip = Path.GetExtension(file).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                        ? ReadWav(File.ReadAllBytes(file), name) ?? ReadUnity(file, name)
                        : ReadUnity(file, name);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Алиса] Не прочитала фразу {file}: {e.Message}");
                    continue;
                }
                if (clip == null)
                {
                    Debug.LogWarning($"[Алиса] Не прочитала фразу {file}");
                    continue;
                }
                clip.name = name;
                if (!result.TryGetValue(topic, out var list))
                    result[topic] = list = new List<AudioClip>();
                list.Add(clip);
                count++;
            }
            Debug.Log($"[Алиса] Фразы из {Dir}: {count} в {result.Count} темах");
            return result;
        }

        private static bool IsAudio(string file)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            return ext == ".wav" || ext == ".ogg" || ext == ".mp3";
        }

        // OGG, MP3 и необычный WAV — средствами Unity (локальный файл читается сразу)
        private static AudioClip ReadUnity(string file, string name)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var type = ext == ".mp3" ? AudioType.MPEG : ext == ".ogg" ? AudioType.OGGVORBIS : AudioType.WAV;
            using var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri, type);
            ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = false;
            var op = request.SendWebRequest();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!op.isDone && DateTime.UtcNow < until)
                System.Threading.Thread.Sleep(1);
            if (request.result != UnityWebRequest.Result.Success)
                return null;
            return DownloadHandlerAudioClip.GetContent(request);
        }

        // WAV сами: PCM 8/16/24/32 бит или float 32, любой порядок чанков
        private static AudioClip ReadWav(byte[] bytes, string name)
        {
            if (bytes.Length < 12 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F'
                || bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
                return null;
            int format = 0, channels = 0, rate = 0, bits = 0, pos = 12;
            while (pos + 8 <= bytes.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                var size = BitConverter.ToInt32(bytes, pos + 4);
                var body = pos + 8;
                if (size < 0 || body + size > bytes.Length)
                    size = bytes.Length - body;
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(bytes, body);
                    channels = BitConverter.ToInt16(bytes, body + 2);
                    rate = BitConverter.ToInt32(bytes, body + 4);
                    bits = BitConverter.ToInt16(bytes, body + 14);
                    if (format == -2 && size >= 26) // WAVE_FORMAT_EXTENSIBLE: настоящий формат в подтипе
                        format = BitConverter.ToInt16(bytes, body + 24);
                }
                else if (id == "data" && channels > 0)
                {
                    var step = bits / 8;
                    if (step == 0 || (format != 1 && format != 3))
                        return null;
                    var count = size / step;
                    var data = new float[count];
                    for (var i = 0; i < count; i++)
                    {
                        var at = body + i * step;
                        data[i] = format == 3 && bits == 32 ? BitConverter.ToSingle(bytes, at)
                            : bits == 8 ? (bytes[at] - 128) / 128f
                            : bits == 16 ? BitConverter.ToInt16(bytes, at) / 32768f
                            : bits == 24 ? ((bytes[at] << 8 | bytes[at + 1] << 16 | bytes[at + 2] << 24) >> 8) / 8388608f
                            : bits == 32 ? BitConverter.ToInt32(bytes, at) / 2147483648f
                            : 0f;
                    }
                    var clip = AudioClip.Create(name, count / channels, channels, rate, false);
                    clip.SetData(data, 0);
                    return clip;
                }
                pos = body + size + (size & 1);
            }
            return null;
        }
    }
}
