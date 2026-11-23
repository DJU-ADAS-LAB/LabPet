using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabPet.Shared.Protocol;

namespace LabPet.Client.Storage;

internal sealed class StoredDeviceIdentity
{
    public int SchemaVersion { get; set; } = 1;
    public string PetId { get; set; } = string.Empty;
    public string PublicKeyBase64 { get; set; } = string.Empty;
    public string ProtectedPrivateKeyBase64 { get; set; } = string.Empty;
}

/// <summary>
/// %AppData%\LabPet\device_identity.json 에 기기 고유 ECDSA P-256 키쌍을 저장한다.
/// 개인키는 Windows DPAPI(LocalSecretProtector)로 보호되며, 메시지·프로필 파일과 분리 관리된다.
/// </summary>
public sealed class DeviceIdentityStore
{
    private const string IdentityFileName = "device_identity.json";

    private readonly string _filePath;
    private readonly object _lock = new();

    public DeviceIdentityStore(IAppDataStore appDataStore)
        : this(appDataStore.DataDirectory)
    {
    }

    internal DeviceIdentityStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, IdentityFileName);
    }

    public (string PublicKeyBase64, ECDsa PrivateKey) GetOrCreateKeyPair(string petId)
    {
        lock (_lock)
        {
            var existing = TryLoadForPet(petId);
            if (existing is not null)
            {
                return existing.Value;
            }

            var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKeyB64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
            var privateKeyBytes = ecdsa.ExportPkcs8PrivateKey();
            try
            {
                var privateB64 = Convert.ToBase64String(privateKeyBytes);
                var protectedPrivate = LocalSecretProtector.Protect(privateB64);

                var doc = new StoredDeviceIdentity
                {
                    SchemaVersion = 1,
                    PetId = petId,
                    PublicKeyBase64 = publicKeyB64,
                    ProtectedPrivateKeyBase64 = protectedPrivate
                };

                WriteAtomic(doc);
                return (publicKeyB64, ecdsa);
            }
            catch
            {
                ecdsa.Dispose();
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKeyBytes);
            }
        }
    }

    private (string PublicKeyBase64, ECDsa PrivateKey)? TryLoadForPet(string petId)
    {
        foreach (var candidate in new[] { _filePath, _filePath + ".bak" })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(candidate, Encoding.UTF8);
                var stored = JsonSerializer.Deserialize<StoredDeviceIdentity>(json, ProtocolJson.StorageOptions);
                if (stored is null
                    || !string.Equals(stored.PetId, petId, StringComparison.OrdinalIgnoreCase)
                    || !RemoteAuthSigner.IsValidDevicePublicKey(stored.PublicKeyBase64))
                {
                    continue;
                }

                if (!LocalSecretProtector.TryUnprotect(stored.ProtectedPrivateKeyBase64, out var plainPrivB64, out _)
                    || string.IsNullOrWhiteSpace(plainPrivB64))
                {
                    continue;
                }

                var privBytes = Convert.FromBase64String(plainPrivB64);
                var ecdsa = ECDsa.Create();
                try
                {
                    ecdsa.ImportPkcs8PrivateKey(privBytes, out _);
                    return (stored.PublicKeyBase64, ecdsa);
                }
                catch
                {
                    ecdsa.Dispose();
                    throw;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(privBytes);
                }
            }
            catch
            {
                // 본 파일 손상 시 .bak 시도
            }
        }

        return null;
    }

    private void WriteAtomic(StoredDeviceIdentity doc)
    {
        var json = JsonSerializer.Serialize(doc, ProtocolJson.StorageOptions);
        var tmpPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        var bakPath = _filePath + ".bak";
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, bakPath, overwrite: true);
            }

            File.Move(tmpPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }
}
