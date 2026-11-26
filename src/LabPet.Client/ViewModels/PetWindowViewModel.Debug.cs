#if DEBUG
using LabPet.Client.Rendering;
using LabPet.Shared.Models;

namespace LabPet.Client.ViewModels;

public partial class PetWindowViewModel
{
    private bool _debugFriendsEnabled;
    private List<QuickOverlayEntry>? _debugFriends;

    internal void EnableDebugFriends()
    {
        _debugFriendsEnabled = true;
        RefreshQuickOverlayEntries();
    }

    private void AppendDebugFriends(List<QuickOverlayEntry> items, bool isLocalFocusing)
    {
        if (!_debugFriendsEnabled) return;
        _debugFriends ??= PetCustomizationCatalog.Species.Skip(4).Take(5)
            .Select((species, index) =>
            {
                var owner = index == 4 ? "이름이긴테스트친구" : $"테스트{index + 1}";
                var friend = new FriendEntry
                {
                    PetId = $"debug-preview-{species.Id}",
                    OwnerName = owner,
                    PetName = species.DisplayName,
                    CachedAppearance = new PetAppearance
                    {
                        SpeciesId = species.Id,
                        BodyColorHex = species.PreviewHex ?? "#FFF5F7"
                    }
                };
                return new QuickOverlayEntry
                {
                    PetId = friend.PetId, Friend = friend,
                    DisplayTitle = $"{owner} ({friend.PetName})",
                    IsMusicActive = index == 1,
                    IsSleeping = index == 2,
                    IsFocusing = index == 3
                };
            }).ToList();
        foreach (var entry in _debugFriends)
        {
            entry.IsLocalFocusing = isLocalFocusing;
            entry.IsVisitingDesktop = IsVisitorActive(entry.PetId);
            items.Add(entry);
        }
    }
}
#endif
