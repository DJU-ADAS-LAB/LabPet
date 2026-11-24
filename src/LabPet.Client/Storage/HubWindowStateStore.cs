using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using LabPet.Shared.Protocol;

namespace LabPet.Client.Storage;

/// <summary>
/// %AppData%\LabPet\hub-ui.json 에 저장되는 통합 관리 창(HubWindow) 레이아웃 상태.
/// </summary>
public sealed class HubWindowState
{
    public const double DefaultWidth = 1080.0;
    public const double DefaultHeight = 720.0;
    public const double MinWidth = 840.0;
    public const double MinHeight = 580.0;

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = DefaultWidth;
    public double Height { get; set; } = DefaultHeight;
    public bool IsMaximized { get; set; }
    public bool IsSidebarCollapsed { get; set; }

    public HubWindowState Normalize(Rect? virtualScreenBounds = null)
    {
        var width = double.IsFinite(Width) && Width >= MinWidth ? Width : DefaultWidth;
        var height = double.IsFinite(Height) && Height >= MinHeight ? Height : DefaultHeight;

        double? left = Left is double l && double.IsFinite(l) ? l : null;
        double? top = Top is double t && double.IsFinite(t) ? t : null;

        if (left is double x && top is double y)
        {
            var bounds = virtualScreenBounds ?? new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);

            if (bounds.Width > 0 && bounds.Height > 0)
            {
                // 창 제목표시줄(최소 120x48)이 화면 밖에 완전히 숨지 않도록 보정
                var clampedWidth = Math.Min(width, Math.Max(MinWidth, bounds.Width));
                var clampedHeight = Math.Min(height, Math.Max(MinHeight, bounds.Height));
                width = clampedWidth;
                height = clampedHeight;

                var maxLeft = Math.Max(bounds.Left, bounds.Right - 120.0);
                var maxTop = Math.Max(bounds.Top, bounds.Bottom - 80.0);
                left = Math.Clamp(x, bounds.Left - width + 120.0, maxLeft);
                top = Math.Clamp(y, bounds.Top, maxTop);
            }
        }

        return new HubWindowState
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            IsMaximized = IsMaximized,
            IsSidebarCollapsed = IsSidebarCollapsed
        };
    }
}

/// <summary>
/// %AppData%\LabPet\hub-ui.json 을 .tmp -> .bak -> 교체 패턴으로 원자적 저장/로드한다.
/// </summary>
public sealed class HubWindowStateStore
{
    private const string FileName = "hub-ui.json";
    private readonly string _filePath;
    private readonly object _ioLock = new();

    public string FilePath => _filePath;

    public HubWindowStateStore(IAppDataStore appDataStore)
        : this(appDataStore.DataDirectory)
    {
    }

    internal HubWindowStateStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("데이터 디렉터리 경로가 비어 있습니다.", nameof(dataDirectory));
        }

        _filePath = Path.Combine(dataDirectory, FileName);
    }

    public HubWindowState Load(Rect? virtualScreenBounds = null)
    {
        lock (_ioLock)
        {
            var bakPath = _filePath + ".bak";
            var loaded = TryDeserialize(_filePath) ?? TryDeserialize(bakPath) ?? new HubWindowState();
            return loaded.Normalize(virtualScreenBounds);
        }
    }

    public void Save(HubWindowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = state.Normalize();

        lock (_ioLock)
        {
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);

            var tmpPath = _filePath + ".tmp";
            var bakPath = _filePath + ".bak";
            var json = JsonSerializer.Serialize(normalized, ProtocolJson.StorageOptions);

            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, bakPath, overwrite: true);
            }

            File.Move(tmpPath, _filePath, overwrite: true);
        }
    }

    private static HubWindowState? TryDeserialize(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<HubWindowState>(json, ProtocolJson.StorageOptions);
        }
        catch
        {
            return null;
        }
    }
}
