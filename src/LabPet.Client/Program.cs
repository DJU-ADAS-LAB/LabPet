using LabPet.Client.SystemIntegration;
using Velopack;

namespace LabPet.Client;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack 설치·업데이트·제거 훅을 WPF 초기화 전에 최우선 실행
        // 최초 설치 직후 LAN P2P 방화벽 규칙(UDP 45678~45679, TCP 45680) 자동 등록 시도
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ =>
            {
                if (!FirewallRegistrationService.AreRulesRegistered())
                {
                    FirewallRegistrationService.TryRegisterRulesElevated();
                }
            })
            .OnBeforeUninstallFastCallback(_ =>
            {
                FirewallRegistrationService.TryRemoveRulesSilently();
            })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
