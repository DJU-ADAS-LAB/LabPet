using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Formatting;
using LabPet.Client.Games;
using LabPet.Client.Solo;
using LabPet.Shared.Models;

namespace LabPet.Client.ViewModels;

public sealed partial class CoinGamesViewModel
{
    public IReadOnlyList<int> SoloLadderBetOptions => SoloLadderEngine.BetOptions;

    public ObservableCollection<string> SoloLadderRevealedSlotLabels { get; } = ["?", "?", "?", "?"];

    [ObservableProperty] private string _soloLadderStatusText = "시작 위치(1~4번)와 참가 코인을 선택한 뒤 사다리를 시작하세요.";
    [ObservableProperty] private string _soloLadderResultHeadline = string.Empty;
    [ObservableProperty] private string _soloLadderResultDetailText = string.Empty;
    [ObservableProperty] private string _soloLadderLastSpentText = "0 코인";
    [ObservableProperty] private string _soloLadderLastPayoutText = "0 코인";
    [ObservableProperty] private string _soloLadderLastNetText = "0 코인";
    [ObservableProperty] private bool _soloLadderLastNetIsPositive;
    [ObservableProperty] private string _soloLadderLastOutcomeKey = string.Empty;

    private readonly HashSet<string> _settledSoloLadderSessions = new(StringComparer.OrdinalIgnoreCase);
    private SoloLadderPersistedSession? _activeSoloLadderSession;
    private double _soloLadderAnimationProgress;
    private bool _soloLadderCompleting;

    public event EventHandler<SoloLadderPersistedSession>? SoloLadderSessionStarted;
    public event EventHandler? SoloLadderResetRequested;

    public SoloLadderPersistedSession? ActiveSoloLadderSession => _activeSoloLadderSession;
    public double ActiveSoloLadderAnimationProgress => _soloLadderAnimationProgress;

    [RelayCommand]
    private void SelectSoloLadderSlot(object? parameter)
    {
        if (SoloLadderPhase != SoloLadderPhase.Idle || IsSoloLadderRunning)
        {
            return;
        }

        int slot;
        switch (parameter)
        {
            case int intVal:
                slot = intVal;
                break;
            case string strVal when int.TryParse(strVal, out var parsed):
                slot = parsed;
                break;
            default:
                return;
        }

        if (slot is < 0 or >= SoloLadderEngine.SlotCount)
        {
            return;
        }

        SoloLadderSelectedSlot = slot;
        SoloLadderStatusText = $"{slot + 1}번 시작 위치를 선택했습니다! 참가 코인을 정하고 게임을 시작하세요.";
    }

    [RelayCommand]
    private async Task StartSoloLadderAsync()
    {
        if (IsAcceptingIncomingInvite || IsAnyGameBusy || SoloLadderPhase != SoloLadderPhase.Idle)
        {
            return;
        }

        if (!SoloLadderEngine.BetOptions.Contains(SoloLadderBetCoins))
        {
            return;
        }

        if (SoloLadderSelectedSlot is < 0 or >= SoloLadderEngine.SlotCount)
        {
            SoloLadderStatusText = "먼저 1~4번 중 원하는 시작 위치를 선택해 주세요.";
            return;
        }

        var (ok, msg) = _progression.TrySpendCoins(_profile, SoloLadderBetCoins);
        if (!ok)
        {
            SoloLadderStatusText = msg;
            OnEconomyCoinsUpdated();
            return;
        }

        var session = SoloLadderEngine.CreateSession(SoloLadderBetCoins, SoloLadderSelectedSlot);
        _profile.SoloProgress.PendingSoloLadderSession = session;
        NotifyCoinsChanged();

        if (!await PersistProfileAsync())
        {
            _profile.SoloProgress.PendingSoloLadderSession = null;
            _progression.AddCoins(_profile, SoloLadderBetCoins);
            NotifyCoinsChanged();
            SoloLadderStatusText = "게임 상태를 저장하지 못해 참가 코인을 반환했습니다.";
            return;
        }

        _activeSoloLadderSession = session;
        _soloLadderAnimationProgress = 0.0;
        ResetRevealedSoloLabels();
        IsLadderMultiplayerMode = false;
        SoloLadderStatusText = $"{CompactNumberFormatter.FormatExact(SoloLadderBetCoins)} 코인 참가 · {SoloLadderSelectedSlot + 1}번 경로를 따라 내려갑니다...";
        SoloLadderPhase = SoloLadderPhase.Running;
        OnPropertyChanged(nameof(ActiveSoloLadderSession));
        OnPropertyChanged(nameof(ActiveSoloLadderAnimationProgress));
        SoloLadderSessionStarted?.Invoke(this, session);
    }

    partial void RestorePendingSoloLadderSessionIfAny()
    {
        var pending = _profile.SoloProgress.PendingSoloLadderSession;
        if (pending is null)
        {
            return;
        }

        if (!SoloLadderEngine.IsValidSession(pending))
        {
            _profile.SoloProgress.PendingSoloLadderSession = null;
            _ = PersistProfileAsync();
            return;
        }

        var sameSession = string.Equals(_activeSoloLadderSession?.SessionId, pending.SessionId, StringComparison.OrdinalIgnoreCase);
        _activeSoloLadderSession = pending;
        if (!sameSession)
        {
            _soloLadderAnimationProgress = 0.0;
        }

        SoloLadderBetCoins = pending.BetCoins;
        SoloLadderSelectedSlot = pending.SelectedSlot;
        ResetRevealedSoloLabels();
        IsLadderMultiplayerMode = false;
        SoloLadderStatusText = $"진행 중이던 {pending.SelectedSlot + 1}번 경로({CompactNumberFormatter.FormatExact(pending.BetCoins)} 코인)를 이어서 확인합니다.";
        SoloLadderPhase = SoloLadderPhase.Running;
        OnPropertyChanged(nameof(ActiveSoloLadderSession));
        OnPropertyChanged(nameof(ActiveSoloLadderAnimationProgress));
        SoloLadderSessionStarted?.Invoke(this, pending);
    }

    /// <summary>
    /// 창을 닫거나 탭을 이동해도 진행률만 유지하며 강제 정산·리롤을 허용하지 않는다.
    /// </summary>
    public void SaveSoloLadderAnimationProgress(double progress)
    {
        if (_activeSoloLadderSession is not null && SoloLadderPhase == SoloLadderPhase.Running)
        {
            _soloLadderAnimationProgress = Math.Clamp(progress, 0.0, 0.999);
            OnPropertyChanged(nameof(ActiveSoloLadderAnimationProgress));
        }
    }

    public async Task CompleteSoloLadderTraceAsync()
    {
        if (_soloLadderCompleting
            || _activeSoloLadderSession is not { } session
            || SoloLadderPhase != SoloLadderPhase.Running)
        {
            return;
        }

        var persisted = _profile.SoloProgress.PendingSoloLadderSession;
        if (persisted is null || !string.Equals(persisted.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_settledSoloLadderSessions.Count >= RecentInvitationCapacity)
        {
            _settledSoloLadderSessions.Clear();
        }

        if (!_settledSoloLadderSessions.Add(session.SessionId))
        {
            return;
        }

        _soloLadderCompleting = true;
        try
        {
            var (endSlot, _) = SoloLadderEngine.TraceSinglePath(session.Bridges, session.SelectedSlot);
            var mult = session.SlotMultipliersPercent[endSlot];
            var outcomeKey = session.SlotOutcomeKeys[endSlot];
            var payout = SoloLadderEngine.CalculatePayout(session.BetCoins, mult);
            var net = payout - session.BetCoins;

            for (var i = 0; i < SoloLadderEngine.SlotCount; i++)
            {
                var label = SoloLadderEngine.GetOutcomeDisplayLabel(session.SlotOutcomeKeys[i], session.SlotMultipliersPercent[i]);
                if (i < SoloLadderRevealedSlotLabels.Count)
                {
                    SoloLadderRevealedSlotLabels[i] = label;
                }
                else
                {
                    SoloLadderRevealedSlotLabels.Add(label);
                }
            }

            _profile.SoloProgress.PendingSoloLadderSession = null;
            if (payout > 0)
            {
                _progression.AddCoins(_profile, payout);
            }

            NotifyCoinsChanged();
            await PersistProfileAsync();

            SoloLadderLastSpentText = $"{CompactNumberFormatter.FormatExact(session.BetCoins)} 코인";
            SoloLadderLastPayoutText = $"{CompactNumberFormatter.FormatExact(payout)} 코인";
            SoloLadderLastNetIsPositive = net >= 0;
            SoloLadderLastNetText = net > 0
                ? $"+{CompactNumberFormatter.FormatExact(net)} 코인"
                : $"{CompactNumberFormatter.FormatExact(net)} 코인";
            SoloLadderLastOutcomeKey = outcomeKey;

            SoloLadderResultHeadline = outcomeKey switch
            {
                "jackpot" => $"당첨! {endSlot + 1}번 칸 도착 (2.5배 획득)",
                "refund" => $"본전! {endSlot + 1}번 칸 도착 (1배 반환)",
                _ => $"아쉬운 꽝! {endSlot + 1}번 칸 도착"
            };
            SoloLadderResultDetailText = $"출발 {session.SelectedSlot + 1}번 → 도착 {endSlot + 1}번 · {SoloLadderEngine.GetOutcomeDisplayLabel(outcomeKey, mult)}";
            SoloLadderStatusText = $"{SoloLadderResultHeadline} · 획득 {SoloLadderLastPayoutText}";

            _soloLadderAnimationProgress = 1.0;
            SoloLadderPhase = SoloLadderPhase.Result;
            OnPropertyChanged(nameof(ActiveSoloLadderAnimationProgress));

            PetEmotionTriggered?.Invoke(this, outcomeKey != "miss");
            if (outcomeKey == "jackpot")
            {
                GameNotification?.Invoke(this, $"솔로 사다리 당첨! (+{CompactNumberFormatter.FormatExact(payout)} 코인)");
            }
        }
        finally
        {
            _soloLadderCompleting = false;
        }
    }

    [RelayCommand]
    private void ResetSoloLadder()
    {
        if (IsSoloLadderRunning || _soloLadderCompleting)
        {
            return;
        }

        _activeSoloLadderSession = null;
        _soloLadderAnimationProgress = 0.0;
        SoloLadderSelectedSlot = -1;
        ResetRevealedSoloLabels();
        SoloLadderResultHeadline = string.Empty;
        SoloLadderResultDetailText = string.Empty;
        SoloLadderPhase = SoloLadderPhase.Idle;
        SoloLadderStatusText = "시작 위치(1~4번)와 참가 코인을 선택한 뒤 사다리를 시작하세요.";
        OnPropertyChanged(nameof(ActiveSoloLadderSession));
        OnPropertyChanged(nameof(ActiveSoloLadderAnimationProgress));
        SoloLadderResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ResetRevealedSoloLabels()
    {
        for (var i = 0; i < SoloLadderEngine.SlotCount; i++)
        {
            if (i < SoloLadderRevealedSlotLabels.Count)
            {
                SoloLadderRevealedSlotLabels[i] = "?";
            }
            else
            {
                SoloLadderRevealedSlotLabels.Add("?");
            }
        }
    }
}
