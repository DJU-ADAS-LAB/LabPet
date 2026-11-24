using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 1:1 게임(가위바위보 · 타이핑 대결) 초대 전송 후 표시하는 전용 대기 화면 상태.
/// </summary>
public sealed partial class OneToOneInviteWaitState : ObservableObject
{
    [ObservableProperty] private string _gameTitle = string.Empty;
    [ObservableProperty] private int _wagerCoins;
    [ObservableProperty] private string _myPetName = "내 펫";
    [ObservableProperty] private ImageSource? _myPetPortrait;
    [ObservableProperty] private string _myReadyStatusText = "준비 완료";
    [ObservableProperty] private string _opponentDisplayName = "상대 펫";
    [ObservableProperty] private ImageSource? _opponentPortrait;
    [ObservableProperty] private string _opponentResponseStatusText = "전송 중";
    [ObservableProperty] private string _statusDetailText = string.Empty;
    [ObservableProperty] private int _remainingSeconds = CoinGamesViewModel.InviteWaitTimeoutSeconds;
    [ObservableProperty] private double _progressRatio = 1.0;
    [ObservableProperty] private bool _isCanceling;
    [ObservableProperty] private bool _canCancel = true;

    public string WagerBadgeText => $"{WagerCoins:N0} 코인";
    public string RemainingTimeText => $"남은 시간 {Math.Max(0, RemainingSeconds)}초";
    public string MyPetInitial => string.IsNullOrWhiteSpace(MyPetName) ? "나" : MyPetName[..1];
    public string OpponentInitial => string.IsNullOrWhiteSpace(OpponentDisplayName) ? "?" : OpponentDisplayName[..1];

    public ICommand? CancelCommand { get; set; }

    partial void OnWagerCoinsChanged(int value) => OnPropertyChanged(nameof(WagerBadgeText));
    partial void OnRemainingSecondsChanged(int value) => OnPropertyChanged(nameof(RemainingTimeText));
    partial void OnMyPetNameChanged(string value) => OnPropertyChanged(nameof(MyPetInitial));
    partial void OnOpponentDisplayNameChanged(string value) => OnPropertyChanged(nameof(OpponentInitial));
}
