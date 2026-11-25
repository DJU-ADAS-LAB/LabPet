using System.Runtime.InteropServices;
using System.Text;

namespace LabPet.Client.Storage;

/// <summary>
/// Windows DPAPI(CryptProtectData / CryptUnprotectData, 현재 사용자 계정 전용)를 사용해
/// 로컬 JSON 저장소(friends.json)에 보관되는 친구 공유 비밀키(PairSecretBase64)를 암호화·복호화한다.
/// </summary>
public static class LocalSecretProtector
{
    public const string ProtectedPrefix = "dpapi:v1:";

    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] AdditionalEntropy = Encoding.UTF8.GetBytes("LabPet.FriendPairSecret.v1");

    public static bool IsProtected(string? value)
        => !string.IsNullOrEmpty(value) &&
           value.StartsWith(ProtectedPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 평문 비밀키를 현재 Windows 사용자 계정 전용 DPAPI 암호문("dpapi:v1:...")으로 변환한다.
    /// 이미 보호된 값이거나 빈 문자열이면 그대로 반환한다.
    /// </summary>
    public static string Protect(string? plaintextSecret)
    {
        if (string.IsNullOrWhiteSpace(plaintextSecret) || IsProtected(plaintextSecret))
        {
            return plaintextSecret ?? string.Empty;
        }

        var plainBytes = Encoding.UTF8.GetBytes(plaintextSecret);
        var protectedBytes = ProtectBytes(plainBytes, AdditionalEntropy, "LabPet Friend Secret");
        return ProtectedPrefix + Convert.ToBase64String(protectedBytes);
    }

    /// <summary>
    /// 저장된 비밀키 값을 복호화한다.
    /// 구버전 평문 값인 경우 wasPlaintext=true로 그대로 반환해 호출자가 DPAPI 형식으로 자동 마이그레이션할 수 있게 한다.
    /// </summary>
    public static bool TryUnprotect(string? storedValue, out string plaintextSecret, out bool wasPlaintext)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            plaintextSecret = string.Empty;
            wasPlaintext = false;
            return true;
        }

        if (!IsProtected(storedValue))
        {
            plaintextSecret = storedValue;
            wasPlaintext = true;
            return true;
        }

        wasPlaintext = false;
        try
        {
            var base64 = storedValue[ProtectedPrefix.Length..];
            var protectedBytes = Convert.FromBase64String(base64);
            var plainBytes = UnprotectBytes(protectedBytes, AdditionalEntropy);
            plaintextSecret = Encoding.UTF8.GetString(plainBytes);
            return true;
        }
        catch
        {
            plaintextSecret = string.Empty;
            return false;
        }
    }

    internal static byte[] ProtectBytes(byte[] plainBytes, byte[] entropy, string description)
    {
        var inputBlob = CreateBlob(plainBytes);
        var entropyBlob = CreateBlob(entropy);
        var outputBlob = default(DataBlob);

        try
        {
            if (!CryptProtectData(
                ref inputBlob,
                szDataDescr: description,
                ref entropyBlob,
                pvReserved: IntPtr.Zero,
                pPromptStruct: IntPtr.Zero,
                dwFlags: CryptProtectUiForbidden,
                out outputBlob))
            {
                throw new InvalidOperationException($"DPAPI 암호화에 실패했습니다 (Win32={Marshal.GetLastWin32Error()}).");
            }

            var result = new byte[outputBlob.cbData];
            Marshal.Copy(outputBlob.pbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            FreeInputBlob(ref inputBlob);
            FreeInputBlob(ref entropyBlob);
            if (outputBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outputBlob.pbData);
            }
        }
    }

    internal static byte[] UnprotectBytes(byte[] protectedBytes, byte[] entropy)
    {
        var inputBlob = CreateBlob(protectedBytes);
        var entropyBlob = CreateBlob(entropy);
        var outputBlob = default(DataBlob);

        try
        {
            if (!CryptUnprotectData(
                ref inputBlob,
                ppszDataDescr: IntPtr.Zero,
                ref entropyBlob,
                pvReserved: IntPtr.Zero,
                pPromptStruct: IntPtr.Zero,
                dwFlags: CryptProtectUiForbidden,
                out outputBlob))
            {
                throw new InvalidOperationException($"DPAPI 복호화에 실패했습니다 (Win32={Marshal.GetLastWin32Error()}).");
            }

            var result = new byte[outputBlob.cbData];
            Marshal.Copy(outputBlob.pbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            FreeInputBlob(ref inputBlob);
            FreeInputBlob(ref entropyBlob);
            if (outputBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outputBlob.pbData);
            }
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DataBlob
        {
            cbData = data.Length,
            pbData = ptr
        };
    }

    private static void FreeInputBlob(ref DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.pbData);
            blob.pbData = IntPtr.Zero;
            blob.cbData = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
