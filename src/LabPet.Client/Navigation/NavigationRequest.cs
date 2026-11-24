using LabPet.Client.ViewModels;

namespace LabPet.Client.Navigation;

/// <summary>
/// 통합 관리 창 내부·외부에서 페이지 이동 및 하위 대상 지정을 요청하는 불변 모델.
/// </summary>
public sealed record NavigationRequest(
    HubPage Page,
    string? SubSection = null,
    GameScreen? TargetGameScreen = null,
    string? SelectedFriendPetId = null,
    HubNavigationAction Action = HubNavigationAction.None,
    bool BypassUnsavedPrompt = false);
