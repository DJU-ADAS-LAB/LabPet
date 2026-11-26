using System;
using System.Windows.Threading;

namespace LabPet.Client.Views;

public partial class PetWindow
{
    private DispatcherTimer? _overlayOpenTimer;

    private void SchedulePetOverlayOpen()
    {
        if (_overlayOpenTimer is null)
        {
            _overlayOpenTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _overlayOpenTimer.Tick += OnOverlayOpenTick;
            PetViewport.MouseLeave += (_, _) => _overlayOpenTimer.Stop();
            IsVisibleChanged += (_, _) =>
            {
                if (!IsVisible) _overlayOpenTimer.Stop();
            };
            Closed += (_, _) =>
            {
                _overlayOpenTimer.Stop();
                _overlayOpenTimer.Tick -= OnOverlayOpenTick;
            };
        }

        _overlayOpenTimer.Stop();
        if (!_isClosingOrClosed && !FriendOverlayPopup.IsOpen)
            _overlayOpenTimer.Start();
    }

    private void OnOverlayOpenTick(object? sender, EventArgs e)
    {
        _overlayOpenTimer?.Stop();
        if (_isClosingOrClosed || !IsVisible || !PetViewport.IsMouseOver
            || _isDraggingWindow || _isPointerPressed)
            return;

        _viewModel.RefreshPomodoroOverlayState();
        _viewModel.RefreshQuickOverlayEntries();
        FriendOverlayPopup.IsOpen = true;
        SetPetControlsVisible(true);
    }
}
