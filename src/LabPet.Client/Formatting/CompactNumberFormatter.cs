using System.Globalization;

namespace LabPet.Client.Formatting;

/// <summary>
/// 큰 숫자를 K/M/B/T 축약 표기 및 쉼표 포함 전체 표기로 변환한다.
/// </summary>
public static class CompactNumberFormatter
{
    private static readonly string[] Suffixes = ["", "K", "M", "B", "T"];
    private static readonly decimal[] Divisors =
    [
        1m,
        1_000m,
        1_000_000m,
        1_000_000_000m,
        1_000_000_000_000m
    ];

    /// <summary>
    /// 숫자를 K/M/B/T 축약 표기로 변환한다. 1,000 미만은 그대로 표시하며 소수 둘째 자리 반올림 시 1000이 되면 다음 단위로 승격한다.
    /// </summary>
    public static string Format(long value)
    {
        var isNegative = value < 0;
        var abs = isNegative ? -(decimal)value : value;

        if (abs < 1_000m)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        var tier = abs switch
        {
            >= 1_000_000_000_000m => 4,
            >= 1_000_000_000m => 3,
            >= 1_000_000m => 2,
            _ => 1
        };

        var rounded = RoundScaled(abs, tier);
        while (rounded >= 1_000m && tier < Suffixes.Length - 1)
        {
            tier++;
            rounded = RoundScaled(abs, tier);
        }

        var prefix = isNegative ? "-" : string.Empty;
        return $"{prefix}{rounded.ToString("0.##", CultureInfo.InvariantCulture)}{Suffixes[tier]}";
    }

    /// <summary>
    /// 천 단위 구분 기호가 포함된 전체 숫자 문자열을 반환한다.
    /// </summary>
    public static string FormatExact(long value)
        => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// 누적 함께한 시간(분)을 UI 요약 카드용 간결한 문자열로 포맷한다.
    /// </summary>
    public static string FormatUptimeDuration(long totalMinutes)
    {
        var safeMinutes = Math.Max(0, totalMinutes);
        if (safeMinutes < 60)
        {
            return $"{safeMinutes}분";
        }

        var hours = safeMinutes / 60;
        var minutes = safeMinutes % 60;
        if (hours >= 1_000)
        {
            return $"{Format(hours)}시간";
        }

        return minutes == 0 ? $"{hours}시간" : $"{hours}시간 {minutes}분";
    }

    /// <summary>
    /// 누적 함께한 시간(분)의 정확한 툴팁 문자열을 반환한다.
    /// </summary>
    public static string FormatExactUptimeTooltip(long totalMinutes)
    {
        var safeMinutes = Math.Max(0, totalMinutes);
        if (safeMinutes < 60)
        {
            return $"{FormatExact(safeMinutes)}분";
        }

        var hours = safeMinutes / 60;
        var minutes = safeMinutes % 60;
        return $"{FormatExact(hours)}시간 {minutes}분 ({FormatExact(safeMinutes)}분)";
    }

    private static decimal RoundScaled(decimal abs, int tier)
        => Math.Round(abs / Divisors[tier], 2, MidpointRounding.AwayFromZero);
}
