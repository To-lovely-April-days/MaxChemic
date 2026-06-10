using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SherpaOnnx;

namespace MaxChemical.Shell.Services
{
    public class SherpaOnnxTTS : IDisposable
    {
        private OfflineTts _tts;
        private bool _disposed = false;

        public SherpaOnnxTTS(string modelPath)
        {
            var modelFile = Path.Combine(modelPath, "model.onnx");

            var tokensFile = File.Exists(Path.Combine(modelPath, "tokens.txt"))
                ? Path.Combine(modelPath, "tokens.txt")
                : Path.Combine(modelPath, "tokens");

            var lexiconFile = File.Exists(Path.Combine(modelPath, "lexicon.txt"))
                ? Path.Combine(modelPath, "lexicon.txt")
                : Path.Combine(modelPath, "lexicon");

            Console.WriteLine("=== 文件检查 ===");
            Console.WriteLine($"模型文件: {File.Exists(modelFile)}");
            Console.WriteLine($"Tokens文件: {File.Exists(tokensFile)}");
            Console.WriteLine($"Lexicon文件: {File.Exists(lexiconFile)}\n");

            if (!File.Exists(modelFile) || !File.Exists(tokensFile) || !File.Exists(lexiconFile))
                throw new FileNotFoundException("缺少必要的模型文件");

            var config = new OfflineTtsConfig();
            var vitsConfig = new OfflineTtsVitsModelConfig
            {
                Model = modelFile,
                Tokens = tokensFile,
                Lexicon = lexiconFile,
                DataDir = ""
            };

            var modelConfig = new OfflineTtsModelConfig
            {
                Vits = vitsConfig,
                NumThreads = 1,
                Debug = 0,
                Provider = "cpu"
            };

            config.Model = modelConfig;
            config.MaxNumSentences = 1;

            Console.WriteLine("正在初始化TTS引擎...");
            _tts = new OfflineTts(config);
            Console.WriteLine("✓ TTS引擎初始化成功！\n");
        }

        // 方法1: 返回原始音频数据
        public (float[] samples, int sampleRate) GenerateRawAudio(string text, int speakerId = 0, int speed = 100)
        {
            Console.WriteLine($"正在生成: {text}");
            var audio = _tts.Generate(text, speakerId, speed);

            if (audio?.Samples == null || audio.Samples.Length == 0)
                throw new Exception("生成的音频为空");

            Console.WriteLine($"✓ 生成成功 ({audio.Samples.Length} 样本, {audio.SampleRate} Hz)");
            return (audio.Samples, audio.SampleRate);
        }

        // 方法2: 返回音频数据（用于播放）
        public (float[] samples, int sampleRate) GenerateAudio(string text, int speakerId = 0, int speed = 100)
        {
            return GenerateRawAudio(text, speakerId, speed);
        }

        // 方法3: 返回WAV格式的字节流
        public byte[] GenerateAudioBytes(string text, int speakerId = 0, int speed = 100)
        {
            var (samples, sampleRate) = GenerateRawAudio(text, speakerId, speed);

            using var ms = new MemoryStream();
            WriteWavToStream(ms, samples, sampleRate);
            return ms.ToArray();
        }

        // 方法4: 生成到文件（保留原功能）
        public void GenerateToFile(string text, string outputWavFile, int speakerId = 0, int speed = 100)
        {
            var audioBytes = GenerateAudioBytes(text, speakerId, speed);
            File.WriteAllBytes(outputWavFile, audioBytes);
            Console.WriteLine($"✓ 已保存到: {outputWavFile}");
        }

        // 方法5: 生成到流
        public void GenerateToStream(string text, Stream outputStream, int speakerId = 0, int speed = 100)
        {
            var (samples, sampleRate) = GenerateRawAudio(text, speakerId, speed);
            WriteWavToStream(outputStream, samples, sampleRate);
        }

        private void WriteWavToStream(Stream stream, float[] samples, int sampleRate)
        {
            short[] pcm = new short[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                float sample = Math.Max(-1.0f, Math.Min(1.0f, samples[i]));
                pcm[i] = (short)(sample * 32767);
            }

            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);

            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + pcm.Length * 2);
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });

            writer.Write(new char[] { 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);

            writer.Write(new char[] { 'd', 'a', 't', 'a' });
            writer.Write(pcm.Length * 2);

            foreach (var sample in pcm)
            {
                writer.Write(sample);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _tts?.Dispose();
                }
                _disposed = true;
            }
        }

        ~SherpaOnnxTTS()
        {
            Dispose(false);
        }
    }
}
