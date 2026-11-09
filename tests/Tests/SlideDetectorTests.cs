using OpenCvSharp;
using TalkLens.Slides;
using Xunit;

namespace TalkLens.Tests;

public class SlideDetectorTests
{
    private static Mat Slide(string text, Scalar background)
    {
        var frame = new Mat(720, 1280, MatType.CV_8UC3, background);
        Cv2.PutText(frame, text, new Point(80, 300), HersheyFonts.HersheySimplex, 5, Scalar.Black, 12);
        return frame;
    }

    private static readonly Scalar Paper = new(240, 240, 240);

    [Fact]
    public void First_stable_frame_commits_a_slide_dated_when_it_appeared()
    {
        using var detector = new SlideDetector();
        using var slide = Slide("Intro", Paper);
        Assert.Null(detector.Process(slide, 1000));
        Assert.Equal(1000, detector.Process(slide, 2000));
    }

    [Fact]
    public void Same_slide_does_not_commit_twice()
    {
        using var detector = new SlideDetector();
        using var slide = Slide("Intro", Paper);
        detector.Process(slide, 1000);
        detector.Process(slide, 2000);
        for (var t = 3000; t < 10000; t += 1000) Assert.Null(detector.Process(slide, t));
    }

    [Fact]
    public void New_slide_commits_after_holding_still()
    {
        using var detector = new SlideDetector();
        using var a = Slide("Intro", Paper);
        using var b = Slide("Network", Paper);
        detector.Process(a, 1000);
        detector.Process(a, 2000);
        Assert.Null(detector.Process(b, 3000));
        Assert.Equal(3000, detector.Process(b, 4000));
    }

    [Fact]
    public void Picture_that_keeps_moving_is_not_a_slide()
    {
        using var detector = new SlideDetector();
        using var a = Slide("Intro", Paper);
        detector.Process(a, 1000);
        detector.Process(a, 2000);
        for (var i = 0; i < 6; i++)
        {
            using var moving = Slide("Intro", Paper);
            Cv2.Rectangle(moving, new Rect(i * 200, 100, 200, 500), Scalar.Black, -1);
            Assert.Null(detector.Process(moving, 3000 + i * 1000));
        }
    }

    [Fact]
    public void Blank_signal_is_ignored_and_the_slide_after_it_is_not_repeated()
    {
        using var detector = new SlideDetector();
        using var a = Slide("Intro", Paper);
        using var black = new Mat(720, 1280, MatType.CV_8UC3, Scalar.Black);
        detector.Process(a, 1000);
        detector.Process(a, 2000);
        Assert.Null(detector.Process(black, 3000));
        Assert.Null(detector.Process(black, 4000));
        Assert.Null(detector.Process(a, 5000));
        Assert.Null(detector.Process(a, 6000));
    }
}
