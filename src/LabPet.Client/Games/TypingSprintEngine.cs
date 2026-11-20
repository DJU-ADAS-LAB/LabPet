namespace LabPet.Client.Games;

/// <summary>
/// 친구 LAN 10초 타이핑 스프린트 대결용 문장 풀 및 점수 계산기.
/// </summary>
public static class TypingSprintEngine
{
    public const string GameTypeId = "typing";
    public const int SprintDurationSeconds = 10;

    public static IReadOnlyList<string> Prompts { get; } =
    [
        "git commit -m fix",
        "dotnet build release",
        "labpet coffee break",
        "today build success",
        "hello desktop pet",
        "clean code fast test",
        "run test zero warning",
        "push main tag deploy",
        "연구실 커피 한잔",
        "오늘도 빌드 성공",
        "말랑한 데스크톱 펫",
        "퇴근 전 커밋 완료"
    ];

    public static string PickPrompt(Random? rng = null, string? exclude = null)
    {
        var r = rng ?? Random.Shared;
        if (Prompts.Count <= 1)
        {
            return Prompts[0];
        }

        string candidate;
        var guard = 0;
        do
        {
            candidate = Prompts[r.Next(Prompts.Count)];
            guard++;
        }
        while (string.Equals(candidate, exclude, StringComparison.Ordinal) && guard < 8);

        return candidate;
    }

    /// <summary>
    /// 제시된 문장과 입력한 문자열이 정확히 일치하면 글자 수 기반 보너스 점수를 계산한다.
    /// </summary>
    public static int EvaluateCompletedPrompt(string prompt, string typed)
    {
        var expected = (prompt ?? string.Empty).Trim();
        var actual = (typed ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(expected) || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return expected.Length;
    }

    /// <summary>
    /// 현재 입력 중인 문장의 앞에서부터 정확히 일치하는 글자 수(진행 보너스)를 계산한다.
    /// </summary>
    public static int CountMatchedPrefixChars(string prompt, string typed)
    {
        if (string.IsNullOrEmpty(prompt) || string.IsNullOrEmpty(typed))
        {
            return 0;
        }

        var len = Math.Min(prompt.Length, typed.Length);
        var matched = 0;
        for (var i = 0; i < len; i++)
        {
            if (char.ToLowerInvariant(prompt[i]) == char.ToLowerInvariant(typed[i]))
            {
                matched++;
            }
            else
            {
                break;
            }
        }

        return matched;
    }
}
