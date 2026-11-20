using System.Windows;
using LabPet.Shared.Protocol;

namespace LabPet.Client.Views;

public partial class PairingCodeWindow : Window
{
    public PairingCodeWindow(string? code = null)
    {
        InitializeComponent();
        CodeInput.MaxLength = PacketCrypto.PairingCodeLength;
        if (code is not null)
        {
            Instructions.Text = "3분 안에 이 코드를 직접 또는 신뢰하는 비공개 채널로 친구에게 전달하세요.";
            CodeInput.Text = code;
            CodeInput.IsReadOnly = true;
            CancelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            Instructions.Text = "친구에게 직접 받은 32자리 확인 코드를 입력하세요.";
        }
        Loaded += (_, _) =>
        {
            CodeInput.Focus();
            CodeInput.SelectAll();
        };
    }

    public string VerificationCode => CodeInput.Text.Trim();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (PacketCrypto.IsPairingCode(VerificationCode))
        {
            DialogResult = true;
        }
        else
        {
            Instructions.Text = "확인 코드는 숫자와 A~F로 이루어진 32자리입니다.";
            CodeInput.Focus();
            CodeInput.SelectAll();
        }
    }
}
