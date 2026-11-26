using System.Runtime.CompilerServices;
using System.Windows.Media.Media3D;

namespace LabPet.Client.Rendering;

/// <summary>장식 효과는 표시하되 펫 쓰다듬기·드래그 대상으로 취급하지 않는다.</summary>
internal static class PetEffectHitTest
{
    // 공유 Geometry가 아니라 모델 인스턴스를 식별한다. 동결 모델도 변경하지 않으며
    // 해제된 리그를 전역 컬렉션이 붙잡지 않는다. 생성 시에만 등록한다.
    private static readonly ConditionalWeakTable<Model3D, Marker> DecorativeModels = new();
    private sealed class Marker { }

    internal static void MarkDecorative(Model3D model)
    {
        DecorativeModels.GetValue(model, static _ => new Marker());
        if (model is Model3DGroup group)
            foreach (var child in group.Children)
                MarkDecorative(child);
    }

    internal static bool IsDecorative(Model3D model)
        => DecorativeModels.TryGetValue(model, out _);
}
