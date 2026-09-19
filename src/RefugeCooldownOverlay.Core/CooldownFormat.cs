using System.Globalization;

namespace RefugeCooldownOverlay.Core;

/// <summary>Countdown text formatting, shared by the overlay and offline tests.</summary>
public static class CooldownFormat
{
    /// <summary>
    /// Compact remaining-time text. Values under two minutes stay numeric so
    /// 100–119 s never collapse into "1m"; minutes format only at 120 s and over.
    /// </summary>
    public static string Seconds(uint remainingMilliseconds)
    {
        if (remainingMilliseconds >= 120_000)
        {
            var minutes = remainingMilliseconds / 60_000;
            var seconds = remainingMilliseconds % 60_000 / 1000;
            return $"{minutes.ToString(CultureInfo.InvariantCulture)}m{seconds.ToString("00", CultureInfo.InvariantCulture)}";
        }

        var asSeconds = remainingMilliseconds / 1000.0;
        return asSeconds >= 10
            ? asSeconds.ToString("F0", CultureInfo.InvariantCulture)
            : asSeconds.ToString("F1", CultureInfo.InvariantCulture);
    }
}
