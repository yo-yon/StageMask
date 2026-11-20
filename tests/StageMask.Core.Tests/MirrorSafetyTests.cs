using StageMask.Core.Geometry;
using StageMask.Core.Mirror;
using Xunit;

namespace StageMask.Core.Tests;

public class MirrorSafetyTests
{
    private static readonly RectD Source = new(0, 0, 1920, 1080);
    private static readonly RectD Other = new(1920, 0, 1920, 1080);

    private static MirrorStatus Ok() => new(true, false, true, Source, Other, false);

    [Fact]
    public void Healthy_IsShown_AndOutlineAllowed()
    {
        Assert.Null(MirrorSafety.BlankReason(Ok()));
        Assert.True(MirrorSafety.CanUseOutlineOnly(Ok(), mirrorOpen: true));
    }

    [Fact]
    public void ClosedMirror_FallsBackToNormalMasks()
        => Assert.False(MirrorSafety.CanUseOutlineOnly(Ok(), mirrorOpen: false));

    [Fact]
    public void CaptureFailure_Blanks()
    {
        var s = Ok() with { CaptureFailed = true };
        Assert.NotNull(MirrorSafety.BlankReason(s));
        Assert.False(MirrorSafety.CanUseOutlineOnly(s, true));
    }

    [Fact]
    public void NoFrameYet_Blanks()
        => Assert.NotNull(MirrorSafety.BlankReason(Ok() with { HasFrame = false }));

    [Fact]
    public void MissingSource_Blanks()
        => Assert.NotNull(MirrorSafety.BlankReason(Ok() with { SourceFound = false }));

    [Fact]
    public void MirrorOnSourceMonitor_Blanks()
    {
        var s = Ok() with { MirrorBounds = new RectD(1800, 100, 800, 600) };
        Assert.NotNull(MirrorSafety.BlankReason(s));
        Assert.False(MirrorSafety.CanUseOutlineOnly(s, true));
    }

    [Fact]
    public void Panic_DisablesOutlineOnly()
        => Assert.False(MirrorSafety.CanUseOutlineOnly(Ok() with { Panic = true }, true));

    [Fact]
    public void ToSourceLocal_OffsetsAndClips()
    {
        var src = new RectD(1920, 0, 1920, 1080);
        Assert.Equal(new RectD(80, 10, 100, 50), MirrorSafety.ToSourceLocal(new RectD(2000, 10, 100, 50), src));
        Assert.Equal(new RectD(0, 0, 100, 50), MirrorSafety.ToSourceLocal(new RectD(1820, 0, 200, 50), src));
        Assert.True(MirrorSafety.ToSourceLocal(new RectD(0, 0, 100, 100), src).IsEmpty);
    }
}
