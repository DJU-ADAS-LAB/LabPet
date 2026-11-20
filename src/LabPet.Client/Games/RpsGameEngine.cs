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
