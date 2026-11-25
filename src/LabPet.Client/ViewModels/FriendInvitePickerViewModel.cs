using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Social;
using LabPet.Shared.Models;
using LabPet.Shared.Transport;

namespace LabPet.Client.ViewModels;

/// <summary>친구 초대 선택 모드.</summary>
public enum FriendInviteSelectionMode
{
    Single,
    Multi
}

/// <summary>친구 초대 입력 스냅샷.</summary>
public sealed record FriendInviteCandidateInput(
    FriendEntry Entry,
    PeerSnapshot? LivePeer,
    ImageSource? Portrait,
    bool IsOnline);

/// <summary>
/// 공용 친구 초대 선택 카드 뷰모델.
/// </summary>
public sealed partial class FriendInviteCandidateCard : ObservableObject
{
    public FriendEntry Entry { get; }
    public string PetId => Entry.PetId;
    public string OwnerName => string.IsNullOrWhiteSpace(Entry.OwnerName) ? "주인 미설정" : Entry.OwnerName;
    public string PetName => string.IsNullOrWhiteSpace(Entry.PetName) ? "이름 없음" : Entry.PetName;
    public string DisplayTitle => SocialCoordinatorService.FormatDisplayName(Entry.OwnerName, Entry.PetName);
    public string PetInitial => string.IsNullOrWhiteSpace(PetName) ? "?" : PetName[..1];
    public bool IsOnline { get; }
    public bool IsSleeping { get; }
    public bool IsMusicActive { get; }
    public bool IsFocusing { get; }
    public bool CanSelect => IsOnline && !IsFocusing;
    public string? CardTooltip => IsFocusing ? FriendOperationErrorCodes.PomodoroFocusMessage : null;
    public string StatusText { get; }
    public string StatusDotHex => !IsOnline ? "#FF9CA3AF" : IsFocusing ? "#FFF59E0B" : IsSleeping ? "#FF6366F1" : "#FF16A34A";
    public ImageSource? Portrait { get; }

    [ObservableProperty] private bool _isSelected;

    public FriendInviteCandidateCard(
        FriendEntry entry,
        PeerSnapshot? livePeer,
        ImageSource? portrait,
        bool isOnline,
        bool isSelected)
    {
        Entry = entry;
        Portrait = portrait;
        IsOnline = isOnline;
        IsSleeping = livePeer?.IsSleeping ?? false;
        IsMusicActive = livePeer?.IsMusicActive ?? false;
        IsFocusing = livePeer?.IsFocusing ?? false;
        StatusText = !isOnline
            ? "오프라인"
            : IsFocusing
                ? "✏️ 집중 중"
                : (IsSleeping, IsMusicActive) switch
                {
                    (true, true) => "온라인 · zzz · 🎵",
                    (true, false) => "온라인 · zzz",
                    (false, true) => "온라인 · 🎵",
                    (false, false) => "온라인"
                };
        _isSelected = isSelected && CanSelect;
    }
}

/// <summary>
/// 가위바위보·타이핑·공동 사다리타기에서 공용으로 사용하는 카드 그리드 친구 선택기 ViewModel.
/// 최초 미선택, PetId 기준 선택 유지, 오프라인 전환 시 해당 선택만 해제(타인 자동 선택 금지) 규칙을 보장한다.
/// </summary>
public sealed partial class FriendInvitePickerViewModel : ObservableObject
{
    private readonly List<FriendInviteCandidateCard> _allCards = [];
    private readonly HashSet<string> _selectedPetIds = new(StringComparer.OrdinalIgnoreCase);

    public FriendInviteSelectionMode SelectionMode { get; }
    public int MaxSelectable { get; }
    public bool IsMultiSelect => SelectionMode == FriendInviteSelectionMode.Multi;

    public ObservableCollection<FriendInviteCandidateCard> FilteredCandidates { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchQuery))]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    private bool _isSelectionLocked;

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool CanInteract => !IsSelectionLocked;
    public bool HasOnlineCandidates => _allCards.Count > 0;
    public bool HasFilteredCandidates => FilteredCandidates.Count > 0;
    public int SelectedCount => _selectedPetIds.Count;
    public bool HasSelection => _selectedPetIds.Count > 0;

    public string SelectionSummaryText
    {
        get
        {
            if (SelectionMode == FriendInviteSelectionMode.Single)
            {
                var first = GetFirstSelectedCard();
                return first is null ? "상대를 선택해 주세요" : $"{first.DisplayTitle} 선택됨";
            }

            return $"선택 {_selectedPetIds.Count}명 / 최대 {MaxSelectable}명";
        }
    }

    public string EmptyStateText => HasOnlineCandidates
        ? "검색 조건에 맞는 온라인 친구가 없습니다."
        : "현재 온라인인 친구가 없습니다. 친구가 접속하면 카드 목록에 표시됩니다.";

    public event EventHandler? SelectionChanged;

    public FriendInvitePickerViewModel(FriendInviteSelectionMode selectionMode, int maxSelectable = 1)
    {
        SelectionMode = selectionMode;
        MaxSelectable = selectionMode == FriendInviteSelectionMode.Single
            ? 1
            : Math.Max(1, maxSelectable);
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
    }

    /// <summary>
    /// 온라인 친구 목록으로 카드를 갱신한다.
    /// 기존 선택 상태는 PetId 기준으로만 유지되며, 오프라인이 된 친구는 선택 해제하되 타인을 임의 선택하지 않는다.
    /// </summary>
    public void UpdateCandidates(IEnumerable<FriendInviteCandidateInput> candidates)
    {
        var inputs = candidates
            .Where(c => c.Entry is not null && !string.IsNullOrWhiteSpace(c.Entry.PetId))
            .ToList();

        var availableIds = new HashSet<string>(
            inputs.Where(c => c.IsOnline && c.LivePeer?.IsFocusing != true).Select(c => c.Entry.PetId),
            StringComparer.OrdinalIgnoreCase);
        var selectionRemoved = _selectedPetIds.RemoveWhere(id => !availableIds.Contains(id)) > 0;

        _allCards.Clear();
        foreach (var item in inputs)
        {
            var selected = _selectedPetIds.Contains(item.Entry.PetId);
            _allCards.Add(new FriendInviteCandidateCard(
                item.Entry,
                item.LivePeer,
                item.Portrait,
                item.IsOnline,
                selected));
        }

        ApplyFilter();
        NotifySelectionState();
        if (selectionRemoved)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public FriendEntry? GetFirstSelectedEntry()
        => GetFirstSelectedCard()?.Entry;

    public FriendInviteCandidateCard? GetFirstSelectedCard()
        => _allCards.FirstOrDefault(c => c.IsSelected);

    public IReadOnlyList<FriendEntry> GetSelectedEntries()
        => _allCards.Where(c => c.IsSelected).Select(c => c.Entry).ToList();

    public IReadOnlyList<string> GetSelectedPetIds()
        => _allCards.Where(c => c.IsSelected).Select(c => c.PetId).ToList();

    public void SetSelectedPetId(string? petId, bool raiseEvent = true)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(petId))
        {
            if (_selectedPetIds.Count > 0)
            {
                _selectedPetIds.Clear();
                changed = true;
            }
        }
        else if (_allCards.Any(c => string.Equals(c.PetId, petId, StringComparison.OrdinalIgnoreCase)))
        {
            if (_selectedPetIds.Count != 1 || !_selectedPetIds.Contains(petId))
            {
                _selectedPetIds.Clear();
                _selectedPetIds.Add(petId);
                changed = true;
            }
        }
        else if (_selectedPetIds.Count > 0)
        {
            _selectedPetIds.Clear();
            changed = true;
        }

        SyncCardFlags();
        NotifySelectionState();
        if (changed && raiseEvent)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetSelection(string petId, bool isSelected, bool raiseEvent = false)
    {
        if (string.IsNullOrWhiteSpace(petId))
        {
            return;
        }

        var changed = false;
        if (isSelected)
        {
            if (SelectionMode == FriendInviteSelectionMode.Single)
            {
                if (_selectedPetIds.Count != 1 || !_selectedPetIds.Contains(petId))
                {
                    _selectedPetIds.Clear();
                    _selectedPetIds.Add(petId);
                    changed = true;
                }
            }
            else if (_selectedPetIds.Count < MaxSelectable)
            {
                changed = _selectedPetIds.Add(petId);
            }
        }
        else
        {
            changed = _selectedPetIds.Remove(petId);
        }

        if (!changed)
        {
            return;
        }

        SyncCardFlags();
        NotifySelectionState();
        if (raiseEvent)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void ToggleCandidate(FriendInviteCandidateCard? card)
    {
        if (card is null || IsSelectionLocked || !card.CanSelect)
        {
            return;
        }

        if (SelectionMode == FriendInviteSelectionMode.Single)
        {
            if (_selectedPetIds.Contains(card.PetId))
            {
                _selectedPetIds.Clear();
            }
            else
            {
                _selectedPetIds.Clear();
                _selectedPetIds.Add(card.PetId);
            }
        }
        else
        {
            if (_selectedPetIds.Contains(card.PetId))
            {
                _selectedPetIds.Remove(card.PetId);
            }
            else if (_selectedPetIds.Count < MaxSelectable)
            {
                _selectedPetIds.Add(card.PetId);
            }
            else
            {
                return;
            }
        }

        SyncCardFlags();
        NotifySelectionState();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SelectAll()
    {
        if (!IsMultiSelect || IsSelectionLocked)
        {
            return;
        }

        var changed = false;
        foreach (var card in FilteredCandidates)
        {
            if (!card.CanSelect)
            {
                continue;
            }

            if (_selectedPetIds.Count >= MaxSelectable)
            {
                break;
            }

            if (_selectedPetIds.Add(card.PetId))
            {
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        SyncCardFlags();
        NotifySelectionState();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        if (IsSelectionLocked || _selectedPetIds.Count == 0)
        {
            return;
        }

        _selectedPetIds.Clear();
        SyncCardFlags();
        NotifySelectionState();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
    }

    private void ApplyFilter()
    {
        var query = (SearchQuery ?? string.Empty).Trim();
        FilteredCandidates.Clear();

        foreach (var card in _allCards)
        {
            if (string.IsNullOrEmpty(query)
                || card.OwnerName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || card.PetName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || card.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredCandidates.Add(card);
            }
        }

        OnPropertyChanged(nameof(HasOnlineCandidates));
        OnPropertyChanged(nameof(HasFilteredCandidates));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    private void SyncCardFlags()
    {
        foreach (var card in _allCards)
        {
            card.IsSelected = _selectedPetIds.Contains(card.PetId);
        }
    }

    private void NotifySelectionState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummaryText));
    }
}
