namespace LabPet.Client.Views;

/// <summary>친구 행에서 고정 크기로 사용하는 공용 수면 표시.</summary>
public sealed class MiniSleepZzzIndicator() : SleepZzzOverlay(compact: true)
{
    public const double SlotWidth = CompactWidth;
    public const double SlotHeight = CompactHeight;
}
