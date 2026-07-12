using System;

namespace Quickening.App.Controls;

/// <summary>
/// Chooses the duration to display for a video. Media Foundation reports a
/// garbage NaturalDuration for some fragmented/header-less MP4s (e.g. 2:48:05
/// for a ~10s clip). Once the clip has played to its end, the position observed
/// at MediaEnded is the true duration and wins; before that (observedEnd == 0)
/// there's nothing better than NaturalDuration.
/// </summary>
public static class VideoDuration
{
    public static TimeSpan Best(TimeSpan naturalDuration, TimeSpan observedEnd) =>
        observedEnd > TimeSpan.Zero ? observedEnd : naturalDuration;
}
