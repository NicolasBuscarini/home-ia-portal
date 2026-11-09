using GarageWatch.Infrastructure.Capture;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class ClipTriggerEpochTests
{
    [Theory]
    [InlineData("local", null, "epoch-1", true)]
    [InlineData("local", "epoch-1", "epoch-2", true)]
    [InlineData("remote", "epoch-1", "epoch-1", true)]
    [InlineData("remote", "epoch-1", "epoch-2", false)]
    [InlineData("remote", null, "epoch-1", false)]
    [InlineData("remote", "", "epoch-1", false)]
    public void Segment_is_only_used_when_it_belongs_to_the_active_capture_epoch(
        string authority, string? activeEpoch, string segmentEpoch, bool expected)
    {
        Assert.Equal(expected,
            ClipTriggerWorker.IsSegmentFromActiveCapture(authority, activeEpoch, segmentEpoch));
    }
}
