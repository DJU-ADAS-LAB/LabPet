namespace LabPet.Client.Rendering;

/// <summary>타이머나 WPF 객체 없이 투척의 관절 자세와 발사 시점을 계산한다.</summary>
internal readonly record struct SnowballThrowPose(double ArmPitch, double ArmRoll, double Weight)
{
    internal const double PrepareSeconds = 0.4;
    internal const double SwingSeconds = 0.3;
    internal const double ReleaseSeconds = PrepareSeconds + SwingSeconds * 0.55;
    internal const double RecoverSeconds = 0.15;
    internal const double DurationSeconds = PrepareSeconds + SwingSeconds + RecoverSeconds;

    internal static SnowballThrowPose Evaluate(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 || elapsedSeconds >= DurationSeconds)
            return default;

        if (elapsedSeconds < PrepareSeconds)
        {
            var t = Smooth(elapsedSeconds / PrepareSeconds);
            return new(-48 * t, -24 * t, t);
        }

        if (elapsedSeconds < PrepareSeconds + SwingSeconds)
        {
            var t = Smooth((elapsedSeconds - PrepareSeconds) / SwingSeconds);
            return new(-48 + 108 * t, -24 + 36 * t, 1);
        }

        var recovery = 1 - Smooth((elapsedSeconds - PrepareSeconds - SwingSeconds) / RecoverSeconds);
        return new(60 * recovery, 12 * recovery, recovery);
    }

    /// <summary>이전·현재 시간이 발사 시점을 통과했는지 검사한다. 호출자는 이전 시간을 전진시켜 중복 발사를 막는다.</summary>
    internal static bool CrossedRelease(double previousSeconds, double currentSeconds)
        => double.IsFinite(previousSeconds) && double.IsFinite(currentSeconds)
            && previousSeconds >= 0 && previousSeconds < ReleaseSeconds && currentSeconds >= ReleaseSeconds;

    private static double Smooth(double value) => value * value * (3 - 2 * value);
}
