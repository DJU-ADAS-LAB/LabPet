using System.Windows;
using System.Windows.Input;

namespace LabPet.Client.Views;

public partial class UpdatePromptWindow : Window
{
    public UpdatePromptWindow(string versionText)
    {
        InitializeComponent();
        TitleVersionText.Text = $"LabPet v{versionText} 업데이트";
        MessageBodyText.Text = $"새 버전(v{versionText}) 다운로드가 완료되었습니다.\n지금 바로 재시작하여 업데이트를 적용할까요?";
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void UpdateNowButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
