using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Lemur.Audio;
using Lemur.Backends;
using Lemur.Config;
using Lemur.Detection;
using Lemur.Logging;
using Lemur.Pipeline;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.Cli;

/// <summary>
/// llmmr — the recorder's engine without the tray: setup (secrets), diagnosis (devices, detect,
/// selftest) and pipeline testing (record, make-test-recording, process).
/// </summary>
static partial class Program
{
    const string Usage = """
        llmmr — Lemur command line

          llmmr settings                        show the settings path and anything missing
          llmmr set-secret <name>               store a secret read from stdin (never echoed)
          llmmr secret-status <name>            say whether a secret is stored
          llmmr devices                         list audio devices and the Windows defaults
          llmmr detect [--seconds N]            show what call detection sees, once a second
          llmmr selftest [--mic name]           play 3 soft beeps while recording; find them in both tracks
          llmmr record --seconds N [--title T] [--play file.wav] [--volume 0.5] [--process]  (WAV only)
          llmmr make-test-recording --mic a.wav --others b.wav[,c.wav...] [--seconds N] [--title T]
          llmmr import <file> [--title T] [--started "2026-10-14 09:30"] [--again]
                                                make a meeting from a recording made elsewhere (a phone's voice memo, a
                                                recorder, a video); the tray makes its notes. Prints the recording's folder.
                                                --again: import a file that has been imported before
          llmmr process <folder> [--backend scriberr|openai] [--from recorded|mixed|transcribed|identified|summarized] [--notes <folder>]
                                                --notes: write the note there instead of the notes folder (for tests)
          llmmr rename <recording> "<title>"    retitle a meeting; its note's files are renamed to match
          llmmr keep <recording> [--off]        keep a meeting's audio and its note's MP3 however old it gets (the Recordings
                                                window's Keep recording); --off leaves them to the clean-up again
          llmmr describe <recording> [--show] [--apply]
                                                ask the summary model for a title and one-line description from the summary;
                                                --show prints them (meeting content), --apply keeps them (and titles a
                                                meeting that had none, unless its note was edited)
          llmmr remake <recording>...           make finished meetings' notes again with this Lemur (transcript fetched again
                                                from Scriberr, labels, names, summary, note), keeping titles, the names you
                                                gave and the note's place; only untouched notes; a backup goes beside them;
                                                safe while the tray runs (prints counts only)
          llmmr export <recording> [--out <folder>] [--transcript | --no-transcript]
                                                write the meeting file (JSON: the meeting's facts, action items and
                                                transcript) beside its note, or into another folder; prints its path.
                                                The transcript is in it as Settings (Notes, Meeting file) says; with
                                                --out, --transcript or --no-transcript decides instead

        Vocabulary (words the transcription gets wrong):
          llmmr vocab list                      the words, and what each is misheard as
          llmmr vocab add "<right spelling>" ["<misheard as>" ...]
          llmmr vocab remove "<right spelling>"
          llmmr vocab fix <recording> | --all   fix the saved words in meetings already made (prints counts only)

        Speaker names (docs/2026-10-13-speaker-names-design.md):
          llmmr identify <recording> [--people <folder>]  re-run voice + introduction naming, print what it found (notes unchanged)
          llmmr people list                     everyone you have named, with their voiceprints
          llmmr people name <recording> "<label>" "<name>" [--remember] [--consent "<note>"] [--not-a-person]
          llmmr people rename <who> "<new name>"
          llmmr people merge <who> <into>
          llmmr people delete <who> --yes
          llmmr people export <file.zip>  |  llmmr people import <file.zip>
          llmmr voices calibrate                show which model the voiceprint service is running (fingerprint)
          llmmr voices benchmark --ami <audio dir> --annotations <ami_public_manual zip> [--series ES2002,...] [--opus]
        """;

    static async Task<int> Main(string[] args)
    {
        // Before 1.0 Lemur was Teams Recorder: move its folders and keys first, as the tray app does.
        List<string> upgraded = [];
        if (LegacyMigration.Needed)
        {
            if (LegacyMigration.LegacyAppRunning)
                return Fail("Teams Recorder (Lemur before 1.0) is still running: quit it from its tray icon first, so its settings, " +
                            "recordings and keys can move to Lemur.");
            upgraded = LegacyMigration.Run();
        }
        Log.Init(AppPaths.LogFolder);
        foreach (var line in upgraded) Log.Info($"upgrade from Teams Recorder: {line}");
        Log.Echo = line => Console.Error.WriteLine(line);
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            var settings = SettingsStore.LoadOrCreate();
            return args[0] switch
            {
                "settings" => ShowSettings(settings),
                "set-secret" => SetSecret(Arg(args, 1)),
                "secret-status" => SecretStatus(Arg(args, 1)),
                "devices" => Devices(),
                "detect" => Detect(settings, Opt(args, "--seconds") is { } s ? double.Parse(s) : 60),
                "selftest" => SelfTest(settings, Opt(args, "--mic")),
                "beeptest" => BeepTest(),
                "record" => await Record(settings, args),
                "make-test-recording" => MakeTestRecording(settings, args),
                "import" => Import(settings, Arg(args, 1), Opt(args, "--title"), Opt(args, "--started"), args.Contains("--again")),
                "process" => await Process(settings, Arg(args, 1), Opt(args, "--backend"), Opt(args, "--from"), Opt(args, "--notes")),
                "rename" => Rename(settings, Arg(args, 1), Arg(args, 2)),
                "keep" => Keep(settings, Arg(args, 1), args.Contains("--off")),
                "describe" => await Describe(settings, Arg(args, 1), args.Contains("--show"), args.Contains("--apply")),
                "vocab" => Vocab(settings, args),
                "identify" => await Identify(settings, Arg(args, 1), Opt(args, "--people")),
                "remake" => await Remake(settings, args),
                "export" => Export(settings, Arg(args, 1), Opt(args, "--out"),
                    args.Contains("--no-transcript") ? false : args.Contains("--transcript") || settings.MeetingFile.IncludeTranscript),
                "people" => await People(settings, args),
                "voices" => await Voices(settings, args),
                _ => Fail($"unknown command '{args[0]}'\n\n{Usage}"),
            };
        }
        catch (Exception ex) when (ex is BackendException or ArgumentException or InvalidOperationException or IOException or FormatException)
        {
            return Fail(ex.Message);
        }
    }

    static int ShowSettings(AppSettings settings)
    {
        Console.WriteLine($"settings:   {AppPaths.SettingsFile}");
        Console.WriteLine($"recordings: {settings.ResolvedRecordingsFolder}");
        Console.WriteLine($"notes:      {settings.NotesFolder}");
        Console.WriteLine($"logs:       {AppPaths.LogFolder}");
        Console.WriteLine($"backend:    {settings.Backend.Type}");
        var problems = settings.Problems();
        Console.WriteLine(problems.Count == 0 ? "status:     ready" : "problems:\n  - " + string.Join("\n  - ", problems));
        return problems.Count == 0 ? 0 : 1;
    }

    static int SetSecret(string name)
    {
        // Windows PowerShell can put a byte-order mark in front of what it pipes in; a key with one can't be sent.
        var value = Console.In.ReadLine()?.Replace("﻿", "").Trim();
        if (string.IsNullOrEmpty(value)) return Fail("no value on stdin");
        if (value.Any(c => c < 0x21 || c > 0x7E))
            return Fail($"'{name}' was not stored: keys are sent in web requests, which allow only plain ASCII letters, digits and symbols (no spaces)");
        new WindowsCredentialStore().Set(name, value);
        Console.WriteLine($"stored secret '{name}' ({value.Length} characters)");
        return 0;
    }

    static int SecretStatus(string name)
    {
        bool present = new WindowsCredentialStore().Get(name) is not null;
        Console.WriteLine($"{name}: {(present ? "stored" : "missing")}");
        return present ? 0 : 1;
    }

    static int Devices()
    {
        using var en = new MMDeviceEnumerator();
        foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
        {
            string? defaultId = null;
            try { defaultId = en.GetDefaultAudioEndpoint(flow, Role.Console).ID; } catch { /* none */ }
            Console.WriteLine(flow == DataFlow.Capture ? "Microphones:" : "Outputs (recorded via loopback):");
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                var f = d.AudioClient.MixFormat;
                Console.WriteLine($"  {(d.ID == defaultId ? "*" : " ")} {d.FriendlyName}  [{f.SampleRate} Hz, {f.Channels} ch]");
            }
        }
        Console.WriteLine("  * = Windows default");
        return 0;
    }

    /// <summary>Prints only watched apps and windows a rule could match — never every window title.</summary>
    static int Detect(AppSettings settings, double seconds)
    {
        var mic = new RegistryMicUsageSource();
        var windows = new Win32WindowSource();
        var sessions = new WasapiSessionSource();
        var detector = new CallDetector(settings.Detection);
        var windowRules = settings.Detection.Sources.Where(r => r.Enabled && r.NeedsWindow).ToList();
        var sw = Stopwatch.StartNew();
        string last = "";
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var m = mic.Read();
            var w = windows.Read();
            var inUse = m.Where(x => x.InUse).Select(x => x.AppKey.Split('#').Last()).ToList();
            // Windows a rule recognises; and while a browser holds the mic, all its windows, to show what an unknown app calls itself.
            var micBrowsers = windowRules.Where(r => r.WindowProcess is not null && m.Any(x => x.InUse && x.AppKey.Contains(r.AppKeyContains, StringComparison.OrdinalIgnoreCase)))
                .Select(r => r.WindowProcess!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var relevant = w.Where(x => windowRules.Any(r => CallDetector.IsWindowOf(r, x)) || micBrowsers.Contains(x.ProcessName))
                .Select(x => $"{x.ProcessName}: {x.Title}").Distinct().ToList();
            var capturing = sessions.ActiveCaptureProcesses();
            var ev = detector.Tick(DateTimeOffset.Now, m, w, capturing);
            var rule = settings.Detection.Sources.FirstOrDefault(r => r.Name == detector.ActiveSource);
            var pick = rule is null ? null : CallDevicePicker.Pick(rule, sessions.ActiveSessions(), sessions.Endpoints());
            var line = $"mic in use by [{string.Join(", ", inUse)}] · capturing [{(capturing is null ? "?" : string.Join(", ", capturing.Order()))}] · phase {detector.Phase}" +
                       (pick is null ? "" : $"\n    would record: mic='{pick.MicName ?? "default"}' output='{pick.OutputName ?? "default"}' ({pick.Reason})") +
                       (detector.ActiveSource is null ? "" : $" ({detector.ActiveSource})") +
                       (relevant.Count == 0 ? "" : "\n    windows: " + string.Join("\n             ", relevant));
            if (line != last || ev is not null)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
                last = line;
            }
            if (ev is not null) Console.WriteLine($"{DateTime.Now:HH:mm:ss} >>> {ev}");
            Thread.Sleep(1000);
        }
        return 0;
    }

    /// <summary>
    /// Plays three quiet 0.25 s beeps (at 1.0 s, 4.5 s after 3 s of silence, and 6.5 s) while recording,
    /// then finds them in the "everyone else" track. Same offset for all three = loopback capture and
    /// alignment hold up across silence.
    /// </summary>
    static int SelfTest(AppSettings settings, string? micOverride)
    {
        var temp = Path.Combine(Path.GetTempPath(), "llmmr-selftest-" + DateTime.Now.ToString("HHmmss"));
        var testSettings = Clone(settings);
        testSettings.RecordingsFolder = temp;
        if (micOverride is not null) testSettings.Audio.MicDeviceContains = micOverride;

        using var en = new MMDeviceEnumerator();
        var output = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        int rate = output.AudioClient.MixFormat.SampleRate, channels = output.AudioClient.MixFormat.Channels;
        double[] beeps = [1.0, 4.5, 6.5];
        const double beepLength = 0.25, total = 8.0;
        var pcm = new float[(int)(total * rate) * channels];
        foreach (var at in beeps)
        {
            int start = (int)(at * rate), n = (int)(beepLength * rate);
            for (int i = 0; i < n; i++)
            {
                float fade = Math.Min(1f, Math.Min(i, n - i) / (0.01f * rate));
                float v = 0.05f * fade * MathF.Sin(2 * MathF.PI * 1000 * i / rate);
                for (int c = 0; c < channels; c++) pcm[(start + i) * channels + c] = v;
            }
        }

        Console.WriteLine($"output: {output.FriendlyName} ({rate} Hz, {channels} ch) — you will hear 3 soft beeps");
        var session = RecordingSession.Start(testSettings, "selftest", "self-test", manual: true);
        Thread.Sleep(300);
        using (var player = new WasapiOut(output, AudioClientShareMode.Shared, true, 50))
        {
            player.Init(new ArraySampleProvider(pcm, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels)).ToWaveProvider());
            var playStart = session.Elapsed;
            player.Play();
            Thread.Sleep((int)(total * 1000) + 700);
            var meta = session.Stop();
            Console.WriteLine($"recorded {meta.DurationSeconds:0.0}s: mic='{meta.MicDevice}' output='{meta.OutputDevice}', " +
                              $"padded mic {meta.MicPaddedSeconds:0.000}s / others {meta.OthersPaddedSeconds:0.000}s");

            // Matched filter: correlate each track with the beep itself. Finds the beeps under music or
            // speech, and despite Windows ducking other audio while a microphone is open.
            var template = Beep16k(beepLength);
            var othersHits = FindBeeps(LoadMono16k(session.Folder.OthersWav), template, beeps.Length);
            var micHits = FindBeeps(LoadMono16k(session.Folder.MicWav), template, beeps.Length);
            var micEnv = Mixer.MeasureEnvelope(session.Folder.MicWav);
            Console.WriteLine($"mic track: loudest {micEnv.Percentile(99):0} dBFS{(micEnv.Percentile(99) < -85 ? "  ← digital silence: is the microphone muted?" : "")}");
            bool ok = true;
            var offsets = new List<double>();
            for (int i = 0; i < beeps.Length; i++)
            {
                double expected = playStart.TotalSeconds + beeps[i];
                var hit = othersHits.OrderBy(h => Math.Abs(h.Time - expected)).FirstOrDefault();
                bool found = hit.Score > 0.2 && Math.Abs(hit.Time - expected) < 1.0;
                ok &= found;
                if (found) offsets.Add(hit.Time - expected);
                var micHit = micHits.OrderBy(h => Math.Abs(h.Time - expected)).FirstOrDefault();
                Console.WriteLine($"  beep {i + 1} @ ~{expected:0.000}s  others: {(found ? $"found, offset {hit.Time - expected:+0.000;-0.000}s (match {hit.Score:0.00})" : "NOT FOUND")}" +
                                  $"  mic: {(micHit.Score > 0.2 && Math.Abs(micHit.Time - expected) < 1.0 ? $"heard, offset {micHit.Time - expected:+0.000;-0.000}s" : "not heard")}");
            }
            if (ok)
            {
                double spread = offsets.Max() - offsets.Min();
                Console.WriteLine($"offset spread across the 3 s silent gap: {spread * 1000:0} ms (≤ 150 ms passes)");
                ok = spread <= 0.15;
            }
            Console.WriteLine(ok ? "SELFTEST PASS" : "SELFTEST FAIL");
            Console.WriteLine($"files: {session.Folder.Path}");
            return ok ? 0 : 1;
        }
    }


    static float[] LoadMono16k(string path)
    {
        using var reader = new WaveFileReader(path);
        var p = reader.ToSampleProvider();
        var all = new List<float>();
        var buffer = new float[16000];
        int n;
        while ((n = p.Read(buffer, 0, buffer.Length)) > 0) all.AddRange(buffer.AsSpan(0, n).ToArray());
        return all.ToArray();
    }

    static float[] Beep16k(double seconds)
    {
        int rate = AnchoredTrackWriter.SampleRate, n = (int)(seconds * rate);
        var t = new float[n];
        for (int i = 0; i < n; i++)
        {
            float fade = Math.Min(1f, Math.Min(i, n - i) / (0.01f * rate));
            t[i] = fade * MathF.Sin(2 * MathF.PI * 1000 * i / rate);
        }
        return t;
    }

    /// <summary>Normalized cross-correlation peaks (score 0..1), at least 1 s apart, best first.</summary>
    static List<(double Time, double Score)> FindBeeps(float[] signal, float[] template, int count)
    {
        int m = template.Length, step = 8;
        double tEnergy = template.Sum(x => (double)x * x);
        var scores = new List<(int Pos, double Score)>();
        for (int pos = 0; pos + m <= signal.Length; pos += step)
        {
            double dot = 0, energy = 0;
            for (int k = 0; k < m; k++)
            {
                dot += signal[pos + k] * template[k];
                energy += signal[pos + k] * signal[pos + k];
            }
            scores.Add((pos, energy <= 1e-12 ? 0 : Math.Abs(dot) / Math.Sqrt(energy * tEnergy)));
        }
        var picked = new List<(double Time, double Score)>();
        foreach (var c in scores.OrderByDescending(x => x.Score))
        {
            double time = c.Pos / (double)AnchoredTrackWriter.SampleRate;
            if (picked.Any(p => Math.Abs(p.Time - time) < 1.0)) continue;
            picked.Add((time, c.Score));
            if (picked.Count == count + 2) break;
        }
        return picked;
    }


    /// <summary>Diagnostic: play the beeps through the default output while capturing ONLY its loopback.</summary>
    static int BeepTest()
    {
        using var en = new MMDeviceEnumerator();
        var output = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        var mix = output.AudioClient.MixFormat;
        Console.WriteLine($"output {output.FriendlyName}: mix format {mix.Encoding} {mix.SampleRate} Hz {mix.Channels} ch {mix.BitsPerSample} bit; state {output.State}; muted {output.AudioEndpointVolume.Mute}, master {output.AudioEndpointVolume.MasterVolumeLevelScalar:0.00}");
        int rate = mix.SampleRate, ch = mix.Channels;
        var pcm = new float[rate * 3 * ch];
        for (int i = 0; i < rate / 2; i++) { float v = 0.05f * MathF.Sin(2 * MathF.PI * 1000 * i / rate); for (int c = 0; c < ch; c++) pcm[(rate + i) * ch + c] = v; }
        var path = Path.Combine(Path.GetTempPath(), "llmmr-beeptest-loopback.wav");
        using (var capture = new WasapiLoopbackCapture(output))
        using (var writer = new WaveFileWriter(path, capture.WaveFormat))
        {
            capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);
            capture.StartRecording();
            using var player = new WasapiOut(output, AudioClientShareMode.Shared, true, 50);
            Exception? stopped = null;
            player.PlaybackStopped += (_, e) => stopped = e.Exception;
            player.Init(new ArraySampleProvider(pcm, WaveFormat.CreateIeeeFloatWaveFormat(rate, ch)).ToWaveProvider());
            player.Play();
            Thread.Sleep(3500);
            Console.WriteLine($"player state {player.PlaybackState}; stopped-with-error: {(stopped is null ? "none" : stopped.Message)}");
            capture.StopRecording();
            Thread.Sleep(300);
        }
        using var reader = new WaveFileReader(path);
        var sp = reader.ToSampleProvider();
        var buf = new float[reader.WaveFormat.SampleRate / 10 * reader.WaveFormat.Channels];
        int frame = 0, n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0)
        {
            double sum = 0; for (int i = 0; i < n; i++) sum += buf[i] * buf[i];
            Console.Write($"{Envelope.ToDb(sum / Math.Max(1, n)):0} ");
            frame++;
        }
        Console.WriteLine();
        Console.WriteLine($"(loopback dB per 100 ms; beep plays 1.0-1.5 s) {path}");
        return 0;
    }

    static List<double> FindOnsets(Envelope env, double thresholdDb)
    {
        var onsets = new List<double>();
        bool above = false;
        for (int i = 0; i < env.Db.Length; i++)
        {
            bool now = env.Db[i] > thresholdDb;
            if (now && !above) onsets.Add(i * env.FrameSeconds);
            above = now;
        }
        return onsets;
    }

    static async Task<int> Record(AppSettings settings, string[] args)
    {
        double seconds = double.Parse(Opt(args, "--seconds") ?? throw new ArgumentException("--seconds is required"));
        var play = Opt(args, "--play");
        float volume = float.Parse(Opt(args, "--volume") ?? "1.0");
        var session = RecordingSession.Start(settings, "cli", Opt(args, "--title") ?? "", manual: true);
        Console.WriteLine($"recording {seconds:0}s into {session.Folder.Path}");
        WasapiOut? player = null;
        WaveFileReader? reader = null;
        try
        {
            if (play is not null)
            {
                reader = new WaveFileReader(play);
                player = new WasapiOut(AudioClientShareMode.Shared, 100);
                player.Init(new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = volume }.ToWaveProvider());
                player.Play();
            }
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }
        finally
        {
            player?.Stop();
            player?.Dispose();
            reader?.Dispose();
        }
        var meta = session.Stop();
        Console.WriteLine($"stopped: {meta.DurationSeconds:0.0}s, mic='{meta.MicDevice}', output='{meta.OutputDevice}', restarts {meta.DeviceRestarts}");
        return args.Contains("--process") ? await Process(settings, session.Folder.Path, null, null) : 0;
    }

    /// <summary>
    /// Builds a recording folder from existing audio — e.g. one AMI headset channel as "mic" and the
    /// others summed as "others" — so the pipeline can be tested without playing anything out loud.
    /// </summary>
    static int MakeTestRecording(AppSettings settings, string[] args)
    {
        var micPath = Opt(args, "--mic") ?? throw new ArgumentException("--mic is required");
        var otherPaths = (Opt(args, "--others") ?? throw new ArgumentException("--others is required")).Split(',');
        double? seconds = Opt(args, "--seconds") is { } s ? double.Parse(s) : null;
        var mic = Load16kMono(micPath, seconds);
        var others = new float[mic.Length];
        foreach (var p in otherPaths)
        {
            var o = Load16kMono(p, seconds);
            for (int i = 0; i < Math.Min(o.Length, others.Length); i++) others[i] += o[i];
        }
        var started = DateTimeOffset.Now;
        var rec = RecordingFolder.CreateNew(settings.ResolvedRecordingsFolder, started);
        using (var sink = new WavSampleSink(rec.MicWav)) sink.Write(mic);
        using (var sink = new WavSampleSink(rec.OthersWav)) sink.Write(others);
        double duration = mic.Length / (double)AnchoredTrackWriter.SampleRate;
        rec.SaveMeta(new RecordingMeta
        {
            Title = Opt(args, "--title") ?? "Test recording",
            Source = "test",
            Manual = true,
            StartedAt = started,
            EndedAt = started.AddSeconds(duration),
            DurationSeconds = Math.Round(duration, 1),
        });
        rec.SaveState(new ProcessingState());
        Console.WriteLine(rec.Path);
        return 0;
    }

    /// <summary>A recording made elsewhere, as a meeting waiting for notes (the Recordings window's Import…).</summary>
    static int Import(AppSettings settings, string file, string? title, string? started, bool again)
    {
        if (!File.Exists(file)) throw new FileNotFoundException($"no such file: {file}");
        DateTimeOffset? at = started is null ? null
            : DateTimeOffset.Parse(started, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.AssumeLocal);
        var root = settings.ResolvedRecordingsFolder;
        if (RecordingImport.FindImported(root, RecordingImport.Sha256Of(file)) is { } earlier && !again)
            return Fail($"this file was imported already, as {earlier.Path}; --again imports it once more");
        try
        {
            var rec = RecordingImport.Import(root, file, title, at);
            var meta = rec.LoadMeta();
            Console.WriteLine(rec.Path);
            Console.Error.WriteLine($"{meta.DurationSeconds:0} s, started {meta.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} " +
                                    $"(from {meta.Imported?.StartedFrom.ToString().ToLowerInvariant()}); waiting for notes");
            return 0;
        }
        catch (ImportException ex)
        {
            return Fail($"it can't be imported: {ex.Message}");
        }
    }

    static float[] Load16kMono(string path, double? seconds)
    {
        using var reader = new WaveFileReader(path);
        ISampleProvider p = reader.ToSampleProvider();
        if (p.WaveFormat.Channels > 1) p = new DownmixToMono(p);
        if (p.WaveFormat.SampleRate != AnchoredTrackWriter.SampleRate) p = new WdlResamplingSampleProvider(p, AnchoredTrackWriter.SampleRate);
        var all = new List<float>();
        var buffer = new float[AnchoredTrackWriter.SampleRate];
        long limit = seconds is { } s ? (long)(s * AnchoredTrackWriter.SampleRate) : long.MaxValue;
        int n;
        while (all.Count < limit && (n = p.Read(buffer, 0, buffer.Length)) > 0)
            all.AddRange(buffer.AsSpan(0, (int)Math.Min(n, limit - all.Count)).ToArray());
        return all.ToArray();
    }

    static async Task<int> Process(AppSettings settings, string folder, string? backend, string? from, string? notes = null)
    {
        if (!Directory.Exists(folder)) throw new FileNotFoundException($"no such recording folder: {folder}");
        if (backend is not null) settings.Backend.Type = backend;
        if (notes is not null) settings.NotesFolder = Path.GetFullPath(notes);
        var rec = new RecordingFolder(folder);
        if (from is not null)
        {
            var state = rec.LoadState();
            state.Stage = Enum.Parse<Stage>(from, ignoreCase: true);
            state.NextAttemptAt = null;
            if (state.Stage <= Stage.Mixed) state.JobId = null; // transcribe afresh, not resume the last job
            rec.SaveState(state);
        }
        var secrets = new WindowsCredentialStore();
        var processor = new MeetingProcessor(settings, () => BackendFactory.Create(settings, secrets),
            SpeakerIdentifier.FromSettings(settings, secrets), () => ChatModel.Create(settings, secrets, out _));
        var sw = Stopwatch.StartNew();
        var result = await processor.ProcessAsync(rec, CancellationToken.None);
        Console.WriteLine($"stage {result.Stage} after {sw.Elapsed.TotalSeconds:0}s");
        if (result.WebUrl is not null) Console.WriteLine($"backend: {result.WebUrl}");
        if (result.NotePath is not null) Console.WriteLine($"note: {result.NotePath}\npage: {NoteWriter.HtmlPath(result.NotePath)}");
        if (result.NotePath is not null && File.Exists(NoteWriter.AudioPath(result.NotePath))) Console.WriteLine($"audio: {NoteWriter.AudioPath(result.NotePath)}");
        if (result.LastError is not null && result.Stage != Stage.Done) Console.WriteLine($"error: {result.LastError}");
        return result.Stage == Stage.Done ? 0 : 1;
    }

    static int Rename(AppSettings settings, string folderOrName, string title)
    {
        var rec = Recording(settings, folderOrName);
        var note = MeetingRename.Apply(rec, title);
        MeetingFile.TryWrite(rec, settings.DisplayName, new Vocabulary(settings.Vocabulary), transcript: settings.MeetingFile.IncludeTranscript); // moved with the note; now it says the new title too
        Console.WriteLine($"renamed to \"{rec.LoadMeta().Title}\"");
        if (note is not null) Console.WriteLine($"note: {note}");
        return 0;
    }

    /// <summary>
    /// Keep recording, as the Recordings window ticks it: the clean-up after keepAudioDays leaves the meeting's audio and its
    /// note's MP3 alone. Safe while the tray runs (meta.json is changed under the meeting's lock); the tray's next clean-up
    /// takes what an unkept meeting past the days still has.
    /// </summary>
    static int Keep(AppSettings settings, string folderOrName, bool off)
    {
        var rec = Recording(settings, folderOrName);
        var state = rec.LoadState();
        bool audio = !state.AudioDeleted;
        bool mp3 = state.NotePath is { } note && File.Exists(NoteWriter.AudioPath(note));
        if (!off && !audio && !mp3) return Fail("there's nothing left to keep: its audio has been deleted, and there's no MP3 beside its note");
        rec.UpdateMeta(m => m.KeepRecording = !off);
        Log.Info($"{rec.Name}: {(off ? "no longer kept" : "kept")} (llmmr keep)");
        if (!off)
        {
            Console.WriteLine(!audio ? "kept: its working audio was deleted already, so its note's MP3 is what stays"
                : mp3 || state.Stage != Stage.Done ? "kept: its audio and its note's MP3 stay, however old it gets"
                : "kept: its audio stays, however old it gets");
            return 0;
        }
        // What the clean-up takes of it: the working audio, and the MP3 (there now, or once its notes are made) only when
        // Settings deletes MP3s and the clean-up hasn't dealt with this one's already.
        int days = settings.KeepAudioDays;
        var span = days == 1 ? "1 day" : $"{days} days";
        bool mp3Goes = settings.NoteMp3.DeleteWithAudio && !state.Mp3Deleted && (mp3 || state.Stage != Stage.Done);
        var what = audio && mp3Goes ? "its audio and its note's MP3" : audio ? "its audio" : mp3Goes ? "its note's MP3" : null;
        Console.WriteLine(what is null || days < 0 ? "no longer kept"
            : state.Stage == Stage.Done && AudioCleanUp.PastDays(state.CompletedAt, days, DateTimeOffset.Now)
                ? $"no longer kept: it's more than {span} old, so the tray's next clean-up deletes {what}"
                : $"no longer kept: {what} will be deleted {span} after its notes are made");
        return 0;
    }

    /// <summary>What the summary model makes of a meeting. Its answer is meeting content, so it is printed only with --show.</summary>
    static async Task<int> Describe(AppSettings settings, string folderOrName, bool show, bool apply)
    {
        var rec = Recording(settings, folderOrName);
        if (!File.Exists(rec.SummaryFile)) return Fail("this meeting has no summary yet");
        using var chat = ChatModel.Create(settings, new WindowsCredentialStore(), out var whyNot);
        if (chat is null) return Fail($"no summary model: {whyNot}");
        var sw = Stopwatch.StartNew();
        var (title, description) = await MeetingDescriber.AskAsync(chat, File.ReadAllText(rec.SummaryFile), CancellationToken.None);
        Console.WriteLine($"asked in {sw.Elapsed.TotalSeconds:0}s");
        Console.WriteLine($"title: {(title is null ? "(none)" : show ? title : $"{title.Split(' ').Length} words (--show prints it)")}");
        Console.WriteLine($"about: {(description is null ? "(none)" : show ? description : $"{description.Length} characters (--show prints it)")}");
        if (!apply) return title is null && description is null ? 1 : 0;

        using (RecordingFolder.Lock())
        {
            var state = rec.LoadState();
            bool untouched = state.NotePath is null || !File.Exists(state.NotePath) ||
                             (state.NoteHash is not null && NoteWriter.Hash(state.NotePath) == state.NoteHash);
            bool retitle = title is not null && MeetingAbout.NeedsTitle(rec.LoadMeta()) && untouched;
            var note = retitle ? MeetingRename.Apply(rec, title!, byUser: false) : null;
            rec.UpdateMeta(m =>
            {
                if (description is not null) m.Description = description;
                if (title is not null) m.SuggestedTitle = title;
            });
            Console.WriteLine(retitle ? $"titled; note: {note ?? "(none yet)"}" : "kept; the meeting's title is unchanged");
        }
        MeetingFile.TryWrite(rec, settings.DisplayName, new Vocabulary(settings.Vocabulary), transcript: settings.MeetingFile.IncludeTranscript);
        return 0;
    }

    /// <summary>The meeting file (docs/2026-10-15-meeting-export-design.md §3.1), beside the note or in another folder.</summary>
    static int Export(AppSettings settings, string folderOrName, string? outFolder, bool transcript)
    {
        var rec = Recording(settings, folderOrName);
        if (rec.LoadState() is not { Stage: Stage.Done, NotePath: { } note }) return Fail("its notes aren't made yet");
        if (outFolder is null && !File.Exists(note))
            return Fail("its note isn't there any more (deleted or moved), so the file has nothing to go beside; --out <folder> writes it elsewhere");
        if (outFolder is not null) Directory.CreateDirectory(outFolder = Path.GetFullPath(outFolder));
        if (outFolder is null && transcript != settings.MeetingFile.IncludeTranscript)
            return Fail($"the file beside the note follows Settings (Notes, Meeting file: the transcript {(settings.MeetingFile.IncludeTranscript ? "in" : "left out")}); " +
                        "--out <folder> writes one the other way elsewhere");
        Console.WriteLine(MeetingFile.Write(rec, settings.DisplayName, outFolder, new Vocabulary(settings.Vocabulary), transcript: transcript));
        return 0;
    }

    /// <summary>The vocabulary in settings.json. The tray app reads it again on "Reload settings" or its next start.</summary>
    static int Vocab(AppSettings settings, string[] args)
    {
        switch (Arg(args, 1))
        {
            case "list":
                if (settings.Vocabulary.Count == 0) Console.WriteLine("no words yet: llmmr vocab add \"Kubernetes\" \"Cooper Netties\"");
                foreach (var e in settings.Vocabulary.OrderBy(e => e.Term, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine(e.HeardAs.Count == 0 ? $"  {e.Term}" : $"  {e.Term}  (misheard as {string.Join(", ", e.HeardAs)})");
                return 0;
            case "add":
            {
                var term = Arg(args, 2);
                var heard = args.Skip(3).Where(a => !a.StartsWith("--")).ToList();
                if (heard.Count == 0) Vocabulary.Add(settings.Vocabulary, term);
                foreach (var h in heard) Vocabulary.Add(settings.Vocabulary, term, h);
                if (Vocabulary.Problems(settings.Vocabulary).FirstOrDefault() is { } problem) return Fail(problem);
                SettingsStore.Save(settings, AppPaths.SettingsFile);
                Console.WriteLine($"saved ({settings.Vocabulary.Count} words). New meetings get it once the tray app reloads its settings.");
                if (heard.Count > 0) Console.WriteLine("For meetings already made: llmmr vocab fix --all");
                return 0;
            }
            case "remove":
                if (!Vocabulary.Remove(settings.Vocabulary, Arg(args, 2))) return Fail($"'{Arg(args, 2)}' isn't in the vocabulary");
                SettingsStore.Save(settings, AppPaths.SettingsFile);
                Console.WriteLine("removed (notes already fixed stay fixed)");
                return 0;
            case "fix":
            {
                var vocabulary = new Vocabulary(settings.Vocabulary);
                if (!vocabulary.CanFix) return Fail("the vocabulary has no misheard words to fix");
                if (args.Contains("--all"))
                {
                    var (done, failed) = MeetingFixes.ApplyAll(settings.ResolvedRecordingsFolder, vocabulary, settings.DisplayName,
                        transcript: settings.MeetingFile.IncludeTranscript);
                    Console.WriteLine($"fixed {done} {(done == 1 ? "meeting" : "meetings")}" + (failed > 0 ? $"; {failed} couldn't be (a note open elsewhere?)" : ""));
                    return failed > 0 ? 1 : 0;
                }
                int n = MeetingFixes.Apply(Recording(settings, Arg(args, 2)), vocabulary, settings.DisplayName, settings.MeetingFile.IncludeTranscript);
                Console.WriteLine(n == 0 ? "nothing to fix (or the meeting isn't finished)" : $"fixed {n} {(n == 1 ? "word" : "words")}");
                return 0;
            }
            default:
                return Fail($"unknown vocab command '{args[1]}'\n\n{Usage}");
        }
    }

    static AppSettings Clone(AppSettings s) =>
        System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(s, Json.Options), Json.Options)!;

    static string Arg(string[] args, int index) =>
        args.Length > index && !args[index].StartsWith("--") ? args[index] : throw new ArgumentException($"missing argument {index}\n\n{Usage}");

    static string? Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 1;
    }

    sealed class ArraySampleProvider(float[] samples, WaveFormat format) : ISampleProvider
    {
        int _position;
        public WaveFormat WaveFormat { get; } = format;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, samples.Length - _position);
            // Element by element on purpose: NAudio may pass a float[] view over a byte[] (WaveBuffer),
            // and Array.Copy/Span copies into that throw "Source array type cannot be assigned".
            for (int i = 0; i < n; i++) buffer[offset + i] = samples[_position + i];
            _position += n;
            return n;
        }
    }
}
