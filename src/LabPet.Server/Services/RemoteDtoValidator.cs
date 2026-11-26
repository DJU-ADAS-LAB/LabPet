using LabPet.Shared.Models;
using LabPet.Shared.Protocol;

namespace LabPet.Server.Services;

/// <summary>
/// 모든 HTTP/SignalR 원격 입력 DTO의 문자열 길이, 제어문자, 컬렉션 크기, 숫자 범위 및 유한값을 검증한다.
/// </summary>
internal static class RemoteDtoValidator
{
    public const int MaxPetIdLength = 64;
    public const int MaxDisplayTextLength = 32;
    public const int MaxAppVersionLength = 24;
    public const int MaxNonceLength = 128;
    public const int MaxSignatureLength = 256;

    public static bool HasControlChar(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (char.IsControl(ch))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ValidatePetId(string? petId)
    {
        if (string.IsNullOrWhiteSpace(petId))
        {
            return false;
        }

        var trimmed = petId.Trim();
        if (trimmed.Length > MaxPetIdLength)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        return true;
    }

    public static bool ValidateOptionalText(string? value, int maxLength)
        => value is null || (value.Length <= maxLength && !HasControlChar(value));

    public static bool ValidateAuthChallengeRequest(AuthChallengeRequest? req)
        => req is not null
           && ValidatePetId(req.PetId)
           && RemoteAuthSigner.IsValidDevicePublicKey(req.DevicePublicKeyBase64);

    public static bool ValidateAuthTokenRequest(AuthTokenRequest? req)
    {
        if (req is null
            || !ValidatePetId(req.PetId)
            || string.IsNullOrWhiteSpace(req.Nonce)
            || req.Nonce.Length > MaxNonceLength
            || HasControlChar(req.Nonce)
            || !RemoteAuthSigner.IsValidDevicePublicKey(req.DevicePublicKeyBase64)
            || string.IsNullOrWhiteSpace(req.SignatureBase64)
            || req.SignatureBase64.Length > MaxSignatureLength
            || HasControlChar(req.SignatureBase64)
            || !ValidateOptionalText(req.FriendCode, MaxDisplayTextLength)
            || !ValidateOptionalText(req.OwnerName, MaxDisplayTextLength)
            || !ValidateOptionalText(req.PetName, MaxDisplayTextLength))
        {
            return false;
        }

        return true;
    }

    public static bool ValidatePetIdCollection(IReadOnlyCollection<string>? ids, int maxCount)
    {
        if (ids is null)
        {
            return true;
        }

        if (ids.Count > maxCount)
        {
            return false;
        }

        foreach (var id in ids)
        {
            if (!ValidatePetId(id))
            {
                return false;
            }
        }

        return true;
    }

    public static bool ValidatePresenceUpdate(RemotePresenceUpdate? update, int maxDeclaredFriends)
    {
        if (update is null)
        {
            return false;
        }

        if (update.ProtocolVersion is < 1 or > 1000
            || update.Level is < 1 or > 9999
            || !ValidateOptionalText(update.FriendCode, MaxDisplayTextLength)
            || !ValidateOptionalText(update.OwnerName, MaxDisplayTextLength)
            || !ValidateOptionalText(update.PetName, MaxDisplayTextLength)
            || !ValidateOptionalText(update.AppVersion, MaxAppVersionLength))
        {
            return false;
        }

        if (update.Appearance is { } app)
        {
            if (!ValidateOptionalText(app.SpeciesId, MaxDisplayTextLength)
                || !ValidateOptionalText(app.BodyColorHex, 16)
                || !ValidateOptionalText(app.AccentColorHex, 16)
                || !ValidateOptionalText(app.ExpressionId, MaxDisplayTextLength)
                || !ValidateOptionalText(app.HeadAccessoryId, MaxDisplayTextLength)
                || !ValidateOptionalText(app.AuraEffectId, MaxDisplayTextLength))
            {
                return false;
            }
        }

        var collectionCap = Math.Max(maxDeclaredFriends * 2, 32);
        return ValidatePetIdCollection(update.KnownFriendPetIds, collectionCap)
            && ValidatePetIdCollection(update.RemovedFriendPetIds, collectionCap);
    }

    public static bool ValidateRelayControlRequest(RelayControlEnvelopeRequest? request, string authenticatedPetId)
    {
        if (request is null
            || request.Envelope?.Header is null
            || !ValidatePetId(request.TargetPetId)
            || string.Equals(request.TargetPetId.Trim(), authenticatedPetId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var envelope = request.Envelope;
        var header = envelope.Header;
        if (!ValidatePetId(header.SenderPetId)
            || !ValidateOptionalText(header.AppVersion, MaxAppVersionLength)
            || !ValidateOptionalText(header.SignatureBase64, 128)
            || !ValidateOptionalText(envelope.NonceBase64, 64)
            || !ValidateOptionalText(envelope.AuthTagBase64, 64))
        {
            return false;
        }

        return true;
    }

    public static bool ValidateMusicFramePacket(MusicFramePacket? packet, string authenticatedPetId)
    {
        if (packet is null
            || !string.Equals(packet.SenderPetId, authenticatedPetId, StringComparison.OrdinalIgnoreCase)
            || ProtocolConstants.EvaluateCompatibility(packet.ProtocolVersion) != ProtocolCompatibility.Compatible
            || string.IsNullOrWhiteSpace(packet.SignatureBase64)
            || packet.SignatureBase64.Length > 88
            || HasControlChar(packet.SignatureBase64)
            || packet.SubscriptionId is null
            || packet.SubscriptionId.Length > 64
            || HasControlChar(packet.SubscriptionId)
            || !ValidateOptionalText(packet.TargetPetId, MaxPetIdLength)
            || !float.IsFinite(packet.Bass) || packet.Bass is < 0f or > 10f
            || !float.IsFinite(packet.Mid) || packet.Mid is < 0f or > 10f
            || !float.IsFinite(packet.Treble) || packet.Treble is < 0f or > 10f)
        {
            return false;
        }

        return packet.TargetPetId is null || ValidatePetId(packet.TargetPetId);
    }
}
