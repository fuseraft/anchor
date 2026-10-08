using Anchor.Cli;

namespace Anchor.Tests;

public class ThemeTests
{
    [Fact]
    public void NoConfig_IsTheDefaultTheme()
    {
        var (theme, warnings) = Theme.From(null, null);

        Assert.Equal(new Theme(), theme);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ABuiltInTheme_IsFoundByName()
    {
        var (theme, warnings) = Theme.From("Mono", null);

        Assert.Equal("mono", theme.Name);
        Assert.Equal("1", theme.Accent);
        Assert.Empty(warnings);
    }

    [Fact]
    public void TheLightTheme_UsesNoYellowOrBrightColors()
    {
        var light = Theme.Named("light")!;
        var codes = typeof(Theme).GetProperties().Where(p => p.PropertyType == typeof(string) && p.Name is not (nameof(Theme.Name) or nameof(Theme.Description) or nameof(Theme.Border)))
            .SelectMany(p => ((string)p.GetValue(light)!).Split(';'));

        Assert.DoesNotContain(codes, c => c == "33" || c.StartsWith('9'));
    }

    [Fact]
    public void Colors_OverrideSingleRoles()
    {
        var (theme, _) = Theme.From("bright", new Dictionary<string, string> { ["Accent"] = "bold magenta", ["code"] = "none" });

        Assert.Equal("1;35", theme.Accent);
        Assert.Equal("", theme.Code);
        Assert.Equal("92", theme.Success);
    }

    [Fact]
    public void WhatCantBeRead_IsReportedAndLeftAsItWas()
    {
        var (theme, warnings) = Theme.From("solarized", new Dictionary<string, string> { ["acent"] = "red", ["tool"] = "teal" });

        Assert.Equal(new Theme(), theme);
        Assert.Collection(warnings,
            w => Assert.Contains("Unknown theme 'solarized'", w),
            w => Assert.Contains("Unknown color role 'acent'", w),
            w => Assert.Contains("Can't read the color 'teal'", w));
    }

    [Theory]
    [InlineData("red", "31")]
    [InlineData("bright-blue", "94")]
    [InlineData("Gray", "90")]
    [InlineData("bold  underline green", "1;4;32")]
    [InlineData("none", "")]
    [InlineData("#ff0000", null)]
    public void Sgr_ReadsColorWords(string words, string? sgr) => Assert.Equal(sgr, Theme.Sgr(words));

    [Fact]
    public void StyleOf_ReadsACodeBackAsAStyle()
    {
        Assert.Equal(new Style(96, true, false), Theme.StyleOf("1;96"));
        Assert.Equal(default, Theme.StyleOf(""));
    }
}
