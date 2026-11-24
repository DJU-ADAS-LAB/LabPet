using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LabPet.Shared.Models;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 허브 창 최상위 영역 전환 구분.
/// </summary>
public enum SoloHubSection
{
    GrowthAudio = 0,
    Pomodoro = 1,
    Games = 2
}

/// <summary>
/// 게임 허브 내부 화면 구분.
/// </summary>
public enum GameScreen
{
    Dashboard = 0,
    HiLow = 1,
    Rps = 2,
    Typing = 3,
    Ladder = 4
}

/// <summary>
/// 수신된 게임 초대의 수명주기 상태.
/// </summary>
public enum GameInviteStatus
{
    None = 0,
    Pending = 1,
    Accepting = 2,
    Accepted = 3,
    Declined = 4,
    Expired = 5,
    Sending = 6,
    Ready = 7,
    Starting = 8,
    Canceled = 9,
    Failed = 10
}

/// <summary>
/// 하이로우 게임 화면 단계.
/// </summary>
public enum HiLowPhase
{
    Idle = 0,
    Playing = 1,
    Result = 2
}

/// <summary>
/// 하이로우 게임 종료 결과 구분.
/// </summary>
public enum HiLowResultKind
{
    None = 0,
    CashedOut = 1,
    Lost = 2
}

/// <summary>
/// LAN 가위바위보 게임 단계.
/// </summary>
public enum RpsPhase
{
    Setup = 0,
    WaitingInviteResponse = 1,
    Choosing = 2,
    WaitingOpponent = 3,
    Result = 4
}

/// <summary>
/// 타이핑 대결 게임 단계.
/// </summary>
public enum TypingPhase
{
    Setup = 0,
    WaitingInviteResponse = 1,
    Countdown = 2,
    Running = 3,
    WaitingOpponent = 4,
    Result = 5
}

/// <summary>
/// 연구실 사다리 게임 5단계 화면 상태.
/// </summary>
public enum LadderStage
{
    Setup = 0,
    Lobby = 1,
    Picking = 2,
    Running = 3,
    Result = 4
}

/// <summary>
/// 솔로 사다리 4칸 미니게임 진행 단계.
/// </summary>
public enum SoloLadderPhase
{
    Idle = 0,
    Running = 1,
    Result = 2
}

/// <summary>
/// 게임 대시보드 카드 메타 정보.
/// </summary>
public sealed record GameCardDescriptor(
    GameScreen Screen,
    string Title,
    string Description,
    string ModeBadge,
    string CostBadge,
    string IconResourceKey);

/// <summary>
/// 사다리 설정 화면에서 초대할 온라인 친구 직접 선택 항목.
/// </summary>
public sealed partial class LadderFriendInviteOption : ObservableObject
{
    public FriendEntry Friend { get; }
    public string DisplayName { get; }
    public ImageSource? Portrait { get; }

    [ObservableProperty] private bool _isSelected = true;

    public LadderFriendInviteOption(FriendEntry friend, string displayName, ImageSource? portrait, bool isSelected = true)
    {
        Friend = friend;
        DisplayName = displayName;
        Portrait = portrait;
        _isSelected = isSelected;
    }
}

/// <summary>
/// 사다리 상품 설정 행 모델(이름 + 수량).
/// </summary>
public sealed partial class LadderPrizeRowViewModel : ObservableObject
{
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private int _quantity = 1;
}

/// <summary>
/// 사다리 대기실·결과 화면의 참가자 카드 항목.
/// </summary>
public sealed record LadderParticipantCardItem(
    string PetId,
    string DisplayName,
    ImageSource? Portrait,
    bool IsHost,
    bool IsMe,
    bool IsPending,
    string StatusBadge,
    int? SelectedSlot,
    string PrizeText,
    string AccentColorHex,
    LabPet.Client.Games.LadderParticipantStatus Status = LabPet.Client.Games.LadderParticipantStatus.Joined,
    bool CanCancelInvite = false,
    string StatusTextColorHex = "#FF15803D",
    string StatusBgColorHex = "#FFECFDF5")
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Brush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    public string SlotText => SelectedSlot is int slot ? $"{slot + 1}번 위치" : StatusBadge;
    public Brush StatusTextBrush => ResolveBrush(StatusTextColorHex, Colors.SeaGreen);
    public Brush StatusBgBrush => ResolveBrush(StatusBgColorHex, Color.FromRgb(236, 253, 245));

    private static Brush ResolveBrush(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            var fb = new SolidColorBrush(fallback);
            fb.Freeze();
            return fb;
        }

        return BrushCache.GetOrAdd(hex, static raw =>
        {
            try
            {
                if (ColorConverter.ConvertFromString(raw) is Color c)
                {
                    var b = new SolidColorBrush(c);
                    b.Freeze();
                    return b;
                }
            }
            catch (FormatException)
            {
            }

            var defaultBrush = new SolidColorBrush(Colors.Gray);
            defaultBrush.Freeze();
            return defaultBrush;
        });
    }
}
