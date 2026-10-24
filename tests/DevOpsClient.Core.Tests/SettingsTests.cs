using DevOpsClient.Core;
using Xunit;

namespace DevOpsClient.Core.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void Encoded_project_names_are_preserved()
    {
        var settings = new ClientSettings { ProjectUrl = "https://dev.azure.com/org/My%20Project", PersonalAccessToken = "test-token", UseDemoData = false };

        Assert.Equal("My Project", settings.ProjectName);
        Assert.Equal("/org/My%20Project/", settings.ProjectUri().AbsolutePath);
    }

    [Fact]
    public void Legacy_visualstudio_project_urls_are_supported()
    {
        var settings = new ClientSettings { ProjectUrl = "https://org.visualstudio.com/My%20Project", PersonalAccessToken = "test-token", UseDemoData = false };

        settings.Validate();
        Assert.Equal("My Project", settings.ProjectName);
    }

    [Theory]
    [InlineData("http://dev.azure.com/org/project")]
    [InlineData("https://example.com/org/project")]
    [InlineData("https://dev.azure.com/org")]
    [InlineData("https://dev.azure.com/org/project/_boards")]
    [InlineData("https://user:password@dev.azure.com/org/project")]
    [InlineData("https://dev.azure.com/org/project?token=secret")]
    [InlineData("https://dev.azure.com:444/org/project")]
    public void Unsafe_or_non_project_urls_are_rejected(string url)
    {
        var settings = new ClientSettings { ProjectUrl = url, PersonalAccessToken = "test-token", UseDemoData = false };

        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void Live_settings_require_a_pat()
    {
        var settings = new ClientSettings { ProjectUrl = "https://dev.azure.com/org/project", UseDemoData = false };

        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void Work_item_limit_is_bounded()
    {
        var settings = new ClientSettings { MaxWorkItems = 2001 };

        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void Defaults_are_safe_for_demo_and_accessibility()
    {
        var settings = new ClientSettings();

        Assert.True(settings.UseDemoData);
        Assert.Equal("Dark", settings.Theme);
        Assert.Equal("Segoe UI", settings.FontFamily);
        Assert.Equal(1.0, settings.FontScale);
        settings.Validate();
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("High contrast")]
    public void Supported_themes_validate(string theme)
    {
        var settings = new ClientSettings { Theme = theme, FontScale = 1.35 };

        settings.Validate();
    }

    [Theory]
    [InlineData("Solarized")]
    [InlineData(0.79)]
    [InlineData(1.81)]
    public void Invalid_appearance_values_are_rejected(object value)
    {
        var settings = value is string theme
            ? new ClientSettings { Theme = theme }
            : new ClientSettings { FontScale = (double)value };

        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void Appearance_settings_round_trip_through_local_override()
    {
        var folder = Path.Combine(Path.GetTempPath(), "orbit-xunit-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var expected = new ClientSettings { Theme = "High contrast", FontFamily = "Verdana", FontScale = 1.55 };
            SettingsStore.Save(folder, expected);

            var actual = SettingsStore.Load(folder);
            Assert.Equal(expected.Theme, actual.Theme);
            Assert.Equal(expected.FontFamily, actual.FontFamily);
            Assert.Equal(expected.FontScale, actual.FontScale);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
