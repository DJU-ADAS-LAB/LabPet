using LabPet.Shared.Models;

namespace LabPet.Client.Storage;

/// <summary>
/// %AppData%\LabPet\ 분리 저장소 인터페이스.
/// </summary>
public interface IAppDataStore
{
    /// <summary>실제 데이터가 저장되는 루트 디렉터리 경로 (%AppData%\LabPet).</summary>
    string DataDirectory { get; }

    Task<PetProfile> LoadProfileAsync(CancellationToken cancellationToken = default);
    Task SaveProfileAsync(PetProfile profile, CancellationToken cancellationToken = default);

    Task<bool> RetryGrowthRecoveryAsync(PetProfile profile, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<bool> ResetUnrecoverableGrowthAsync(PetProfile profile, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<FriendsStore> LoadFriendsAsync(CancellationToken cancellationToken = default);
    Task SaveFriendsAsync(FriendsStore friendsStore, CancellationToken cancellationToken = default);
}
