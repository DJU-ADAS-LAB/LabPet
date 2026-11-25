using LabPet.Client.SystemIntegration;
using Velopack;

namespace LabPet.Client;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 외부 PowerShell이나 .ps1 스크립트 없이 독립 콘솔 크리스마스 트리를 즉시 실행하는 전용 진입점
        if (args.Any(a => string.Equals(a, TerminalChristmasTreeService.NativeCliFlag, StringComparison.OrdinalIgnoreCase)))
        {
            TerminalChristmasTreeService.RunNativeConsoleMode(args);
            return;
        }

        // Velopack 설치·업데이트·제거 훅을 WPF 초기화 전에 최우선 실행
        // 다운로드된 패키지의 적용 여부도 업데이트 서비스에서 결정
        // 최초 설치 직후 LAN P2P 방화벽 규칙(UDP 45678~45679, TCP 45680) 자동 등록 시도
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback(_ =>
            {
                // 기본값으로 Windows 시작 시 자동 실행 등록
                new StartupRegistrationService().SetEnabled(true);

                if (!FirewallRegistrationService.AreRulesRegistered())
                {
                    FirewallRegistrationService.TryRegisterRulesElevated();
                }
            })
            .OnBeforeUninstallFastCallback(_ =>
            {
                new StartupRegistrationService().SetEnabled(false);
                FirewallRegistrationService.TryRemoveRulesSilently();
            })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
