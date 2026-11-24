using LabPet.Client.Navigation;

namespace LabPet.Client.Navigation;

/// <summary>
/// 통합 관리 창 페이지 UserControl의 활성화·비활성화 생명주기 인터페이스.
/// 비활성화 및 창 최소화·종료 시 UI 타이머와 3D 미리보기를 중지하는 데 사용한다.
/// </summary>
public interface IHubPageLifecycle
{
    void OnActivated(NavigationRequest request);
    void OnDeactivated();
}

/// <summary>
/// 저장되지 않은 편집 초안이 있는 페이지가 이탈·창 종료 전 확인을 요청할 수 있도록 하는 가드.
/// </summary>
public interface IHubUnsavedChangesGuard
{
    bool HasUnsavedChanges { get; }
    Task<bool> ConfirmLeaveAsync(bool isClosingWindow);
}
