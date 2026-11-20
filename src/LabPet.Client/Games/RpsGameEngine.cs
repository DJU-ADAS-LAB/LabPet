using System.Security.Cryptography;
using System.Text.Json;

namespace LabPet.Client.Games;

public enum RpsMove
{
    Rock = 0,
    Paper = 1,
    Scissors = 2
}

public enum RpsOutcome
{
    Draw = 0,
    Win = 1,
    Lose = 2
}

/// <summary>
/// 친구 LAN 가위바위보 코인 배틀 판정 및 프로토콜 직렬화 유틸리티.
/// </summary>
public static class RpsGameEngine
{
    public const string GameTypeId = "rps";

    internal static string CreateNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    internal static string Commit(string sessionId, string playerId, RpsMove move, string nonce)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { "LabPet-v3-RPS", sessionId, playerId.ToUpperInvariant(), ToToken(move), nonce })));

    internal static bool IsCommitment(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static bool TryValidateReveal(
        string sessionId, string playerId, string commitment, string? reveal, out RpsMove move)
    {
        move = default;
        if (!IsCommitment(commitment) || reveal is null)
        {
            return false;
        }
        var parts = reveal.Split(':', 2);
        return parts.Length == 2 && parts[1].Length == 32 && parts[1].All(Uri.IsHexDigit)
            && TryParseToken(parts[0], out move)
            && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(commitment),
                Convert.FromHexString(Commit(sessionId, playerId, move, parts[1])));
    }

    public static RpsOutcome Evaluate(RpsMove mine, RpsMove opponent)
    {
        if (mine == opponent)
        {
            return RpsOutcome.Draw;
        }

        return (mine, opponent) switch
        {
            (RpsMove.Rock, RpsMove.Scissors) => RpsOutcome.Win,
            (RpsMove.Scissors, RpsMove.Paper) => RpsOutcome.Win,
            (RpsMove.Paper, RpsMove.Rock) => RpsOutcome.Win,
            _ => RpsOutcome.Lose
        };
    }

    public static string ToToken(RpsMove move) => move switch
    {
        RpsMove.Rock => "rock",
        RpsMove.Paper => "paper",
        RpsMove.Scissors => "scissors",
        _ => "rock"
    };

    public static bool TryParseToken(string? token, out RpsMove move)
    {
        switch ((token ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "rock":
                move = RpsMove.Rock;
                return true;
            case "paper":
                move = RpsMove.Paper;
                return true;
            case "scissors":
                move = RpsMove.Scissors;
                return true;
            default:
                move = RpsMove.Rock;
                return false;
        }
    }

    public static string FormatKorean(RpsMove move) => move switch
    {
        RpsMove.Rock => "✊ 바위",
        RpsMove.Paper => "🖐 보",
        RpsMove.Scissors => "✌️ 가위",
        _ => "✊ 바위"
    };
}
