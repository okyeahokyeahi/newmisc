using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DesktopBuddy.Magi;

public enum Cue { Deciding, Approve, Deny, Resolve, Alarm, Tick }

/// <summary>
/// MAGI theme sounds. Each cue uses your own file from %AppData%\DesktopBuddy\sounds if one exists
/// (deciding.wav / deciding.mp3 etc.), otherwise a built-in synthesized sound. Your files never go in
/// the app or the repo. Quiet files are levelled automatically. Silent during games or when muted.
/// </summary>
internal sealed class SoundBank : IDisposable
{
    public static string Folder { get; } = Path.Combine(Settings.Folder, "sounds");
    private static readonly string[] Extensions = [".wav", ".mp3"];
    private const int SampleRate = 44100;

    private readonly Settings _settings;
    private readonly Func<bool> _quiet; // e.g. a game is running
    private readonly List<WaveOutEvent> _playing = [];
    private readonly Dictionary<string, float> _gainCache = new(StringComparer.OrdinalIgnoreCase);

    public SoundBank(Settings settings, Func<bool> quiet)
    {
        _settings = settings;
        _quiet = quiet;
        WriteReadme();
    }

    private bool Enabled => _settings.MagiSounds && !_quiet();

    public void Play(Cue cue)
    {
        if (!Enabled) return;
        try
        {
            Start(Source(cue, loop: false));
        }
        catch (Exception ex)
        {
            Log.Error($"Playing {cue} failed", ex);
        }
    }

    /// <summary>Starts the deciding sound (looped if shorter than the vote). Returns stop(fadeMs).</summary>
    public Action<int> StartDeciding()
    {
        if (!Enabled) return _ => { };
        try
        {
            var fader = new FadeInOutSampleProvider(Source(Cue.Deciding, loop: true));
            WaveOutEvent output = Start(fader);
            bool stopped = false;
            return fadeMs =>
            {
                if (stopped) return;
                stopped = true;
                fader.BeginFadeOut(Math.Max(10, fadeMs));
                Task.Delay(fadeMs + 60).ContinueWith(_ => { try { output.Stop(); } catch { /* already stopped */ } });
            };
        }
        catch (Exception ex)
        {
            Log.Error("Playing the deciding sound failed", ex);
            return _ => { };
        }
    }

    /// <summary>Your file for this cue, if you added one.</summary>
    public static string? CustomFile(Cue cue)
    {
        string name = cue.ToString().ToLowerInvariant();
        return Extensions.Select(ext => Path.Combine(Folder, name + ext)).FirstOrDefault(File.Exists);
    }

    private ISampleProvider Source(Cue cue, bool loop)
    {
        if (CustomFile(cue) is string file)
        {
            var reader = new AudioFileReader(file);
            ISampleProvider p = new VolumeSampleProvider(reader) { Volume = LevelFor(file) };
            if (loop) p = new LoopingProvider(reader, p);
            return new TimeLimit(p, TimeSpan.FromSeconds(loop ? 30 : 10)); // never play a whole long file
        }
        float[] samples = Synth(cue);
        ISampleProvider synth = new ArrayProvider(samples, loop);
        return new TimeLimit(synth, TimeSpan.FromSeconds(30));
    }

    /// <summary>Gain that brings a quiet file's peak up to a comfortable level (cached per file version).</summary>
    private float LevelFor(string file)
    {
        string key = $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}";
        if (_gainCache.TryGetValue(key, out float cached)) return cached;
        float peak = 0;
        using (var reader = new AudioFileReader(file))
        {
            var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
            int total = 0, read, limit = buffer.Length * 10; // first ~10 s
            while (total < limit && (read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
                total += read;
            }
        }
        float gain = peak > 0.0001f ? Math.Clamp(0.7f / peak, 0.3f, 10f) : 1f;
        return _gainCache[key] = gain * (float)Math.Clamp(_settings.MagiVolume, 0, 1.5);
    }

    private WaveOutEvent Start(ISampleProvider provider)
    {
        var output = new WaveOutEvent();
        output.Init(provider);
        lock (_playing) _playing.Add(output);
        output.PlaybackStopped += (_, _) =>
        {
            lock (_playing) _playing.Remove(output);
            output.Dispose();
        };
        output.Play();
        return output;
    }

    // ---------- Built-in sounds ----------
    private float[] Synth(Cue cue)
    {
        var s = new List<float>();
        float vol = (float)Math.Clamp(_settings.MagiVolume, 0, 1.5);
        void Tone(double freq, double seconds, string wave = "square", double amp = 0.25, double? slideTo = null, double at = -1)
        {
            int start = at < 0 ? s.Count : (int)(at * SampleRate);
            int n = (int)(seconds * SampleRate);
            while (s.Count < start + n) s.Add(0);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / n;
                double f = slideTo is double end ? freq * Math.Pow(end / freq, t) : freq;
                phase += f / SampleRate;
                double x = phase % 1.0;
                double v = wave switch
                {
                    "sine" => Math.Sin(2 * Math.PI * x),
                    "saw" => 2 * x - 1,
                    "triangle" => 1 - 4 * Math.Abs(x - 0.5),
                    _ => x < 0.5 ? 1 : -1,
                };
                double env = Math.Min(1, i / (0.004 * SampleRate)) * Math.Exp(-4 * t); // quick attack, decay
                s[start + i] += (float)(v * env * amp * vol);
            }
        }

        switch (cue)
        {
            case Cue.Tick: Tone(1400, 0.03, amp: 0.12); break;
            case Cue.Approve: Tone(880, 0.12, "sine", 0.35); Tone(1320, 0.22, "sine", 0.35, at: 0.10); break;
            case Cue.Deny: Tone(160, 0.32, "saw", 0.25, slideTo: 110); break;
            case Cue.Resolve:
                Tone(523, 0.5, "triangle", 0.2, at: 0); Tone(659, 0.5, "triangle", 0.2, at: 0.05); Tone(784, 0.5, "triangle", 0.2, at: 0.10);
                break;
            case Cue.Alarm: for (int i = 0; i < 6; i++) Tone(i % 2 == 1 ? 720 : 960, 0.22, amp: 0.18, at: i * 0.24); break;
            case Cue.Deciding: for (int i = 0; i < 14; i++) Tone(900 + (i % 3) * 260, 0.035, amp: 0.1, at: i * 0.11); break;
        }
        return [.. s];
    }

    private void WriteReadme()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string readme = Path.Combine(Folder, "README.txt");
            if (File.Exists(readme)) return;
            File.WriteAllText(readme,
                "Desktop Buddy MAGI theme sounds\r\n" +
                "================================\r\n\r\n" +
                "Put your own .wav or .mp3 files here to replace the built-in sounds:\r\n\r\n" +
                "  deciding.wav  plays while the three cores decide (loops if short, fades out at the verdict)\r\n" +
                "  approve.wav   a core votes APPROVE\r\n" +
                "  deny.wav      a core votes DENY\r\n" +
                "  resolve.wav   the verdict\r\n" +
                "  alarm.wav     a security vote opens\r\n" +
                "  tick.wav      button clicks\r\n\r\n" +
                "Only the first 30 seconds of a file is used. Quiet files are levelled automatically.\r\n" +
                "Delete a file to go back to the built-in sound.\r\n");
        }
        catch (Exception ex)
        {
            Log.Error("Creating the sounds folder failed", ex);
        }
    }

    public void Dispose()
    {
        lock (_playing)
        {
            foreach (var o in _playing.ToList())
            {
                try { o.Stop(); o.Dispose(); } catch { /* exiting */ }
            }
            _playing.Clear();
        }
    }

    // ---------- small sample providers ----------
    private sealed class ArrayProvider(float[] samples, bool loop) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int written = 0;
            while (written < count && samples.Length > 0)
            {
                if (_pos >= samples.Length)
                {
                    if (!loop) break;
                    _pos = 0;
                }
                int n = Math.Min(count - written, samples.Length - _pos);
                Array.Copy(samples, _pos, buffer, offset + written, n);
                _pos += n;
                written += n;
            }
            return written;
        }
    }

    /// <summary>Rewinds the file when it ends, so a short deciding sound lasts the whole vote.</summary>
    private sealed class LoopingProvider(AudioFileReader reader, ISampleProvider inner) : ISampleProvider
    {
        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int written = 0;
            while (written < count)
            {
                int n = inner.Read(buffer, offset + written, count - written);
                if (n == 0)
                {
                    if (reader.Length == 0) break;
                    reader.Position = 0;
                    continue;
                }
                written += n;
            }
            return written;
        }
    }

    /// <summary>Stops after a maximum duration.</summary>
    private sealed class TimeLimit(ISampleProvider inner, TimeSpan max) : ISampleProvider
    {
        private long _left = (long)(max.TotalSeconds * inner.WaveFormat.SampleRate * inner.WaveFormat.Channels);
        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            if (_left <= 0) return 0;
            int n = inner.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= n;
            return n;
        }
    }
}
