using Brutality.UI;

namespace Brutality.Tests;

public class AppSettingsTests
{
    [Fact]
    public void A_deck_s_effect_settings_are_kept()
    {
        using var folder = new TempFolder();
        var settings = new AppSettings
        {
            Effects = new() { ["A"] = new AppSettings.DeckEffectSettings { Kind = Brutality.Audio.DeckEffect.Flanger, Level = 0.25f, EchoBeats = 1, FlangerBeats = 16 } },
        };

        settings.Save(folder["settings.json"]);

        var effects = AppSettings.Load(folder["settings.json"]).Effects!["A"];
        Assert.Equal(Brutality.Audio.DeckEffect.Flanger, effects.Kind);
        Assert.Equal(0.25f, effects.Level);
        Assert.Equal(1, effects.EchoBeats);
        Assert.Equal(16, effects.FlangerBeats);
    }

    [Fact]
    public void Settings_come_back_as_they_were_saved()
    {
        using var folder = new TempFolder();
        var path = folder["Cuepoint", "settings.json"];
        var settings = new AppSettings
        {
            OutputDevice = "Speakers",
            HeadphoneDevice = "DDJ-SB3",
            CueMix = 0.25f,
            MusicFolder = @"F:\Music",
            LargeListText = true,
            MasterLimiter = false,
            BrowserFolder = ["Virtual folders", "Warm up"],
            Browser = new BrowserLayout { SortColumn = "BPM", SortDescending = true, ColumnWidths = { ["Key"] = 130 }, HiddenColumns = ["Album"] },
        };

        Assert.Null(settings.Save(path));
        var loaded = AppSettings.Load(path);

        Assert.Equal("Speakers", loaded.OutputDevice);
        Assert.Equal("DDJ-SB3", loaded.HeadphoneDevice);
        Assert.Equal(0.25f, loaded.CueMix);
        Assert.Equal(@"F:\Music", loaded.MusicFolder);
        Assert.True(loaded.LargeListText);
        Assert.False(loaded.MasterLimiter);
        Assert.Equal(new[] { "Virtual folders", "Warm up" }, loaded.BrowserFolder);
        Assert.Equal("BPM", loaded.Browser.SortColumn);
        Assert.True(loaded.Browser.SortDescending);
        Assert.Equal(130, loaded.Browser.ColumnWidths["Key"]);
        Assert.Equal(new[] { "Album" }, loaded.Browser.HiddenColumns);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void No_file_or_a_broken_one_gives_the_defaults()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder["broken.json"], "{ not json");

        foreach (var settings in new[] { AppSettings.Load(folder["missing.json"]), AppSettings.Load(folder["broken.json"]) })
        {
            Assert.Null(settings.OutputDevice);
            Assert.Null(settings.MusicFolder);
            Assert.True(settings.Os2lEnabled);
            Assert.True(settings.MasterLimiter);
            Assert.Equal(0.8f, settings.HeadphoneVolume);
        }
    }

    [Fact]
    public void Settings_written_by_an_older_version_keep_what_they_have_and_take_defaults_for_the_rest()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder["old.json"], """{ "outputDevice": "Speakers", "recursiveFolders": ["F:\\Music"] }""");

        var settings = AppSettings.Load(folder["old.json"]);

        Assert.Equal("Speakers", settings.OutputDevice);
        Assert.True(settings.MasterLimiter);
    }
}
