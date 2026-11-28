using RSMatrix.Desktop.Storage;
using RSMatrix.Desktop.ViewModels;

namespace RSMatrix.Desktop.Tests;

public class SettingsTests
{
    [Test]
    public async Task Appearance_Survives_A_Save_And_Older_Files_Get_Defaults()
    {
        var directory = Directory.CreateTempSubdirectory("rsmatrix-settings-tests").FullName;
        try
        {
            var localData = new LocalData(directory);
            File.WriteAllText(Path.Combine(directory, "settings.json"), """{ "LastUserId": "@alice:example.org" }""");
            var settings = localData.LoadSettings();
            await Assert.That(settings.Appearance).IsEqualTo(new AppearanceSettings());

            settings.Appearance = new AppearanceSettings { Theme = ThemePreference.Dark, FontFamily = "Hack", FontSize = 16, ShowRoomIcons = false };
            localData.SaveSettings(settings);

            await Assert.That(File.ReadAllText(Path.Combine(directory, "settings.json"))).Contains("\"Dark\"");
            await Assert.That(localData.LoadSettings().Appearance).IsEqualTo(settings.Appearance);
            await Assert.That(localData.LoadSettings().LastUserId).IsEqualTo("@alice:example.org");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Settings_Change_Only_On_Save()
    {
        var results = new List<SettingsDialogResult?>();
        var settings = new SettingsViewModel(new AppearanceSettings(), 5, ["Inter", "Hack"], results.Add);

        settings.FontFamily = "Hack";
        settings.CancelCommand.Execute(null);
        settings.ShowRoomPreviews = false;
        settings.IsClassicLayout = true;
        settings.IdleTime = settings.IdleTimes.Single(t => t.Label == "Never");
        settings.SaveCommand.Execute(null);

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results[0]).IsNull();
        await Assert.That(results[1]).IsEqualTo(new SettingsDialogResult(
            new AppearanceSettings { FontFamily = "Hack", ShowRoomPreviews = false, MessageLayout = MessageLayout.Classic }, 0));
        await Assert.That(settings.IsComfortableLayout).IsFalse();
    }

    [Test]
    public async Task Unknown_Choices_From_Another_Version_Fall_Back_To_Defaults()
    {
        var directory = Directory.CreateTempSubdirectory("rsmatrix-settings-tests").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "settings.json"),
                """{ "Appearance": { "Theme": "Sepia", "MessageLayout": "Bubbles", "FontSize": 16 }, "Presence": { "Mode": "Busy", "IdleMinutes": 7 } }""");
            var settings = new LocalData(directory).LoadSettings();

            await Assert.That(settings.Appearance).IsEqualTo(new AppearanceSettings { FontSize = 16 });
            await Assert.That(settings.Presence).IsEqualTo(new PresenceSettings());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_Saved_Font_That_Is_Missing_Stays_Selectable()
    {
        var settings = new SettingsViewModel(new AppearanceSettings { FontFamily = "Gone" }, 5, ["Inter"], _ => { });

        await Assert.That(settings.FontFamily).IsEqualTo("Gone");
        await Assert.That(settings.FontFamilies).Contains("Gone");
    }
}
