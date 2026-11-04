using GarageWatch.Infrastructure.Capture;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class ClipTriggerOptionsTests
{
    [Fact]
    public void Trigger_is_enabled_by_default_and_can_be_disabled_by_configuration()
    {
        var enabled = ClipTriggerOptions.FromEnvironment(new ConfigurationBuilder().Build());
        Assert.True(enabled.Enabled);

        var disabled = ClipTriggerOptions.FromEnvironment(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CLIP_TRIGGER_ENABLED"] = "false" })
            .Build());
        Assert.False(disabled.Enabled);
    }
}
