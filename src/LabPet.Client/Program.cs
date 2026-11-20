using Velopack;

namespace LabPet.Client;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack 설치·업데이트·제거 훅을 WPF 초기화 전에 최우선 실행
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
