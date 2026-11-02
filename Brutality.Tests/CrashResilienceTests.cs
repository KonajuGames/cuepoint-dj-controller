namespace Brutality.Tests;

public class SessionStateTests
{
    static SessionState Sample(DateTime savedAt) =>
        new()
        {
            SavedAt = savedAt,
            Decks =
            [
                new() { Name = "A", Path = @"F:\Music\a.mp3", Position = 61.5, Playing = true, Tempo = 0.04, KeyLocked = true },
                new() { Name = "B", Path = null },
            ],
        };

    [Fact]
    public void A_session_comes_back_as_it_was_saved()
    {
        using var folder = new TempFolder();
        var path = folder["Cuepoint", "session.json"];
        var savedAt = new DateTime(2026, 11, 3, 21, 30, 0, DateTimeKind.Utc);

        Assert.Null(Sample(savedAt).Save(path));
        var loaded = SessionState.Load(path)!;

        Assert.Equal(savedAt, loaded.SavedAt);
        var a = loaded.Decks[0];
        Assert.Equal(("A", @"F:\Music\a.mp3", 61.5, true, 0.04, true), (a.Name, a.Path, a.Position, a.Playing, a.Tempo, a.KeyLocked));
        Assert.Null(loaded.Decks[1].Path);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void No_session_means_the_app_closed_properly()
    {
        using var folder = new TempFolder();
        var path = folder["session.json"];
        Sample(DateTime.UtcNow).Save(path);

        SessionState.Delete(path);

        Assert.Null(SessionState.Load(path));
        // Deleting what isn't there is no trouble.
        SessionState.Delete(path);
    }

    [Fact]
    public void A_session_that_will_not_read_is_passed_over()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder["session.json"], "{ half written");

        Assert.Null(SessionState.Load(folder["session.json"]));
    }

    [Fact]
    public void A_playing_deck_is_put_where_it_would_have_got_to_at_its_tempo()
    {
        var savedAt = new DateTime(2026, 11, 3, 21, 30, 0, DateTimeKind.Utc);
        var session = Sample(savedAt);
        var playing = session.Decks[0];

        // Ten seconds on at 4% fast is 10.4 seconds of song.
        Assert.Equal(61.5 + 10.4, session.PositionAt(playing, savedAt.AddSeconds(10)), 6);

        playing.Playing = false;
        Assert.Equal(61.5, session.PositionAt(playing, savedAt.AddSeconds(10)));

        // A clock gone backwards doesn't take it back.
        playing.Playing = true;
        Assert.Equal(61.5, session.PositionAt(playing, savedAt.AddSeconds(-5)));
    }
}

[Collection(nameof(ErrorLogTests))]
public class ErrorLogTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly string? _was = ErrorLog.FilePath;

    public void Dispose()
    {
        ErrorLog.FilePath = _was;
        _folder.Dispose();
    }

    static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException e)
        {
            return e;
        }
    }

    [Fact]
    public void Writes_what_went_wrong_and_what_the_app_was_doing()
    {
        ErrorLog.FilePath = _folder["Cuepoint", "errors.log"];

        ErrorLog.Write("drawing the screen", Thrown("the swapchain went away"));
        ErrorLog.Write("loading a song", Thrown("no such file"));

        var text = File.ReadAllText(ErrorLog.FilePath);
        Assert.Contains("While drawing the screen:", text);
        Assert.Contains("the swapchain went away", text);
        Assert.Contains(nameof(Thrown), text);
        Assert.Contains("While loading a song:", text);
    }

    [Fact]
    public void Starts_afresh_once_it_gets_big_keeping_the_last_lot()
    {
        ErrorLog.FilePath = _folder["errors.log"];
        File.WriteAllText(ErrorLog.FilePath, new string('x', 2 << 20));

        ErrorLog.Write("reading tags", Thrown("oops"));

        Assert.True(new FileInfo(ErrorLog.FilePath).Length < 10_000);
        Assert.True(File.Exists(_folder["errors.old.log"]));
    }

    [Fact]
    public void Never_throws_when_it_cannot_write()
    {
        // A file where its folder would be.
        var blocker = _folder.File("blocker");
        ErrorLog.FilePath = Path.Combine(blocker, "errors.log");

        ErrorLog.Write("anything", Thrown("oops"));

        ErrorLog.FilePath = null;
        ErrorLog.Write("anything", Thrown("oops"));
    }
}
