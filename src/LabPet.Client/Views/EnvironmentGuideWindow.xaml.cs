using System.Windows;
using System.Windows.Input;
using LabPet.Client.Storage;
using LabPet.Shared.Protocol;

namespace LabPet.Client.Views;

public partial class EnvironmentGuideWindow : Window
{
    public EnvironmentGuideWindow(IAppDataStore store)
    {
        InitializeComponent();

        FirewallCommandBox.Text =
            $"New-NetFirewallRule -DisplayName 'LabPet LAN UDP' -Direction Inbound -Profile Private -Protocol UDP -LocalPort {ProtocolConstants.UdpDiscoveryPort},{ProtocolConstants.UdpMusicFramePort} -Action Allow; " +
            $"New-NetFirewallRule -DisplayName 'LabPet LAN TCP' -Direction Inbound -Profile Private -Protocol TCP -LocalPort {ProtocolConstants.TcpControlPort} -Action Allow";

        DataPathText.Text =
            $"모든 펫 프로필(profile.json)과 친구 목록(friends.json)은 설치 폴더가 아닌 '{store.DataDirectory}'에 원자적 저장(.tmp + .bak)으로 보관되어 Velopack 자동 업데이트 후에도 유지됩니다.";
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
