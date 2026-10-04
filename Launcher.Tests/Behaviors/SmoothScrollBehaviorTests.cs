/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Xml.Linq;
using Launcher.App.Behaviors;

namespace Launcher.Tests.Behaviors;

public sealed class SmoothScrollBehaviorTests
{
    [Fact]
    public void DefaultWheelAnimationDurationIs130Milliseconds()
    {
        var defaultValue = SmoothScrollBehavior
            .WheelAnimationDurationMillisecondsProperty
            .DefaultMetadata
            .DefaultValue;

        Assert.Equal(130d, Assert.IsType<double>(defaultValue));
    }

    [Fact]
    public void EveryXamlOverrideUses130Milliseconds()
    {
        var overrides = TestRepository.EnumerateProjectFiles("Launcher.App", "*.xaml")
            .SelectMany(FindDurationOverrides)
            .ToArray();

        Assert.NotEmpty(overrides);
        Assert.All(overrides, item => Assert.Equal("130", item.Value));
    }

    private static IEnumerable<(string File, string Value)> FindDurationOverrides(string filePath)
    {
        var document = XDocument.Load(filePath);

        foreach (var attribute in document.Descendants().Attributes()
                     .Where(attribute => attribute.Name.LocalName == "WheelAnimationDurationMilliseconds"))
        {
            yield return (filePath, attribute.Value);
        }

        foreach (var setter in document.Descendants().Where(element => element.Name.LocalName == "Setter"))
        {
            var property = setter.Attribute("Property")?.Value;
            if (property?.EndsWith("WheelAnimationDurationMilliseconds", StringComparison.Ordinal) is not true)
                continue;

            var value = setter.Attribute("Value")?.Value;
            if (value is not null)
                yield return (filePath, value);
        }
    }

}
