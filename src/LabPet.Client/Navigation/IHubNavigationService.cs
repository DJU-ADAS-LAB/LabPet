namespace LabPet.Client.Navigation;

/// <summary>
/// 단일 인스턴스 통합 관리 창(HubWindow)의 열기·활성화·최소화 복원 및 페이지 이동을 일원화하는 서비스.
/// </summary>
public interface IHubNavigationService
{
    HubPage CurrentPage { get; }
    NavigationRequest CurrentRequest { get; }
    bool IsHubOpen { get; }
    bool IsHubVisibleAndNotMinimized { get; }

    event EventHandler<NavigationRequest>? Navigated;
    event EventHandler? HubVisibilityChanged;

    Task<bool> NavigateAsync(NavigationRequest request);

    Task<bool> NavigateAsync(HubPage page, string? subSection = null, HubNavigationAction action = HubNavigationAction.None)
        => NavigateAsync(new NavigationRequest(page, SubSection: subSection, Action: action));

    void CloseIfOpen();
}
