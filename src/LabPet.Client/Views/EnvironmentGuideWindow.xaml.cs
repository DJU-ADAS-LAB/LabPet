using System.Windows;
using System.Windows.Input;
using LabPet.Client.Storage;
using LabPet.Client.SystemIntegration;

namespace LabPet.Client.Views;

public partial class EnvironmentGuideWindow : Window
{
    public EnvironmentGuideWindow(IAppDataStore store)
    {
        InitializeComponent();

        FirewallCommandBox.Text = FirewallRegistrationService.BuildPowerShellRegisterCommand(Environment.ProcessPath);

        DataPathText.Text =
            $"모든 펫 프로필(profile.json)과 친구 목록(friends.json)은 설치 폴더가 아닌 '{store.DataDirectory}'에 원자적 저장(.tmp + .bak)으로 보관되어 Velopack 자동 업데이트 후에도 유지됩니다.";

        RefreshFirewallStatus();
    }

    private void RefreshFirewallStatus()
    {
        var status = FirewallRegistrationService.GetRuleStatus();
        FirewallStatusText.Text = status switch
        {
            FirewallRuleStatus.RegisteredHardened =>
                "현재 상태: 보안 강화 방화벽 규칙(로컬 서브넷 · LabPet 전용)이 허용되어 있습니다.",
            FirewallRuleStatus.RegisteredLegacy =>
                "현재 상태: 기존 방화벽 규칙이 등록되어 있습니다. 위 원클릭 버튼으로 보안 강화 규칙(로컬 서브넷 · LabPet 전용)으로 업데이트해 주세요.",
            _ =>
                "현재 상태: 방화벽 규칙이 아직 등록되지 않았습니다. 위 원클릭 버튼으로 허용해 주세요."
        };
    }

    private void AutoRegisterFirewall_Click(object sender, RoutedEventArgs e)
    {
        var ok = FirewallRegistrationService.TryRegisterRulesElevated();
        RefreshFirewallStatus();
        MessageBox.Show(
            ok
                ? "LabPet LAN 방화벽 규칙(UDP 45678~45679, TCP 45680 · 로컬 서브넷 · LabPet 전용)이 자동으로 허용되었습니다."
                : "방화벽 규칙을 등록하지 못했습니다. 관리자 권한(UAC) 허용 여부를 확인해 주세요.",
            "LabPet",
            MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void CopyFirewallCommand_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(FirewallCommandBox.Text);
        MessageBox.Show(
            "방화벽 허용 PowerShell 명령을 클립보드에 복사했습니다.\n관리자 권한 PowerShell에 붙여넣어 실행하세요.",
            "LabPet",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
