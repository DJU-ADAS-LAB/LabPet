using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.SystemIntegration;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 시즌 콘텐츠 항목 메타데이터 모델. 추후 신규 시즌 콘텐츠 추가 시 확장 가능하도록 데이터 모델로 관리한다.
/// </summary>
public sealed record SeasonalContentItem(
    string Id,
    string Title,
    string SeasonBadge,
    string Description,
    string ActionLabel);

/// <summary>
/// 통합 창 시즌 콘텐츠 페이지 ViewModel.
/// 실제 구현된 기능만 목록에 노출하며, 실행 상태와 안내 문구를 관리한다.
/// </summary>
public sealed partial class SeasonalPageViewModel : ObservableObject
{
    private const string ChristmasTreeId = "christmas-tree";

    private readonly PetWindowViewModel _petVm;
    private readonly TerminalChristmasTreeService _christmasTreeService;

    public IReadOnlyList<SeasonalContentItem> Items { get; } =
    [
        new(
            ChristmasTreeId,
            "터미널 크리스마스 트리",
            "윈터 시즌",
            "별도의 콘솔 창에 반짝이는 ASCII 크리스마스 트리와 내 펫 이름을 출력합니다.",
            "크리스마스 트리 실행")
    ];

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    public SeasonalPageViewModel(
        PetWindowViewModel petVm,
        TerminalChristmasTreeService christmasTreeService)
    {
        _petVm = petVm;
        _christmasTreeService = christmasTreeService;
    }

    [RelayCommand]
    private void LaunchItem(SeasonalContentItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (string.Equals(item.Id, ChristmasTreeId, StringComparison.Ordinal))
        {
            var (ok, message) = _christmasTreeService.Launch(_petVm.CurrentProfile.PetName);
            HasError = !ok;
            StatusMessage = message;
            _petVm.ShowBubble(message, ok ? 3000 : 2600);
        }
    }
}
