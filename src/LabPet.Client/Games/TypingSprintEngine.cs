using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LabPet.Client.Games;

/// <summary>
/// 친구 LAN 10초 타이핑 스프린트 대결용 문장 풀 및 점수 계산기.
/// </summary>
public static class TypingSprintEngine
{
    public const string GameTypeId = "typing";
    public const int SprintDurationSeconds = 10;
    internal const int MaxCompletedPrompts = 64;

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

    internal static int MaxMatchScore => (MaxCompletedPrompts + 1) * Prompts.Max(prompt => prompt.Length);

    internal static string MatchPrompt(string sessionId, int promptIndex)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"LabPet-v3-Typing|{sessionId}|{promptIndex}")));
        return Prompts[(int)(BinaryPrimitives.ReadUInt32BigEndian(digest) % Prompts.Count)];
    }

    internal static string CreateTranscript(IReadOnlyList<string> completed, string prefix)
        => JsonSerializer.Serialize(new Transcript(completed.ToArray(), prefix));

    internal static bool TryScoreTranscript(string sessionId, string? data, out int score)
    {
        score = 0;
        if (data is null || data.Length > 8192)
        {
            return false;
        }
        Transcript? transcript;
        try
        {
            transcript = JsonSerializer.Deserialize<Transcript>(data);
        }
        catch (JsonException)
        {
            return false;
        }
        if (transcript is not { Completed: { Length: <= MaxCompletedPrompts }, Prefix: not null })
        {
            return false;
        }
        var result = 0;
        for (var i = 0; i < transcript.Completed.Length; i++)
        {
            var points = EvaluateCompletedPrompt(MatchPrompt(sessionId, i), transcript.Completed[i]);
            if (points == 0)
            {
                return false;
            }
            result += points;
        }
        var partial = CountMatchedPrefixChars(MatchPrompt(sessionId, transcript.Completed.Length), transcript.Prefix);
        if (partial != transcript.Prefix.Length)
        {
            return false;
        }
        score = result + partial;
        return true;
    }

    private sealed record Transcript(string[] Completed, string Prefix);

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
