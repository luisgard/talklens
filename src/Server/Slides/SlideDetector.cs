using OpenCvSharp;

namespace TalkLens.Slides;

/// <summary>
/// Decides when the projected slide has changed. A slide commits once the picture differs from the last
/// committed slide and has held still, so transitions and a speaker walking past do not create extra slides.
/// </summary>
public sealed class SlideDetector(double changeRatio = 0.02, double stillRatio = 0.01, int stillFrames = 1) : IDisposable
{
    private const double PixelDelta = 25;
    private const double BlankStdDev = 3;
    private static readonly Size WorkSize = new(320, 180);

    private Mat? _committed;
    private Mat? _previous;
    private long? _changedAt;
    private int _still;

    /// <summary>Feed one BGR frame per second. Returns the talk time of a new slide when one commits.</summary>
    public long? Process(Mat frame, long talkMs)
    {
        var gray = Normalize(frame);
        Cv2.MeanStdDev(gray, out _, out var stdDev);
        var previous = _previous;
        _previous = gray;
        try
        {
            if (stdDev.Val0 < BlankStdDev)
            {
                // A blank signal (projector off, HDMI lost) is not a slide.
                _changedAt = null;
                _still = 0;
                return null;
            }

            if (_committed != null && DiffRatio(gray, _committed) < changeRatio)
            {
                _changedAt = null;
                _still = 0;
                return null;
            }

            _changedAt ??= talkMs;
            _still = previous != null && DiffRatio(gray, previous) < stillRatio ? _still + 1 : 0;
            if (_still < stillFrames) return null;

            _committed?.Dispose();
            _committed = gray.Clone();
            var committedAt = _changedAt;
            _changedAt = null;
            _still = 0;
            return committedAt;
        }
        finally
        {
            previous?.Dispose();
        }
    }

    private static Mat Normalize(Mat frame)
    {
        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        using var small = new Mat();
        Cv2.Resize(gray, small, WorkSize, interpolation: InterpolationFlags.Area);
        var blurred = new Mat();
        Cv2.GaussianBlur(small, blurred, new Size(3, 3), 0);
        return blurred;
    }

    private static double DiffRatio(Mat a, Mat b)
    {
        using var diff = new Mat();
        Cv2.Absdiff(a, b, diff);
        using var mask = new Mat();
        Cv2.Threshold(diff, mask, PixelDelta, 255, ThresholdTypes.Binary);
        return Cv2.CountNonZero(mask) / (double)(mask.Rows * mask.Cols);
    }

    public void Dispose()
    {
        _committed?.Dispose();
        _previous?.Dispose();
    }
}
