using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using LabPet.Client.Interop;

namespace LabPet.Client.Views;

/// <summary>
/// 수신 순서를 기준으로 현재 열린 방문 펫 팝업의 HWND 표시 순서를 관리한다.
/// </summary>
internal sealed class VisitorPopupOrder
{
    private readonly Dictionary<Popup, long> _bubbles = [];
    private Popup? _composer;
    private long _lastReceivedOrder;

    internal long NextReceivedOrder() => ++_lastReceivedOrder;

    internal void UpdateBubble(Popup bubble, long receivedOrder)
    {
        if (bubble.IsOpen)
        {
            _bubbles[bubble] = receivedOrder;
            ApplyOrder();
        }
    }

    internal void RemoveBubble(Popup bubble) => _bubbles.Remove(bubble);

    internal void OpenComposer(Popup composer)
    {
        _composer = composer;
        ApplyOrder();
    }

    internal void CloseComposer(Popup composer)
    {
        if (ReferenceEquals(_composer, composer))
        {
            _composer = null;
        }
    }

    private void ApplyOrder()
    {
        // WPF 팝업 위치·크기 변경은 기존 Z-order를 유지하므로, 수신과 열기 시에만 순서를 맞춘다.
        if (_composer is not null && !IsPopupOpenWithValidHandle(_composer))
        {
            _composer = null;
        }

        var higher = GetOpenHandle(_composer);
        List<Popup>? staleBubbles = null;
        foreach (var (popup, _) in _bubbles.OrderByDescending(pair => pair.Value))
        {
            var handle = GetOpenHandle(popup);
            if (handle == IntPtr.Zero)
            {
                if (!popup.IsOpen)
                {
                    staleBubbles ??= [];
                    staleBubbles.Add(popup);
                }

                continue;
            }

            if (higher != IntPtr.Zero && !Win32WindowHelper.IsWindowAbove(higher, handle))
            {
                Win32WindowHelper.PlacePopupBehind(handle, higher);
            }
            higher = handle;
        }

        if (staleBubbles is not null)
        {
            foreach (var stale in staleBubbles)
            {
                _bubbles.Remove(stale);
            }
        }
    }

    private static bool IsPopupOpenWithValidHandle(Popup? popup)
        => GetOpenHandle(popup) != IntPtr.Zero || popup is { IsOpen: true };

    private static IntPtr GetOpenHandle(Popup? popup)
        => popup is { IsOpen: true, Child: not null }
            && PresentationSource.FromVisual(popup.Child) is HwndSource { IsDisposed: false, Handle: var handle }
            && Win32WindowHelper.IsValidWindowHandle(handle)
            ? handle
            : IntPtr.Zero;
}
