using System.Buffers.Binary;
using NAudio.MediaFoundation;
using NAudio.Wave;
using Lemur.Audio;
using Lemur.Backends;
using Lemur.Config;
using Lemur.Pipeline;

namespace Lemur.Tests;

public class RowGroupTests
{
    static RecordingRow Row(string name, DateTime? local, double seconds = 600) =>
        new(name, name, local is { } t ? new DateTimeOffset(t) : null, name, seconds, RowKind.Done, "Done", "", null, null);

    [Fact]
    public void Rows_go_under_today_yesterday_this_week_last_week_the_rest_of_the_month_then_months()
    {
        var today = new DateTime(2026, 10, 15); // a Thursday; weeks start on Sunday here, as in the US
        var rows = new[]
        {
            Row("a", today.AddHours(9), 48 * 60), Row("b", today.AddHours(11), 17 * 60),
            Row("c", new DateTime(2026, 10, 14, 15, 0, 0)),
            Row("d", new DateTime(2026, 10, 12, 10, 0, 0)), Row("e", new DateTime(2026, 10, 11, 10, 0, 0)),
            Row("f", new DateTime(2026, 10, 10, 10, 0, 0)), Row("g", new DateTime(2026, 10, 4, 10, 0, 0)),
            Row("h", new DateTime(2026, 10, 3, 10, 0, 0)), Row("i", new DateTime(2026, 10, 1, 10, 0, 0)),
            Row("j", new DateTime(2026, 9, 30, 10, 0, 0)), Row("k", new DateTime(2026, 8, 2, 10, 0, 0)),
            Row("l", null),
        };

        var groups = RowGroups.Of(rows, today, DayOfWeek.Sunday);

        Assert.Equal(["Today", "Yesterday", "Earlier this week", "Last week", "Earlier in October", "September 2026", "August 2026", "No date"],
            groups.Select(g => g.Title));
        Assert.Equal(["a b", "c", "d e", "f g", "h i", "j", "k", "l"], groups.Select(g => string.Join(" ", g.Rows.Select(r => r.Name))));
        Assert.Equal("2 meetings · 1 h 05 min", groups[0].Summary);
        Assert.Equal("1 meeting · 10 min", groups[1].Summary);
        Assert.Equal([true, true, false, false, false, false, false, false], groups.Select(g => g.OpenAtFirst));
        Assert.Equal(["today:2026-10-15", "yesterday:2026-10-15", "week:2026-10-11", "week:2026-10-04", "rest:2026-10-01", "month:2026-09",
            "month:2026-08", "undated"], groups.Select(g => g.Key));
    }

    [Fact]
    public void A_week_starting_on_monday_and_a_yesterday_in_last_week()
    {
        var today = new DateTime(2026, 11, 2); // a Monday: yesterday was last week's Sunday
        var rows = new[]
        {
            Row("sun", new DateTime(2026, 11, 1, 10, 0, 0)), Row("mon", new DateTime(2026, 10, 26, 10, 0, 0)),
            Row("sat", new DateTime(2026, 10, 25, 10, 0, 0)),
        };

        var groups = RowGroups.Of(rows, today, DayOfWeek.Monday);

        Assert.Equal(["Yesterday", "Last week", "October 2026"], groups.Select(g => g.Title));
        Assert.Equal(["sun", "mon", "sat"], groups.Select(g => g.Rows.Single().Name));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(20, "under 1 min")]
    [InlineData(45 * 60, "45 min")]
    [InlineData(2 * 3600, "2 h")]
    [InlineData(2 * 3600 + 10 * 60, "2 h 10 min")]
    [InlineData(61 * 3600 + 20 * 60, "61 h")]
    public void A_group_says_how_long_its_meetings_were_altogether(double seconds, string? said) =>
        Assert.Equal(said, RowGroups.Hours(seconds));
}

public class ImportTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-import").FullName;
    string Recordings => Path.Combine(_root, "rec");
    string Inbox => Path.Combine(_root, "inbox");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>A phone-like file: 44.1 kHz stereo, a tone in each second but the first.</summary>
    string Wav(string name, double seconds = 3, bool silent = false, string? folder = null)
    {
        var path = Path.Combine(folder ?? _root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        using (var writer = new WaveFileWriter(path, format))
        {
            int frames = (int)(seconds * 44100);
            var block = new float[2 * frames];
            for (int i = 0; i < frames; i++)
                block[2 * i] = block[2 * i + 1] = silent || i < 44100 ? 0 : 0.3f * MathF.Sin(i * 2 * MathF.PI * 440 / 44100);
            writer.WriteSamples(block, 0, block.Length);
        }
        return path;
    }

    [Theory]
    [InlineData("New Recording 12.m4a", "")]
    [InlineData("New Recording.m4a", "")]
    [InlineData("Recording (2).m4a", "")]
    [InlineData("Voice 20261014_093000.m4a", "")]
    [InlineData("ZOOM0001.WAV", "")]
    [InlineData("IMG_1234.MOV", "")]
    [InlineData("20261014.mp3", "")]
    [InlineData("2026-10-14 0930 Design review.mp3", "Design review")]
    [InlineData("Budget_review_with_Dana.m4a", "Budget review with Dana")]
    [InlineData("123 Main St.m4a", "123 Main St")]
    public void A_title_comes_from_the_file_name_unless_it_says_nothing(string name, string title) =>
        Assert.Equal(title, RecordingImport.TitleFrom(name));

    [Fact]
    public void A_date_and_time_in_the_file_name_is_when_it_began()
    {
        Assert.Equal(new DateTime(2026, 10, 14, 9, 30, 0), RecordingImport.FromName("Voice 20261014_093000")?.DateTime);
        Assert.Equal(new DateTime(2026, 10, 14, 9, 30, 0), RecordingImport.FromName("2026-10-14 09.30 standup")?.DateTime);
        Assert.Equal(new DateTime(2026, 10, 14, 9, 30, 0), RecordingImport.FromName("2026-10-14 0930 Design review")?.DateTime);
        Assert.Null(RecordingImport.FromName("Weekly sync"));
        Assert.Null(RecordingImport.FromName("2026-02-31 1000")); // no such day
        Assert.Null(RecordingImport.FromName("2099-01-01 1000")); // not yet
    }

    /// <summary>The boxes an MP4 file needs for its recording time: ftyp, some media, and moov holding mvhd.</summary>
    static byte[] Mp4(ulong created, bool version1 = false, bool moovLast = true)
    {
        static byte[] Box(string type, byte[] body)
        {
            var box = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
            body.CopyTo(box, 8);
            return box;
        }
        var mvhd = new byte[version1 ? 112 : 100];
        mvhd[0] = (byte)(version1 ? 1 : 0);
        if (version1) BinaryPrimitives.WriteUInt64BigEndian(mvhd.AsSpan(4), created);
        else BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(4), (uint)created);
        var moov = Box("moov", [.. Box("mvhd", mvhd), .. Box("trak", new byte[40])]);
        var ftyp = Box("ftyp", "M4A \0\0\0\0M4A isom"u8.ToArray());
        var mdat = Box("mdat", new byte[5000]);
        return moovLast ? [.. ftyp, .. mdat, .. moov] : [.. ftyp, .. moov, .. mdat];
    }

    static ulong Since1904(DateTimeOffset t) => (ulong)(t - new DateTimeOffset(1904, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;

    [Fact]
    public void A_phones_file_says_when_it_was_recorded_in_its_movie_header()
    {
        var at = new DateTimeOffset(2026, 10, 14, 14, 30, 5, TimeSpan.Zero);
        var path = Path.Combine(_root, "memo.m4a");

        File.WriteAllBytes(path, Mp4(Since1904(at)));
        Assert.Equal(at, RecordingImport.RecordedAt(path));
        File.WriteAllBytes(path, Mp4(Since1904(at), version1: true, moovLast: false));
        Assert.Equal(at, RecordingImport.RecordedAt(path));
        File.WriteAllBytes(path, Mp4(0)); // never set
        Assert.Null(RecordingImport.RecordedAt(path));
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        Assert.Null(RecordingImport.RecordedAt(path));
        var mp3 = Path.Combine(_root, "memo.mp3");
        File.WriteAllBytes(mp3, Mp4(Since1904(at))); // only MP4 kinds of file are asked
        Assert.Null(RecordingImport.RecordedAt(mp3));
    }

    [Fact]
    public void A_file_becomes_a_meeting_waiting_for_notes_as_an_in_person_one()
    {
        var path = Wav("Budget_review.wav");
        var saved = new DateTime(2026, 10, 14, 10, 0, 3);
        File.SetLastWriteTime(path, saved);
        var before = File.ReadAllBytes(path);

        var rec = RecordingImport.Import(Recordings, path);

        Assert.Equal("20261014-100000", rec.Name); // saved when it stopped, 3 s after it began
        var meta = rec.LoadMeta();
        Assert.Equal(("Budget review", true, false, "Imported"), (meta.Title, meta.TitleFromFile, meta.TitleByUser, meta.Source));
        Assert.Equal(3, meta.DurationSeconds, 1);
        Assert.Equal(StartSource.Modified, meta.Imported!.StartedFrom);
        Assert.Equal(("Budget_review.wav", before.Length, RecordingImport.Sha256Of(path)), (meta.Imported.Name, meta.Imported.Bytes, meta.Imported.Sha256));
        Assert.Equal(Stage.Recorded, rec.LoadState().Stage);
        Assert.True(MeetingAbout.NeedsTitle(meta)); // the file's name stands in until the summary suggests a title

        using (var mic = new WaveFileReader(rec.MicWav))
        {
            Assert.Equal((16000, 1), (mic.WaveFormat.SampleRate, mic.WaveFormat.Channels));
            Assert.Equal(3, mic.TotalTime.TotalSeconds, 1);
        }
        using (var others = new WaveFileReader(rec.OthersWav)) Assert.Equal(0, others.Length); // no call audio
        Assert.Equal(before, File.ReadAllBytes(path)); // the file is only read
        Assert.Empty(Directory.GetFiles(Recordings)); // nothing left beside the recordings
        Assert.Equal(rec.Path, RecordingImport.FindImported(Recordings, meta.Imported.Sha256)?.Path);
    }

    [Fact]
    public void A_title_and_time_chosen_when_importing_are_the_users()
    {
        var at = new DateTimeOffset(2026, 10, 13, 16, 45, 0, TimeSpan.FromHours(-5));
        var rec = RecordingImport.Import(Recordings, Wav("New Recording 3.wav"), "Coffee with Sam", at);
        var meta = rec.LoadMeta();
        Assert.Equal(("Coffee with Sam", true, false, StartSource.User), (meta.Title, meta.TitleByUser, meta.TitleFromFile, meta.Imported!.StartedFrom));
        Assert.Equal(at, meta.StartedAt);

        var untitled = RecordingImport.Import(Recordings, Wav("x.wav"), "", at).LoadMeta();
        Assert.Equal(("", false, false), (untitled.Title, untitled.TitleByUser, untitled.TitleFromFile));
    }

    [Fact]
    public void A_silent_file_or_one_that_isnt_audio_is_refused_and_leaves_nothing_behind()
    {
        var silent = Assert.Throws<ImportException>(() => RecordingImport.Import(Recordings, Wav("quiet.wav", silent: true)));
        Assert.True(silent.Permanent);
        var junk = Path.Combine(_root, "junk.m4a");
        File.WriteAllBytes(junk, new byte[4096]);
        Assert.True(Assert.Throws<ImportException>(() => RecordingImport.Import(Recordings, junk)).Permanent);
        Assert.Empty(Directory.GetFileSystemEntries(Recordings));
    }

    [Fact]
    public void A_compressed_phone_file_is_read_by_windows_and_keeps_its_recording_time()
    {
        var wav = Wav("memo.wav", seconds: 4);
        var m4a = Path.Combine(_root, "memo.m4a");
        MediaFoundationApi.Startup();
        using (var reader = new WaveFileReader(wav))
            MediaFoundationEncoder.EncodeToAac(new NAudio.Wave.SampleProviders.SampleToWaveProvider16(reader.ToSampleProvider()), m4a, 96000);

        var guess = RecordingImport.Guess(m4a);
        var rec = RecordingImport.Import(Recordings, m4a);

        Assert.Equal(4, guess.Seconds, 0);
        using var mic = new WaveFileReader(rec.MicWav);
        Assert.Equal(4, mic.TotalTime.TotalSeconds, 0);
        // Windows' encoder writes the time it made the file into the movie header, as a phone writes when it recorded.
        Assert.Equal(StartSource.Recording, rec.LoadMeta().Imported!.StartedFrom);
    }

    [Fact]
    public async Task An_imported_recording_goes_through_every_stage_as_an_in_person_meeting()
    {
        var s = new AppSettings
        {
            DisplayName = "Paul", RecordingsFolder = Recordings, NotesFolder = Path.Combine(_root, "notes"),
            Backend = { Type = BackendTypes.Scriberr, Scriberr = { BaseUrl = "https://x" } },
        };
        var rec = RecordingImport.Import(Recordings, Wav("123 Main St.wav"), startedAt: new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(-5)));
        var backend = new FakeBackend { Summary = "Title: Budget planning\nAbout: Next year's budget.\n\n## Summary\nWe planned.\n\n## Action items\n- [ ] Dana - send numbers" };

        var state = await new MeetingProcessor(s, () => backend).ProcessAsync(rec, CancellationToken.None);

        Assert.Equal(Stage.Done, state.Stage);
        Assert.Null(backend.Named); // nobody is "me" by track in a recording with no call audio
        Assert.DoesNotContain("Paul:", backend.LastTranscriptText);
        var meta = rec.LoadMeta();
        Assert.Equal(("Budget planning", false), (meta.Title, meta.TitleFromFile)); // the summary's title replaced the street name
        Assert.EndsWith("2026-10-14 0930 Budget planning.md", state.NotePath);
        Assert.Contains("source: Imported", File.ReadAllText(state.NotePath!));
    }

    [Fact]
    public void The_import_folder_takes_a_file_once_it_has_settled_and_moves_it_to_imported()
    {
        var path = Wav("Standup.wav", folder: Inbox);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var folder = new ImportFolder();

        Assert.Empty(folder.Scan(Inbox, Recordings)); // first seen: it may still be arriving
        var result = Assert.Single(folder.Scan(Inbox, Recordings));

        Assert.NotNull(result.Recording);
        Assert.Null(result.Problem);
        Assert.Equal(Path.Combine(Inbox, ImportFolder.ImportedName, "Standup.wav"), result.MovedTo);
        Assert.False(File.Exists(path));
        Assert.Equal("Standup", result.Recording!.LoadMeta().Title);
        Assert.Empty(folder.Scan(Inbox, Recordings));
    }

    [Fact]
    public void A_file_still_changing_is_left_until_it_stops()
    {
        var path = Wav("Long call.wav", folder: Inbox);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var folder = new ImportFolder();
        folder.Scan(Inbox, Recordings);
        File.AppendAllText(path, "more"); // the sync client wrote more
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-30));
        Assert.Empty(folder.Scan(Inbox, Recordings));
        Assert.Single(folder.Scan(Inbox, Recordings));
    }

    [Fact]
    public void The_import_folder_moves_a_file_it_cant_use_aside_and_leaves_other_files_alone()
    {
        Directory.CreateDirectory(Inbox);
        var junk = Path.Combine(Inbox, "broken.m4a");
        File.WriteAllBytes(junk, new byte[4096]);
        var note = Path.Combine(Inbox, "notes.txt");
        File.WriteAllText(note, "not a recording");
        var office = Path.Combine(Inbox, "~$draft.m4a"); // what an editor leaves while it works
        File.WriteAllBytes(office, new byte[10]);
        foreach (var f in new[] { junk, note, office }) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-1));
        var folder = new ImportFolder();

        folder.Scan(Inbox, Recordings);
        var result = Assert.Single(folder.Scan(Inbox, Recordings));

        Assert.Equal("broken.m4a", result.FileName);
        Assert.NotNull(result.Problem);
        Assert.Equal(Path.Combine(Inbox, ImportFolder.FailedName, "broken.m4a"), result.MovedTo);
        Assert.True(File.Exists(note));
        Assert.True(File.Exists(office));
        Assert.False(Directory.Exists(Recordings) && Directory.EnumerateDirectories(Recordings).Any());
    }

    [Fact]
    public void A_file_imported_before_is_not_imported_twice()
    {
        var first = Wav("Retro.wav", folder: Inbox);
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(-1));
        var bytes = File.ReadAllBytes(first);
        var folder = new ImportFolder();
        folder.Scan(Inbox, Recordings);
        var made = Assert.Single(folder.Scan(Inbox, Recordings)).Recording;

        File.WriteAllBytes(first, bytes); // dropped in again
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(-1));
        folder.Scan(Inbox, Recordings);
        var again = Assert.Single(folder.Scan(Inbox, Recordings));

        Assert.True(again.AlreadyImported);
        Assert.Equal(made!.Path, again.Recording!.Path);
        Assert.Equal(Path.Combine(Inbox, ImportFolder.ImportedName, "Retro (2).wav"), again.MovedTo);
        Assert.Single(RecordingFolder.All(Recordings));
    }

    [Fact]
    public void The_import_folder_cant_be_the_notes_or_recordings_folder()
    {
        Assert.NotNull(RecordingImport.FolderProblem(@"C:\Notes\", @"C:\notes", @"C:\rec"));
        Assert.NotNull(RecordingImport.FolderProblem(@"C:\rec", @"C:\notes", @"C:\rec"));
        Assert.Null(RecordingImport.FolderProblem(@"C:\notes\Voice memos", @"C:\notes", @"C:\rec"));
    }

    [Fact]
    public void An_import_setting_left_null_in_the_file_reads_as_none()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{ "displayName": "Paul", "import": null }""");
        Assert.Null(SettingsStore.LoadOrCreate(path).Import.Folder);
    }
}
