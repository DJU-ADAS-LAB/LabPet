using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace LabPet.Client.Diagnostics;

/// <summary>
/// 예외 발생 시 %AppData%\LabPet\logs\crash.log에 실제 스택 트레이스와 안전 상태 메타데이터를 기록하는 경량 로컬 로거.
/// 로그 기록 자체가 보조 예외로 전파되지 않도록 실패 안전하게 동작한다.
/// </summary>
internal static class CrashLogWriter
{
    internal const string DefaultFileName = "crash.log";
    internal const string RotatedFileName = "crash.prev.log";
    internal const long DefaultMaxFileBytes = 512 * 1024;

    private static readonly object SyncLock = new();

    public static string DefaultLogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LabPet",
        "logs");

    public static void Write(
        string source,
        Exception exception,
        string? context = null,
        string? logDirectory = null,
        long maxFileBytes = DefaultMaxFileBytes)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            var directory = string.IsNullOrWhiteSpace(logDirectory) ? DefaultLogDirectory : logDirectory;
            var entry = FormatEntry(source, exception, context);
            var effectiveMaxBytes = maxFileBytes > 0 ? maxFileBytes : DefaultMaxFileBytes;

            lock (SyncLock)
            {
                Directory.CreateDirectory(directory);
                var logPath = Path.Combine(directory, DefaultFileName);
                var rotatedPath = Path.Combine(directory, RotatedFileName);

                var info = new FileInfo(logPath);
                if (info.Exists && info.Length >= effectiveMaxBytes)
                {
                    File.Move(logPath, rotatedPath, overwrite: true);
                }

                File.AppendAllText(logPath, entry, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // 로컬 로그 기록 실패가 상위 호출자나 UI 종료를 유발하지 않도록 무시한다.
        }
    }

    internal static string FormatEntry(string source, Exception exception, string? context)
    {
        var nowLocal = DateTimeOffset.Now;
        var nowUtc = nowLocal.ToUniversalTime();
        var assembly = typeof(CrashLogWriter).Assembly;
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        var sb = new StringBuilder();
        sb.AppendLine("========================================");
        sb.Append("Timestamp(Local): ").AppendLine(nowLocal.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture));
        sb.Append("Timestamp(UTC):   ").AppendLine(nowUtc.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        sb.Append("AppVersion:       ").AppendLine(infoVersion);
        sb.Append("Source:           ").AppendLine(string.IsNullOrWhiteSpace(source) ? "Unknown" : source);
        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.Append("Context:          ").AppendLine(context);
        }

        sb.Append("ExceptionType:    ").AppendLine(exception.GetType().FullName ?? exception.GetType().Name);
        sb.Append("Message:          ").AppendLine(exception.Message);
        sb.AppendLine("StackTrace:");
        sb.AppendLine(exception.ToString());
        return sb.ToString();
    }
}
