using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;

namespace TradeFix.Shared.Tests;

public class SourceDefinitionTests
{
    [Theory]
    [InlineData(SourceType.Camera, SourceCategory.DeviceMapped)]
    [InlineData(SourceType.Microphone, SourceCategory.DeviceMapped)]
    [InlineData(SourceType.DisplayCapture, SourceCategory.DeviceMapped)]
    [InlineData(SourceType.WindowCapture, SourceCategory.DeviceMapped)]
    [InlineData(SourceType.Image, SourceCategory.Logical)]
    [InlineData(SourceType.Video, SourceCategory.Logical)]
    [InlineData(SourceType.Browser, SourceCategory.Logical)]
    [InlineData(SourceType.Text, SourceCategory.Logical)]
    public void Category_MatchesSpecSection6_LogicalVsDeviceMapped(SourceType type, SourceCategory expected)
    {
        var source = new SourceDefinition { Id = "s1", Name = "Test", Type = type };
        Assert.Equal(expected, source.Category);
    }

    [Fact]
    public void ProjectDefinition_DefaultsToCurrentSchemaVersion()
    {
        var project = new ProjectDefinition { ProjectId = "p1", ProjectName = "Test Project" };
        Assert.Equal(ProjectDefinition.CurrentSchemaVersion, project.SchemaVersion);
    }
}
