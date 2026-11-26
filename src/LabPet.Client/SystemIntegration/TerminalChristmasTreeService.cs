using System.Diagnostics;
using System.IO;
using System.Text;
using LabPet.Client.Storage;

namespace LabPet.Client.SystemIntegration;

/// <summary>
/// 바탕화면 펫/트레이 우클릭 메뉴에서 소형 고정폭 글꼴(11px) 기반 고밀도 창(164x48 셀 / 164x90 픽셀)으로
/// Unicode Half-Block(▀) 픽셀 버퍼, 4x SSAA 정적 캐시, ACES Filmic 톤매핑,
/// 오로라 캐싱·다항 블룸·제로-할당 UTF-8 차분 스트리밍을 지원하는 크리스마스 트리 애니메이션 서비스.
/// 생성되는 스크립트는 %AppData%\LabPet\scripts\ 에만 저장하며 설치 폴더에는 기록하지 않는다.
/// </summary>
public sealed class TerminalChristmasTreeService
{
    public const string ScriptsDirectoryName = "scripts";
    public const string ScriptFileName = "christmas_tree.ps1";
    public const string NativeCliFlag = "--christmas-tree";
    public const string DefaultPetName = "LabPet";
    public const int MaxPetNameLength = 32;
    public const int DefaultWindowColumns = 164;
    public const int DefaultWindowRows = 48;
    public const short DefaultFontPixelHeight = 11;

    private readonly string _dataDirectory;

    public TerminalChristmasTreeService(IAppDataStore store)
        : this(store.DataDirectory)
    {
    }

    internal TerminalChristmasTreeService(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("데이터 디렉터리 경로가 비어 있습니다.", nameof(dataDirectory));
        }

        _dataDirectory = dataDirectory;
    }

    /// <summary>
    /// %AppData%\LabPet\scripts\christmas_tree.ps1 전체 경로.
    /// </summary>
    public string ScriptFilePath => Path.Combine(_dataDirectory, ScriptsDirectoryName, ScriptFileName);

    /// <summary>
    /// 펫 이름에서 제어문자(개행·탭·ANSI ESC 등)를 제거하고 길이를 제한해 터미널 출력/인자 전달에 안전한 문자열로 정제한다.
    /// </summary>
    public static string SanitizePetName(string? petName)
    {
        if (string.IsNullOrWhiteSpace(petName))
        {
            return DefaultPetName;
        }

        var sb = new StringBuilder(Math.Min(petName.Length, MaxPetNameLength));
        foreach (var ch in petName)
        {
            if (char.IsControl(ch))
            {
                continue;
            }

            sb.Append(ch);
        }

        var cleaned = sb.ToString().Trim();
        if (cleaned.Length == 0)
        {
            return DefaultPetName;
        }

        return cleaned.Length <= MaxPetNameLength
            ? cleaned
            : cleaned[..MaxPetNameLength].TrimEnd();
    }

    /// <summary>
    /// Windows PowerShell 5.1(C# 5.0 기반 Add-Type) 및 PowerShell 7+ 모두에서 동작하는
    /// 고성능 C# RunLoop · 제로-할당 UTF-8 바이트 버퍼 직출력 · 4x SSAA · ACES · 오로라 캐싱 크리스마스 트리 스크립트를 생성한다.
    /// PowerShell 5.1 호환을 위해 `e 및 `u{...} 대신 [char]27을 사용한다.
    /// </summary>
    public static string BuildScriptContent()
    {
        return """"
param(
    [string]$PetName = "LabPet"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "SilentlyContinue"

[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)

try {
    $Host.UI.RawUI.WindowTitle = "LabPet - Merry Christmas!"
}
catch {
}

# PowerShell 5.1(C# 5.0 csc.exe) 및 PowerShell 7+(Roslyn) 공용 고성능 바이트-스트리밍 Half-Block(0x2580) 엔진
$engineSource = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class LabPetConsoleNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CONSOLE_FONT_INFOEX
    {
        public uint cbSize;
        public uint nFont;
        public short dwFontSizeX;
        public short dwFontSizeY;
        public int FontFamily;
        public int FontWeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref CONSOLE_FONT_INFOEX lpConsoleCurrentFontEx);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleOutputCP(uint wCodePageID);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCP(uint wCodePageID);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    public static bool TryEnableVirtualTerminal()
    {
        try
        {
            IntPtr stdOut = GetStdHandle(-11);
            if (stdOut == IntPtr.Zero || stdOut == new IntPtr(-1))
            {
                return false;
            }

            uint mode = 0;
            if (!GetConsoleMode(stdOut, out mode))
            {
                return false;
            }

            if ((mode & 0x0004) != 0)
            {
                return true;
            }

            return SetConsoleMode(stdOut, mode | 0x0004);
        }
        catch
        {
            return false;
        }
    }

    public static void TryDisableQuickEditMode()
    {
        try
        {
            IntPtr stdIn = GetStdHandle(-10);
            if (stdIn == IntPtr.Zero || stdIn == new IntPtr(-1))
            {
                return;
            }

            uint mode = 0;
            if (GetConsoleMode(stdIn, out mode))
            {
                uint nextMode = (mode & ~0x0040u) | 0x0080u;
                SetConsoleMode(stdIn, nextMode);
            }
        }
        catch
        {
        }
    }

    public static void TryConfigureHighDensityWindow(int targetCols, int targetRows, short fontPixelHeight)
    {
        try
        {
            SetConsoleOutputCP(65001);
            SetConsoleCP(65001);
        }
        catch
        {
        }

        try
        {
            IntPtr stdOut = GetStdHandle(-11);
            if (stdOut != IntPtr.Zero && stdOut != new IntPtr(-1))
            {
                string[] fonts = new string[] { "Consolas", "Lucida Console", "굴림체" };
                short[] heights = new short[] { fontPixelHeight, fontPixelHeight, (short)(fontPixelHeight + 1) };

                for (int i = 0; i < fonts.Length; i++)
                {
                    CONSOLE_FONT_INFOEX fontInfo = new CONSOLE_FONT_INFOEX();
                    fontInfo.cbSize = (uint)Marshal.SizeOf(typeof(CONSOLE_FONT_INFOEX));
                    fontInfo.nFont = 0;
                    fontInfo.dwFontSizeX = 0;
                    fontInfo.dwFontSizeY = heights[i];
                    fontInfo.FontFamily = 54;
                    fontInfo.FontWeight = 400;
                    fontInfo.FaceName = fonts[i];
                    if (SetCurrentConsoleFontEx(stdOut, false, ref fontInfo))
                    {
                        break;
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            Thread.Sleep(45);
            int largestW = Console.LargestWindowWidth;
            int largestH = Console.LargestWindowHeight;
            int maxW = (largestW > 4) ? Math.Max(60, largestW - 4) : targetCols;
            int maxH = (largestH > 4) ? Math.Max(24, largestH - 4) : targetRows;
            int w = Math.Min(targetCols, maxW);
            int h = Math.Min(targetRows, maxH);

            if (w < Console.WindowWidth) Console.WindowWidth = w;
            if (h < Console.WindowHeight) Console.WindowHeight = h;
            Console.SetBufferSize(Math.Max(w, Console.BufferWidth), Math.Max(h, Console.BufferHeight));
            Console.SetWindowSize(w, h);
            Console.SetBufferSize(w, h);
        }
        catch
        {
        }
    }

    public static void TryToggleMaximizeConsoleWindow()
    {
        try
        {
            IntPtr hWnd = GetConsoleWindow();
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, IsZoomed(hWnd) ? 9 : 3);
            }
        }
        catch
        {
        }
    }
}

public sealed class LabPetChristmasEngine
{
    private struct Ornament
    {
        public int X;
        public int Y;
        public int Index;
        public int Phase;
        public float Size;
        public bool IsFairy;
    }

    private struct SkyStar
    {
        public float Rx;
        public float Ry;
        public int Phase;
        public int ColorIndex;
        public float BaseIntensity;
    }

    private sealed class Snowflake
    {
        public float X;
        public float Y;
        public float Speed;
        public int Phase;
        public int Depth;
    }

    private sealed class SmokePuff
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float Life;
        public float MaxLife;
    }

    private sealed class Meteor
    {
        public bool Active;
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public int Life;
        public int MaxLife;
    }

    private sealed class Rocket
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float TargetY;
        public int Color1;
        public int Color2;
        public float[] TrailX = new float[4];
        public float[] TrailY = new float[4];
        public int TrailCount;
    }

    private sealed class Spark
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public int Color;
        public int Life;
        public int MaxLife;
        public float[] TrailX = new float[3];
        public float[] TrailY = new float[3];
        public int TrailCount;
        public bool HasCrackle;
    }

    private sealed class BurstFlash
    {
        public float X;
        public float Y;
        public int Color;
        public int Life;
        public int MaxLife;
    }

    private struct GiftSpec
    {
        public int LeftX;
        public int Width;
        public int Height;
        public int BoxColor;
        public int RibbonColor;
    }

    private static readonly int[] BulbPalette = new int[]
    {
        PackRgb(255, 68, 108),
        PackRgb(255, 214, 72),
        PackRgb(72, 224, 255),
        PackRgb(255, 108, 196),
        PackRgb(190, 128, 255),
        PackRgb(84, 245, 176),
        PackRgb(255, 148, 78),
        PackRgb(245, 250, 255)
    };

    private static readonly int[] FireworkPalette = new int[]
    {
        PackRgb(255, 86, 125),
        PackRgb(255, 222, 92),
        PackRgb(82, 232, 255),
        PackRgb(132, 255, 176),
        PackRgb(214, 136, 255),
        PackRgb(255, 160, 84)
    };

    private static readonly string[] LightModeNames = new string[]
    {
        "오로라 웨이브",
        "스타라이트 스파클",
        "로열 골드 & 루비",
        "사이버 네온 펄스"
    };

    private static readonly string[] SnowModeNames = new string[]
    {
        "잔잔한 눈",
        "함박눈",
        "눈보라"
    };

    private static readonly float[] Bayer2x2 = new float[] { 0.0f, 0.50f, 0.75f, 0.25f };

    // 제로-할당 UTF-8 직접 출력 바이트 버퍼
    private byte[] _outBuf = new byte[524288];
    private int _outLen;

    private int _w;
    private int _h;
    private int _pw;
    private int _ph;
    private char[] _currChars = new char[0];
    private int[] _currFg = new int[0];
    private int[] _currBg = new int[0];
    private bool[] _currBold = new bool[0];

    private char[] _prevChars = new char[0];
    private int[] _prevFg = new int[0];
    private int[] _prevBg = new int[0];
    private bool[] _prevBold = new bool[0];

    // 4x SSAA 정적 씬 + 오로라 합성 캐시 + HDR 작업 버퍼
    private float[] _baseR = new float[0];
    private float[] _baseG = new float[0];
    private float[] _baseB = new float[0];
    private float[] _skyR = new float[0];
    private float[] _skyG = new float[0];
    private float[] _skyB = new float[0];
    private float[] _r = new float[0];
    private float[] _g = new float[0];
    private float[] _b = new float[0];
    private byte[] _occludeMask = new byte[0];
    private byte[] _pondMask = new byte[0];

    // 오로라 X축 사전계산 배열 (매 행 Sin/Cos 반복 제거 + 3프레임 주기 캐시)
    private float[] _auroraC1 = new float[0];
    private float[] _auroraC2 = new float[0];
    private float[] _auroraRay = new float[0];
    private int _lastAuroraBucket = -1;
    private bool _auroraValid;

    private readonly List<Ornament> _ornaments = new List<Ornament>();
    private readonly List<int> _garlandPixels = new List<int>();
    private readonly List<int> _cabinWindowPixels = new List<int>();
    private readonly List<GiftSpec> _gifts = new List<GiftSpec>();

    private int _cx;
    private float _moonX;
    private float _moonY;
    private float _moonR;
    private int _starX;
    private int _starY;
    private float _starBaseRadius;
    private int _treeTopY;
    private int _treeBottomY;
    private int _treeH;
    private int _treeTierCount;
    private int _treeMaxHalf;
    private int _trunkTopY;
    private int _trunkBottomY;
    private int _trunkHalf;
    private int _auroraLimitY;
    private int _pondTopY;
    private float _pondCX;
    private float _pondCY;
    private float _pondRX;
    private float _pondRY;

    private bool _hasSnowman;
    private float _snowmanCX;
    private float _snowmanBodyCY;
    private float _snowmanBodyR;
    private float _snowmanHeadCY;
    private float _snowmanHeadR;

    private bool _hasCabin;
    private int _cabinLeft;
    private int _cabinW;
    private int _cabinBaseY;
    private int _cabinWallTopY;
    private int _cabinRoofH;
    private int _chimneyX;
    private int _chimneyY;
    private bool _hasLantern;
    private int _lanternX;
    private int _lanternY;

    private int[] _frontGroundY = new int[0];
    private int[] _farGroundY = new int[0];
    private int[] _mtn1Y = new int[0];
    private int[] _mtn2Y = new int[0];

    private readonly Random _rng = new Random(20261225);
    private readonly List<SkyStar> _stars = new List<SkyStar>();
    private readonly List<Snowflake> _snowflakes = new List<Snowflake>();
    private readonly List<SmokePuff> _smokePuffs = new List<SmokePuff>();
    private readonly List<Rocket> _rockets = new List<Rocket>();
    private readonly List<Spark> _sparks = new List<Spark>();
    private readonly List<BurstFlash> _flashes = new List<BurstFlash>();
    private readonly Meteor _meteor = new Meteor();

    public LabPetChristmasEngine()
    {
        InitParticles();
    }

    private void InitParticles()
    {
        for (int i = 0; i < 120; i++)
        {
            SkyStar s = new SkyStar();
            s.Rx = (float)_rng.NextDouble();
            s.Ry = (float)(_rng.NextDouble() * 0.72);
            s.Phase = _rng.Next(0, 64);
            s.ColorIndex = i % 4;
            s.BaseIntensity = 0.48f + (float)_rng.NextDouble() * 0.52f;
            _stars.Add(s);
        }

        for (int i = 0; i < 180; i++)
        {
            Snowflake sf = new Snowflake();
            sf.X = (float)(_rng.NextDouble() * 240.0);
            sf.Y = (float)(_rng.NextDouble() * 120.0);
            int d = (i % 10 == 0) ? 2 : ((i % 3 == 0) ? 1 : 0);
            sf.Depth = d;
            sf.Speed = (d == 2) ? 0.42f : ((d == 1) ? 0.28f : 0.17f);
            sf.Phase = _rng.Next(0, 64);
            _snowflakes.Add(sf);
        }
    }

    public void TriggerFirework(int count)
    {
        if (_pw <= 0 || _ph <= 0) return;
        int n = Math.Max(1, Math.Min(4, count));
        for (int i = 0; i < n; i++)
        {
            SpawnRocket((i % 2) == 0);
        }
    }

    private void SpawnRocket(bool preferLeft)
    {
        if (_rockets.Count >= 6) return;

        int x;
        int safeLeftMax = Math.Max(10, _cx - _treeMaxHalf - 8);
        int safeRightMin = Math.Min(_pw - 11, _cx + _treeMaxHalf + 8);
        if (_pw >= 76 && safeLeftMax > 8 && safeRightMin < _pw - 8)
        {
            x = preferLeft ? _rng.Next(6, safeLeftMax + 1) : _rng.Next(safeRightMin, _pw - 6);
        }
        else
        {
            x = preferLeft ? _rng.Next(5, Math.Max(7, _cx - 8)) : _rng.Next(Math.Min(_pw - 8, _cx + 8), _pw - 5);
        }

        Rocket r = new Rocket();
        r.X = x;
        r.Y = Math.Max(12, (int)(_ph * 0.78f));
        r.Vx = (float)((_rng.NextDouble() - 0.5) * 0.30);
        r.Vy = -(1.08f + (float)_rng.NextDouble() * 0.42f);
        r.TargetY = _rng.Next(Math.Max(5, (int)(_ph * 0.11f)), Math.Max(12, (int)(_ph * 0.34f)));
        r.Color1 = FireworkPalette[_rng.Next(0, FireworkPalette.Length)];
        r.Color2 = FireworkPalette[_rng.Next(0, FireworkPalette.Length)];
        r.TrailCount = 0;
        _rockets.Add(r);
    }

    private void ExplodeRocket(Rocket r)
    {
        BurstFlash fl = new BurstFlash();
        fl.X = r.X;
        fl.Y = r.Y;
        fl.Color = r.Color1;
        fl.Life = 0;
        fl.MaxLife = 9;
        _flashes.Add(fl);

        int outer = 18;
        for (int i = 0; i < outer; i++)
        {
            double ang = (Math.PI * 2.0 * i) / outer + (_rng.NextDouble() - 0.5) * 0.10;
            float spd = 1.02f + (float)_rng.NextDouble() * 0.40f;
            Spark sp = new Spark();
            sp.X = r.X;
            sp.Y = r.Y;
            sp.Vx = (float)(Math.Cos(ang) * spd);
            sp.Vy = (float)(Math.Sin(ang) * spd);
            sp.Color = (i % 2 == 0) ? r.Color1 : r.Color2;
            sp.Life = 0;
            sp.MaxLife = 22 + _rng.Next(0, 9);
            sp.HasCrackle = (i % 3 == 0);
            _sparks.Add(sp);
        }

        int inner = 10;
        for (int i = 0; i < inner; i++)
        {
            double ang = (Math.PI * 2.0 * i) / inner + 0.15;
            float spd = 0.50f + (float)_rng.NextDouble() * 0.22f;
            Spark sp = new Spark();
            sp.X = r.X;
            sp.Y = r.Y;
            sp.Vx = (float)(Math.Cos(ang) * spd);
            sp.Vy = (float)(Math.Sin(ang) * spd);
            sp.Color = PackRgb(255, 246, 200);
            sp.Life = 0;
            sp.MaxLife = 16 + _rng.Next(0, 6);
            sp.HasCrackle = false;
            _sparks.Add(sp);
        }
    }

    private void RebuildStaticScene(int width, int height)
    {
        int w = Math.Max(40, width);
        int h = Math.Max(22, height);
        int gfxRows = Math.Max(10, h - 3);
        int pw = w;
        int ph = gfxRows * 2;

        _w = w;
        _h = h;
        _pw = pw;
        _ph = ph;

        int cellTotal = w * h;
        _currChars = new char[cellTotal];
        _currFg = new int[cellTotal];
        _currBg = new int[cellTotal];
        _currBold = new bool[cellTotal];
        _prevChars = new char[cellTotal];
        _prevFg = new int[cellTotal];
        _prevBg = new int[cellTotal];
        _prevBold = new bool[cellTotal];

        int minOutBuf = (cellTotal * 28) + 65536;
        if (_outBuf.Length < minOutBuf)
        {
            _outBuf = new byte[minOutBuf];
        }

        int pxTotal = pw * ph;
        _baseR = new float[pxTotal];
        _baseG = new float[pxTotal];
        _baseB = new float[pxTotal];
        _r = new float[pxTotal];
        _g = new float[pxTotal];
        _b = new float[pxTotal];
        _occludeMask = new byte[pxTotal];
        _pondMask = new byte[pxTotal];
        _frontGroundY = new int[pw];
        _farGroundY = new int[pw];
        _mtn1Y = new int[pw];
        _mtn2Y = new int[pw];

        _auroraLimitY = (int)(ph * 0.56f);
        int auroraPx = _auroraLimitY * pw;
        _skyR = new float[auroraPx];
        _skyG = new float[auroraPx];
        _skyB = new float[auroraPx];
        _auroraC1 = new float[pw];
        _auroraC2 = new float[pw];
        _auroraRay = new float[pw];
        _auroraValid = false;
        _lastAuroraBucket = -1;

        _ornaments.Clear();
        _garlandPixels.Clear();
        _cabinWindowPixels.Clear();
        _smokePuffs.Clear();
        _gifts.Clear();

        _cx = pw / 2;

        float horizonBase = ph * 0.80f;
        for (int x = 0; x < pw; x++)
        {
            float nx = (float)x / Math.Max(1, pw - 1);
            float farHill = horizonBase - (float)(Math.Sin(nx * 4.2 + 0.5) * (ph * 0.042) + Math.Cos(nx * 9.0) * (ph * 0.014));
            float frontHill = (ph * 0.838f) - (float)(Math.Cos((nx - 0.5) * 3.1) * (ph * 0.026) + Math.Sin(nx * 7.5 + 1.2) * (ph * 0.010));
            int frontY = ClampInt((int)Math.Round(frontHill), (int)(ph * 0.68f), ph - 5);
            int farY = ClampInt((int)Math.Round(farHill), (int)(ph * 0.62f), frontY);
            _frontGroundY[x] = frontY;
            _farGroundY[x] = farY;

            _mtn1Y[x] = (int)Math.Round((ph * 0.72f) - (
                Math.Abs(Math.Sin(nx * 5.2 + 0.8)) * (ph * 0.13) +
                Math.Abs(Math.Sin(nx * 12.5 + 2.1)) * (ph * 0.042)));
            _mtn2Y[x] = (int)Math.Round((ph * 0.77f) - (
                Math.Abs(Math.Sin(nx * 3.9 + 2.4)) * (ph * 0.10) +
                Math.Abs(Math.Cos(nx * 11.0)) * (ph * 0.028)));
        }

        _moonX = Math.Max(9.0f, pw * 0.135f);
        _moonY = Math.Max(6.0f, ph * 0.175f);
        _moonR = ClampFloat(ph * 0.072f, 3.0f, 7.8f);

        int centerGroundY = _frontGroundY[_cx];
        int trunkH = Math.Max(4, (int)Math.Round(ph * 0.072f));
        _trunkTopY = centerGroundY - trunkH;
        _trunkBottomY = centerGroundY;
        _starY = Math.Max(5, (int)Math.Round(ph * 0.125f));
        _starX = _cx;
        _starBaseRadius = ClampFloat(ph * 0.065f, 3.0f, 7.2f);
        _treeTopY = _starY + Math.Max(2, (int)Math.Round(_starBaseRadius * 0.55f));
        _treeBottomY = _trunkTopY - 1;
        _treeH = Math.Max(16, _treeBottomY - _treeTopY + 1);
        _treeMaxHalf = Math.Max(11, Math.Min((int)(pw * 0.28f), (int)(_treeH * 0.64f)));
        _treeTierCount = (_treeH >= 44) ? 7 : ((_treeH >= 30) ? 6 : 5);
        _trunkHalf = Math.Max(2, (int)Math.Round(_treeMaxHalf * 0.14f));

        _pondCX = _cx;
        _pondRY = ClampFloat(ph * 0.056f, 2.5f, 5.2f);
        _pondCY = ClampFloat(ph - _pondRY - 1.6f, centerGroundY + _pondRY + 0.8f, ph - _pondRY - 1.2f);
        _pondRX = Math.Max(14.0f, _treeMaxHalf * 1.32f);
        _pondTopY = (int)Math.Floor(_pondCY - _pondRY);

        int giftLeftEnd = _cx - _trunkHalf - 2;
        int giftRightStart = _cx + _trunkHalf + 2;
        AddGiftSpec(giftLeftEnd - 7, 7, 5, PackRgb(220, 42, 72), PackRgb(255, 222, 95));
        AddGiftSpec(giftRightStart + 1, 8, 5, PackRgb(42, 138, 225), PackRgb(255, 175, 225));
        if (pw >= 76)
        {
            AddGiftSpec(giftLeftEnd - 13, 5, 4, PackRgb(155, 72, 220), PackRgb(120, 250, 205));
            AddGiftSpec(giftRightStart + 10, 6, 4, PackRgb(235, 162, 42), PackRgb(255, 75, 108));
        }
        if (pw >= 118)
        {
            AddGiftSpec(giftLeftEnd - 21, 7, 6, PackRgb(36, 158, 108), PackRgb(255, 232, 128));
            AddGiftSpec(giftRightStart + 17, 7, 6, PackRgb(225, 72, 148), PackRgb(245, 252, 255));
        }

        int leftLimit = (pw >= 118) ? (giftLeftEnd - 23) : ((pw >= 76) ? (giftLeftEnd - 15) : (giftLeftEnd - 9));
        int snowmanX = Math.Min(_cx - _treeMaxHalf - 8, leftLimit - 6);
        _hasSnowman = (pw >= 68 && snowmanX >= 7);
        if (_hasSnowman)
        {
            _snowmanCX = snowmanX;
            int sBaseY = _frontGroundY[ClampInt(snowmanX, 0, pw - 1)];
            _snowmanBodyR = ClampFloat(ph * 0.048f, 2.8f, 4.6f);
            _snowmanHeadR = _snowmanBodyR * 0.72f;
            _snowmanBodyCY = sBaseY - _snowmanBodyR * 0.7f;
            _snowmanHeadCY = _snowmanBodyCY - _snowmanBodyR - _snowmanHeadR * 0.72f;
        }

        _hasCabin = false;
        _hasLantern = false;
        int rightLimit = (pw >= 118) ? (giftRightStart + 25) : ((pw >= 76) ? (giftRightStart + 16) : (giftRightStart + 11));
        _cabinW = ClampInt((int)(pw * 0.145f), 16, 26);
        _cabinLeft = Math.Max(_cx + _treeMaxHalf + 6, rightLimit + 4);
        if (pw >= 88 && _cabinLeft + _cabinW + 4 < pw)
        {
            _hasCabin = true;
            int center = _cabinLeft + (_cabinW / 2);
            _cabinBaseY = _frontGroundY[ClampInt(center, 0, pw - 1)];
            int wallH = Math.Max(6, (int)Math.Round(_cabinW * 0.38f));
            _cabinRoofH = Math.Max(5, (int)Math.Round(_cabinW * 0.34f));
            _cabinWallTopY = _cabinBaseY - wallH + 1;
            _chimneyX = _cabinLeft + (int)(_cabinW * 0.72f);
            _chimneyY = _cabinWallTopY - _cabinRoofH;

            int postX = _cabinLeft - 2;
            if (postX > _cx + _treeMaxHalf + 2)
            {
                _hasLantern = true;
                _lanternX = postX;
                _lanternY = Math.Max(2, _frontGroundY[postX] - Math.Max(5, (int)Math.Round(ph * 0.08f)) + 1);
            }
        }
        else if (pw >= 68 && rightLimit + 6 < pw - 4)
        {
            _hasLantern = true;
            _lanternX = rightLimit + 5;
            _lanternY = Math.Max(2, _frontGroundY[_lanternX] - Math.Max(5, (int)Math.Round(ph * 0.08f)) + 1);
        }

        // 4x SSAA 정적 씬 빌더
        float[] subOffsets = new float[] { -0.25f, 0.25f };
        for (int y = 0; y < ph; y++)
        {
            for (int x = 0; x < pw; x++)
            {
                float sumR = 0.0f;
                float sumG = 0.0f;
                float sumB = 0.0f;
                byte centerOcclude = 0;
                byte pondWeight = 0;

                for (int sy = 0; sy < 2; sy++)
                {
                    for (int sx = 0; sx < 2; sx++)
                    {
                        float fx = x + subOffsets[sx];
                        float fy = y + subOffsets[sy];
                        float sr;
                        float sg;
                        float sb;
                        byte occ;
                        byte pwVal;
                        SampleStaticSubpixel(fx, fy, out sr, out sg, out sb, out occ, out pwVal);
                        sumR += sr;
                        sumG += sg;
                        sumB += sb;
                        if (sx == 0 && sy == 0)
                        {
                            centerOcclude = occ;
                            pondWeight = pwVal;
                        }
                        else if (occ > centerOcclude)
                        {
                            centerOcclude = occ;
                        }
                        if (pwVal > pondWeight) pondWeight = pwVal;
                    }
                }

                int idx = y * pw + x;
                _baseR[idx] = sumR * 0.25f;
                _baseG[idx] = sumG * 0.25f;
                _baseB[idx] = sumB * 0.25f;
                _occludeMask[idx] = centerOcclude;
                _pondMask[idx] = pondWeight;
            }
        }

        CollectTreeDecorations();
        if (_hasCabin)
        {
            CollectCabinWindows();
        }
    }

    private void UpdateAuroraCacheIfNeeded(int frame)
    {
        int bucket = frame / 6;
        int band = frame % 6;
        bool initAll = !_auroraValid;

        if (initAll || band == 0 || bucket != _lastAuroraBucket)
        {
            _lastAuroraBucket = bucket;
            float tTime = bucket * 0.14f;
            float invPw = 1.0f / Math.Max(1, _pw);
            for (int x = 0; x < _pw; x++)
            {
                float nx = x * invPw;
                _auroraC1[x] = 0.34f + 0.16f * (float)Math.Sin(nx * 5.8f + tTime) + 0.08f * (float)Math.Sin(nx * 12.2f - tTime * 0.7f);
                _auroraC2[x] = 0.48f + 0.14f * (float)Math.Cos(nx * 4.1f - tTime * 0.85f) + 0.07f * (float)Math.Sin(nx * 9.5f + tTime * 1.1f);
                _auroraRay[x] = 0.58f + 0.42f * (float)Math.Sin(nx * 28.0f + tTime * 1.9f);
            }
        }

        int yStart = initAll ? 0 : (band * _auroraLimitY) / 6;
        int yEnd = initAll ? _auroraLimitY : ((band + 1) * _auroraLimitY) / 6;
        if (yEnd <= yStart)
        {
            _auroraValid = true;
            return;
        }

        int offset = yStart * _pw;
        int count = (yEnd - yStart) * _pw;
        Array.Copy(_baseR, offset, _skyR, offset, count);
        Array.Copy(_baseG, offset, _skyG, offset, count);
        Array.Copy(_baseB, offset, _skyB, offset, count);

        float invLimY = 1.0f / Math.Max(1, _auroraLimitY);
        for (int y = yStart; y < yEnd; y++)
        {
            float ny = y * invLimY;
            int rowBase = y * _pw;
            for (int x = 0; x < _pw; x++)
            {
                int idx = rowBase + x;
                if (_occludeMask[idx] != 0) continue;

                float d1 = Math.Abs(ny - _auroraC1[x]);
                float d2 = Math.Abs(ny - _auroraC2[x]);

                float band1 = 0.0f;
                if (d1 < 0.25f)
                {
                    float u1 = 1.0f - d1 * 4.0f;
                    band1 = u1 * u1;
                }

                float band2 = 0.0f;
                if (d2 < 0.22f)
                {
                    float u2 = 1.0f - d2 * 4.545f;
                    band2 = u2 * u2;
                }

                if (band1 > 0.01f || band2 > 0.01f)
                {
                    float ray = _auroraRay[x];
                    float a1 = band1 * ray * 0.46f;
                    float a2 = band2 * (1.15f - ray * 0.4f) * 0.32f;

                    _skyR[idx] += a1 * 0.08f + a2 * 0.50f;
                    _skyG[idx] += a1 * 0.70f + a2 * 0.20f;
                    _skyB[idx] += a1 * 0.54f + a2 * 0.82f;
                }
            }
        }

        _auroraValid = true;
    }

    private void SampleStaticSubpixel(float fx, float fy, out float r, out float g, out float b, out byte occlude, out byte pondWeight)
    {
        int pw = _pw;
        int ph = _ph;
        int ix = ClampInt((int)Math.Round(fx), 0, pw - 1);
        float nx = fx / Math.Max(1.0f, pw - 1.0f);

        int frontY = _frontGroundY[ix];
        int farY = _farGroundY[ix];
        int mtn1Y = _mtn1Y[ix];
        int mtn2Y = _mtn2Y[ix];
        pondWeight = 0;

        if (fy < mtn1Y && fy < farY)
        {
            float t = Clamp01(fy / Math.Max(1.0f, ph * 0.80f));
            r = 0.012f + 0.040f * t;
            g = 0.020f + 0.064f * t;
            b = 0.072f + 0.148f * t;
            occlude = 0;

            float dxM = fx - _moonX;
            float dyM = fy - _moonY;
            float distMain = (float)Math.Sqrt(dxM * dxM + dyM * dyM);
            if (distMain <= _moonR * 4.2f)
            {
                float cutDx = dxM - _moonR * 0.44f;
                float cutDy = dyM + _moonR * 0.22f;
                float distCut = (float)Math.Sqrt(cutDx * cutDx + cutDy * cutDy);
                float crescent = SmoothStep(_moonR + 0.4f, _moonR - 0.4f, distMain) * SmoothStep(_moonR * 0.82f - 0.4f, _moonR * 0.82f + 0.4f, distCut);
                if (crescent > 0.01f)
                {
                    float crater = 0.94f + 0.06f * (float)Math.Sin(fx * 1.3f + fy * 1.7f);
                    r = Lerp(r, 1.02f * crater, crescent);
                    g = Lerp(g, 0.97f * crater, crescent);
                    b = Lerp(b, 0.76f * crater, crescent);
                }
                float halo = (float)Math.Exp(-(distMain * distMain) / (_moonR * _moonR * 4.4f)) * 0.26f;
                r += 0.95f * halo;
                g += 0.85f * halo;
                b += 0.52f * halo;
            }
        }
        else if (fy < farY)
        {
            float ridgeDist = Math.Min(Math.Abs(fy - mtn1Y), Math.Abs(fy - mtn2Y));
            bool inFrontMtn = (fy >= mtn2Y);
            float snowRim = (ridgeDist <= 2.2f) ? (1.0f - ridgeDist / 2.2f) : 0.0f;
            float ridgeSlope = 0.5f + 0.5f * (float)Math.Sin(nx * 24.0f + fy * 0.15f);
            if (inFrontMtn)
            {
                r = 0.062f + snowRim * (0.12f + 0.06f * ridgeSlope);
                g = 0.100f + snowRim * (0.17f + 0.08f * ridgeSlope);
                b = 0.205f + snowRim * (0.22f + 0.09f * ridgeSlope);
            }
            else
            {
                r = 0.042f + snowRim * 0.11f;
                g = 0.072f + snowRim * 0.15f;
                b = 0.160f + snowRim * 0.21f;
            }
            occlude = 1;
        }
        else if (fy < frontY)
        {
            float depth = (fy - farY) / Math.Max(1.0f, frontY - farY);
            r = 0.26f + depth * 0.16f;
            g = 0.38f + depth * 0.20f;
            b = 0.58f + depth * 0.20f;
            occlude = 2;
        }
        else
        {
            float depth = Clamp01((fy - frontY) / Math.Max(1.0f, ph - 1.0f - frontY));
            float rim = (fy - frontY <= 1.2f) ? 0.14f : 0.0f;
            r = Clamp01(0.54f - depth * 0.24f + rim);
            g = Clamp01(0.70f - depth * 0.26f + rim);
            b = Clamp01(0.88f - depth * 0.20f + rim * 0.7f);
            occlude = 2;

            float pdx = (fx - _pondCX) / _pondRX;
            float pdy = (fy - _pondCY) / _pondRY;
            float pd = (float)Math.Sqrt(pdx * pdx + pdy * pdy);
            if (pd <= 1.12f)
            {
                if (pd >= 0.92f)
                {
                    float edgeSnow = SmoothStep(1.12f, 0.96f, pd);
                    r = Lerp(r, 0.80f, edgeSnow * 0.65f);
                    g = Lerp(g, 0.90f, edgeSnow * 0.65f);
                    b = Lerp(b, 0.99f, edgeSnow * 0.65f);
                }
                else
                {
                    float iceDepth = SmoothStep(0.95f, 0.35f, pd);
                    r = Lerp(r, 0.06f, iceDepth);
                    g = Lerp(g, 0.16f, iceDepth);
                    b = Lerp(b, 0.34f, iceDepth);
                    pondWeight = (byte)ClampInt((int)(iceDepth * 230.0f), 0, 255);
                }
            }
        }

        if (fy >= _trunkTopY - 0.5f && fy <= _trunkBottomY + 1.2f && Math.Abs(fx - _cx) <= _trunkHalf + 0.35f)
        {
            float u = (fx - _cx) / Math.Max(1.0f, _trunkHalf);
            float shade = 0.86f - Math.Abs(u) * 0.34f - (u * 0.14f);
            float bark = 0.92f + 0.08f * (float)Math.Sin(fx * 2.4f + fy * 0.5f);
            r = 0.42f * shade * bark;
            g = 0.24f * shade * bark;
            b = 0.12f * shade * bark;
            occlude = 2;
            pondWeight = 0;
        }

        if (fy >= _treeTopY - 0.4f && fy <= _treeBottomY + 0.8f)
        {
            float rowF = fy - _treeTopY;
            float globalT = Clamp01(rowF / Math.Max(1.0f, _treeH - 1.0f));
            int tier = ClampInt((int)((rowF * _treeTierCount) / _treeH), 0, _treeTierCount - 1);
            float tierStart = (float)tier / _treeTierCount;
            float tierEnd = (float)(tier + 1) / _treeTierCount;
            float localU = Clamp01((globalT - tierStart) / Math.Max(0.001f, tierEnd - tierStart));

            float minRatio = (tier == 0) ? 0.02f : (0.14f + tier * 0.112f);
            float maxRatio = 0.24f + (tier + 1) * (0.76f / _treeTierCount);
            float halfF = Math.Max(1.0f, _treeMaxHalf * (minRatio + (maxRatio - minRatio) * (float)Math.Pow(localU, 0.88)));

            float dx = fx - _cx;
            float scallopDrop = (localU > 0.78f && halfF > 3.5f)
                ? (0.5f + 0.5f * (float)Math.Sin(dx * 1.15f + tier * 1.3f)) * 1.2f
                : 0.0f;

            if (Math.Abs(dx) <= halfF + scallopDrop * 0.4f && (localU <= 0.93f || scallopDrop > 0.35f))
            {
                float sideNorm = ClampFloat(dx / Math.Max(1.0f, halfF), -1.0f, 1.0f);
                float radial = Math.Abs(sideNorm);
                float aoUnderUpperTier = (tier > 0 && localU < 0.18f) ? (0.58f + (localU / 0.18f) * 0.42f) : 1.0f;
                float lightBias = -sideNorm * 0.15f + (1.0f - radial * radial) * 0.30f;
                float pineNoise = 0.025f * (float)Math.Sin(fx * 1.9f + fy * 0.7f) * (float)Math.Cos(fx * 0.8f - fy * 1.5f);

                float tr = Clamp01((0.035f + lightBias * 0.12f + pineNoise) * aoUnderUpperTier);
                float tg = Clamp01((0.185f + lightBias * 0.29f + (1.0f - globalT) * 0.05f + pineNoise * 1.6f) * aoUnderUpperTier);
                float tb = Clamp01((0.095f + lightBias * 0.14f + pineNoise) * aoUnderUpperTier);

                bool snowEdge = (localU > 0.86f) && (radial > 0.42f || scallopDrop > 0.72f);
                if (snowEdge)
                {
                    float smix = (localU - 0.86f) / 0.14f * 0.65f;
                    tr = Lerp(tr, 0.78f, smix);
                    tg = Lerp(tg, 0.89f, smix);
                    tb = Lerp(tb, 0.99f, smix);
                }

                r = tr; g = tg; b = tb;
                occlude = 2;
                pondWeight = 0;
            }
        }

        for (int i = 0; i < _gifts.Count; i++)
        {
            GiftSpec gf = _gifts[i];
            int gxCenter = ClampInt(gf.LeftX + gf.Width / 2, 0, pw - 1);
            int gBaseY = _frontGroundY[gxCenter];
            int gTopY = gBaseY - gf.Height + 1;

            if (fy >= gBaseY + 0.5f && fy <= gBaseY + 1.8f && fx >= gf.LeftX - 0.5f && fx <= gf.LeftX + gf.Width + 0.5f)
            {
                r *= 0.62f; g *= 0.64f; b *= 0.72f;
            }

            if (fx >= gf.LeftX - 0.2f && fx <= gf.LeftX + gf.Width - 0.8f && fy >= gTopY - 0.2f && fy <= gBaseY + 0.2f)
            {
                int midX = gf.LeftX + gf.Width / 2;
                int midY = gTopY + gf.Height / 2;
                bool isRib = (Math.Abs(fx - midX) <= 0.55f || Math.Abs(fy - midY) <= 0.55f);
                float br = ((gf.BoxColor >> 16) & 255) / 255.0f;
                float bg = ((gf.BoxColor >> 8) & 255) / 255.0f;
                float bb = (gf.BoxColor & 255) / 255.0f;
                float rr = ((gf.RibbonColor >> 16) & 255) / 255.0f;
                float rg = ((gf.RibbonColor >> 8) & 255) / 255.0f;
                float rb = (gf.RibbonColor & 255) / 255.0f;
                float shade = (fy - gTopY < 1.0f) ? 1.15f : (0.80f + 0.20f * (1.0f - (fx - gf.LeftX) / gf.Width));
                if (isRib) { r = Clamp01(rr * shade); g = Clamp01(rg * shade); b = Clamp01(rb * shade); }
                else { r = Clamp01(br * shade); g = Clamp01(bg * shade); b = Clamp01(bb * shade); }
                occlude = 2;
                pondWeight = 0;
            }
            else if (Math.Abs(fy - (gTopY - 1.0f)) <= 0.45f && (Math.Abs(fx - (gf.LeftX + gf.Width / 2 - 1)) <= 0.45f || Math.Abs(fx - (gf.LeftX + gf.Width / 2 + 1)) <= 0.45f))
            {
                r = ((gf.RibbonColor >> 16) & 255) / 255.0f;
                g = ((gf.RibbonColor >> 8) & 255) / 255.0f;
                b = (gf.RibbonColor & 255) / 255.0f;
                occlude = 2;
                pondWeight = 0;
            }
        }

        if (_hasSnowman)
        {
            float dxB = fx - _snowmanCX;
            float dyB = fy - _snowmanBodyCY;
            float dB = (float)Math.Sqrt(dxB * dxB + dyB * dyB);
            float dyH = fy - _snowmanHeadCY;
            float dH = (float)Math.Sqrt(dxB * dxB + dyH * dyH);

            if (dB <= _snowmanBodyR + 0.35f)
            {
                float shade = Clamp01(1.0f - (dB / (_snowmanBodyR + 0.6f)) * 0.22f) * Clamp01(0.95f - (dxB / Math.Max(1.0f, _snowmanBodyR)) * 0.16f);
                r = 0.85f * shade; g = 0.91f * shade; b = 0.99f * Clamp01(1.0f - (dB / (_snowmanBodyR + 0.6f)) * 0.16f);
                if (Math.Abs(dxB) <= 0.55f && (Math.Abs(dyB) <= 1.2f && Math.Abs(dyB) >= 0.4f)) { r = 0.12f; g = 0.14f; b = 0.20f; }
                occlude = 2; pondWeight = 0;
            }
            if (dH <= _snowmanHeadR + 0.35f)
            {
                float shade = Clamp01(1.0f - (dH / (_snowmanHeadR + 0.6f)) * 0.20f) * Clamp01(0.95f - (dxB / Math.Max(1.0f, _snowmanHeadR)) * 0.16f);
                r = 0.86f * shade; g = 0.92f * shade; b = 0.99f;
                if (Math.Abs(dyH + _snowmanHeadR * 0.25f) <= 0.45f && Math.Abs(Math.Abs(dxB) - 1.0f) <= 0.42f) { r = 0.08f; g = 0.10f; b = 0.16f; }
                if (Math.Abs(dyH - 0.1f) <= 0.42f && fx >= _snowmanCX - 1.3f && fx <= _snowmanCX + 0.3f) { r = 1.0f; g = 0.48f; b = 0.10f; }
                occlude = 2; pondWeight = 0;
            }

            float scarfY = _snowmanHeadCY + _snowmanHeadR * 0.85f;
            float scarfW = Math.Max(2.0f, _snowmanHeadR);
            if (Math.Abs(fy - scarfY) <= 0.55f && Math.Abs(dxB) <= scarfW)
            {
                bool whiteStripe = ((int)Math.Round(fx) % 2 == 0);
                r = whiteStripe ? 0.96f : 0.90f; g = whiteStripe ? 0.96f : 0.16f; b = whiteStripe ? 0.98f : 0.24f;
                occlude = 2; pondWeight = 0;
            }
            else if (fx >= _snowmanCX + 0.6f && fx <= _snowmanCX + 1.5f && fy >= scarfY + 0.6f && fy <= scarfY + 2.4f)
            {
                r = 0.82f; g = 0.14f; b = 0.22f;
                occlude = 2; pondWeight = 0;
            }

            float brimY = _snowmanHeadCY - _snowmanHeadR * 0.85f;
            float hatW = Math.Max(2.0f, _snowmanHeadR * 1.15f);
            if (Math.Abs(fy - brimY) <= 0.5f && Math.Abs(dxB) <= hatW)
            {
                r = 0.12f; g = 0.14f; b = 0.22f; occlude = 2; pondWeight = 0;
            }
            else if (fy >= brimY - 3.2f && fy < brimY - 0.4f && Math.Abs(dxB) <= hatW - 0.8f)
            {
                if (fy >= brimY - 1.3f) { r = 0.88f; g = 0.18f; b = 0.25f; }
                else if (fy <= brimY - 2.6f) { r = 0.85f; g = 0.92f; b = 0.99f; }
                else { r = 0.14f; g = 0.16f; b = 0.24f; }
                occlude = 2; pondWeight = 0;
            }
        }

        if (_hasCabin)
        {
            int centerC = _cabinLeft + (_cabinW / 2);
            if (fx >= _chimneyX - 0.2f && fx <= _chimneyX + 1.3f && fy >= _chimneyY + 0.6f && fy <= _cabinWallTopY)
            {
                r = (fx < _chimneyX + 0.6f) ? 0.36f : 0.26f;
                g = 0.20f; b = 0.16f;
                occlude = 2; pondWeight = 0;
            }
            if (fx >= _cabinLeft + 0.8f && fx <= _cabinLeft + _cabinW - 1.8f && fy >= _cabinWallTopY - 0.2f && fy <= _cabinBaseY + 1.2f)
            {
                float logShade = (((int)Math.Floor(fy) % 2) == 0) ? 1.0f : 0.78f;
                float eaveAO = (fy - _cabinWallTopY <= 1.5f) ? 0.56f : 1.0f;
                r = 0.38f * logShade * eaveAO; g = 0.22f * logShade * eaveAO; b = 0.12f * logShade * eaveAO;

                int doorX = centerC + (_cabinW / 6);
                int doorW = Math.Max(2, _cabinW / 7);
                if (fx >= doorX - 0.2f && fx <= doorX + doorW - 0.6f && fy >= _cabinBaseY - Math.Max(3, (_cabinBaseY - _cabinWallTopY) - 1))
                {
                    r = 0.22f; g = 0.12f; b = 0.08f;
                }
                occlude = 2; pondWeight = 0;
            }
            float roofTopY = _cabinWallTopY - _cabinRoofH + 1.0f;
            if (fy >= roofTopY - 0.3f && fy <= _cabinWallTopY + 0.4f)
            {
                float ru = Clamp01((fy - roofTopY) / Math.Max(1.0f, _cabinRoofH - 1.0f));
                float rHalf = Math.Max(1.0f, (_cabinW * 0.56f) * ru);
                float rdx = fx - centerC;
                if (Math.Abs(rdx) <= rHalf)
                {
                    float s = 0.88f - (rdx > 0 ? 0.12f : 0.0f);
                    r = 0.84f * s; g = 0.91f * s; b = 0.99f * s;
                    occlude = 2; pondWeight = 0;
                }
            }
            else if (fy > _cabinWallTopY + 0.4f && fy <= _cabinWallTopY + 1.6f && Math.Abs(fx - centerC) <= (_cabinW * 0.54f))
            {
                if (((int)Math.Round(fx) % 3) == 0)
                {
                    r = 0.76f; g = 0.88f; b = 0.99f;
                    occlude = 2; pondWeight = 0;
                }
            }
        }

        if (_hasLantern)
        {
            int lBaseY = _frontGroundY[ClampInt(_lanternX, 0, pw - 1)];
            int lTopY = _lanternY - 1;
            if (Math.Abs(fx - _lanternX) <= 0.48f && fy >= lTopY + 2 && fy <= lBaseY)
            {
                r = 0.26f; g = 0.30f; b = 0.40f; occlude = 2; pondWeight = 0;
            }
            else if (Math.Abs(fy - lTopY) <= 0.48f && Math.Abs(fx - _lanternX) <= 1.2f)
            {
                r = 0.34f; g = 0.38f; b = 0.50f; occlude = 2; pondWeight = 0;
            }
            else if (Math.Abs(fy - lBaseY) <= 0.48f && Math.Abs(fx - _lanternX) <= 1.2f)
            {
                r = 0.24f; g = 0.28f; b = 0.36f; occlude = 2; pondWeight = 0;
            }
        }
    }

    private void CollectTreeDecorations()
    {
        int bulbSeq = 0;
        for (int row = 0; row < _treeH; row++)
        {
            int y = _treeTopY + row;
            if (y < 0 || y >= _ph) continue;

            float globalT = (float)row / Math.Max(1, _treeH - 1);
            int tier = ClampInt((int)((row * _treeTierCount) / (float)_treeH), 0, _treeTierCount - 1);
            float tierStart = (float)tier / _treeTierCount;
            float tierEnd = (float)(tier + 1) / _treeTierCount;
            float localU = Clamp01((globalT - tierStart) / Math.Max(0.001f, tierEnd - tierStart));
            float minRatio = (tier == 0) ? 0.02f : (0.14f + tier * 0.112f);
            float maxRatio = 0.24f + (tier + 1) * (0.76f / _treeTierCount);
            int half = Math.Max(1, (int)Math.Round(_treeMaxHalf * (minRatio + (maxRatio - minRatio) * (float)Math.Pow(localU, 0.88))));
            bool isTierBottom = (row == _treeH - 1) || ((int)(((row + 1) * _treeTierCount) / (float)_treeH) > tier);

            for (int dx = -half; dx <= half; dx++)
            {
                int x = _cx + dx;
                if (x < 0 || x >= _pw) continue;
                float band = (float)Math.Sin((dx * 0.36f) + (row * 0.26f));
                if (half >= 4 && Math.Abs(band) < 0.16f && !isTierBottom)
                {
                    _garlandPixels.Add(y * _pw + x);
                }
            }

            if (row >= 3 && !isTierBottom && half >= 4)
            {
                int step = Math.Max(5, (int)Math.Round(half * 0.58f));
                if (row % 5 == 2)
                {
                    for (int ox = -half + 3; ox <= half - 3; ox += step)
                    {
                        Ornament orn = new Ornament();
                        orn.X = _cx + ox;
                        orn.Y = y;
                        orn.Index = bulbSeq++;
                        orn.Phase = (orn.X * 7 + y * 13) % 60;
                        orn.Size = (_treeH >= 40) ? 1.55f : 1.15f;
                        orn.IsFairy = false;
                        _ornaments.Add(orn);
                    }
                }
                else if (row % 3 == 1)
                {
                    for (int fx = -half + 2 + ((row * 2) % 3); fx <= half - 2; fx += 4)
                    {
                        Ornament fairy = new Ornament();
                        fairy.X = _cx + fx;
                        fairy.Y = y;
                        fairy.Index = bulbSeq++;
                        fairy.Phase = (fairy.X * 11 + y * 5) % 48;
                        fairy.Size = 0.72f;
                        fairy.IsFairy = true;
                        _ornaments.Add(fairy);
                    }
                }
            }
        }
    }

    private void CollectCabinWindows()
    {
        int wallH = Math.Max(6, (int)Math.Round(_cabinW * 0.38f));
        int winX = _cabinLeft + Math.Max(3, _cabinW / 5);
        int winW = Math.Max(3, _cabinW / 5);
        int winTop = _cabinWallTopY + Math.Max(1, wallH / 4);
        int winH = Math.Max(3, wallH / 2);
        int midWX = winX + (winW / 2);
        int midWY = winTop + (winH / 2);

        for (int wy = winTop; wy < winTop + winH && wy < _ph; wy++)
        {
            for (int wx = winX; wx < winX + winW && wx < _pw; wx++)
            {
                if ((winW >= 3 && wx == midWX) || (winH >= 3 && wy == midWY))
                {
                    int fIdx = wy * _pw + wx;
                    _baseR[fIdx] = 0.16f;
                    _baseG[fIdx] = 0.09f;
                    _baseB[fIdx] = 0.05f;
                    continue;
                }
                _cabinWindowPixels.Add(wy * _pw + wx);
            }
        }
    }

    private void AddGiftSpec(int leftX, int width, int height, int boxColor, int ribbonColor)
    {
        if (leftX < 1 || leftX + width >= _pw - 1) return;
        GiftSpec spec = new GiftSpec();
        spec.LeftX = leftX;
        spec.Width = width;
        spec.Height = height;
        spec.BoxColor = boxColor;
        spec.RibbonColor = ribbonColor;
        _gifts.Add(spec);
    }

    private void StepDynamicAnimations(int frame, int snowLevel)
    {
        if ((frame % 28) == 3 && _rockets.Count < 4)
        {
            SpawnRocket(((frame / 28) % 2) == 0);
        }

        for (int i = _rockets.Count - 1; i >= 0; i--)
        {
            Rocket rk = _rockets[i];
            for (int t = 3; t >= 1; t--)
            {
                rk.TrailX[t] = rk.TrailX[t - 1];
                rk.TrailY[t] = rk.TrailY[t - 1];
            }
            rk.TrailX[0] = rk.X;
            rk.TrailY[0] = rk.Y;
            if (rk.TrailCount < 4) rk.TrailCount++;

            rk.X += rk.Vx;
            rk.Y += rk.Vy;
            if (rk.Y <= rk.TargetY)
            {
                ExplodeRocket(rk);
                _rockets.RemoveAt(i);
            }
        }

        for (int i = _flashes.Count - 1; i >= 0; i--)
        {
            _flashes[i].Life++;
            if (_flashes[i].Life >= _flashes[i].MaxLife) _flashes.RemoveAt(i);
        }

        for (int i = _sparks.Count - 1; i >= 0; i--)
        {
            Spark sp = _sparks[i];
            for (int t = 2; t >= 1; t--)
            {
                sp.TrailX[t] = sp.TrailX[t - 1];
                sp.TrailY[t] = sp.TrailY[t - 1];
            }
            sp.TrailX[0] = sp.X;
            sp.TrailY[0] = sp.Y;
            if (sp.TrailCount < 3) sp.TrailCount++;

            sp.X += sp.Vx;
            sp.Y += sp.Vy;
            sp.Vx *= 0.945f;
            sp.Vy = sp.Vy * 0.945f + 0.042f;
            sp.Life++;
            if (sp.Life >= sp.MaxLife || sp.Y >= _pondTopY) _sparks.RemoveAt(i);
        }

        if (!_meteor.Active && (frame % 62) == 18)
        {
            _meteor.Active = true;
            _meteor.X = _rng.Next(Math.Max(8, _pw / 5), Math.Max(14, (int)(_pw * 0.80f)));
            _meteor.Y = _rng.Next(2, Math.Max(4, _ph / 8));
            _meteor.Vx = (frame % 124 < 62) ? 2.6f : -2.6f;
            _meteor.Vy = 1.15f;
            _meteor.Life = 0;
            _meteor.MaxLife = 20;
        }
        else if (_meteor.Active)
        {
            _meteor.X += _meteor.Vx;
            _meteor.Y += _meteor.Vy;
            _meteor.Life++;
            if (_meteor.Life >= _meteor.MaxLife || _meteor.X < 1 || _meteor.X >= _pw - 1 || _meteor.Y >= _auroraLimitY)
            {
                _meteor.Active = false;
            }
        }

        if (_hasCabin)
        {
            if ((frame % 4) == 0 && _smokePuffs.Count < 18)
            {
                SmokePuff sm = new SmokePuff();
                sm.X = _chimneyX + 0.5f;
                sm.Y = _chimneyY;
                sm.Vx = 0.08f + (float)(_rng.NextDouble() * 0.08f);
                sm.Vy = -(0.22f + (float)(_rng.NextDouble() * 0.12f));
                sm.Life = 0;
                sm.MaxLife = 26 + _rng.Next(0, 8);
                _smokePuffs.Add(sm);
            }

            for (int i = _smokePuffs.Count - 1; i >= 0; i--)
            {
                SmokePuff sm = _smokePuffs[i];
                sm.X += sm.Vx + (float)Math.Sin((frame + i) * 0.22) * 0.06f;
                sm.Y += sm.Vy;
                sm.Life++;
                if (sm.Life >= sm.MaxLife || sm.Y < 1) _smokePuffs.RemoveAt(i);
            }
        }

        int sMode = snowLevel % 3;
        int activeSnow = GetActiveSnowCount(sMode);
        float speedMul = (sMode == 2) ? 1.85f : ((sMode == 1) ? 1.30f : 0.85f);
        float windX = (sMode == 2) ? 0.42f : ((sMode == 1) ? 0.12f : 0.03f);

        for (int i = 0; i < activeSnow; i++)
        {
            Snowflake sf = _snowflakes[i];
            sf.Y += sf.Speed * speedMul;
            sf.X += windX + (float)Math.Sin((frame + sf.Phase) * 0.12) * 0.16f;
            if (sf.Y >= _ph - 1) { sf.Y = 0.0f; sf.X = (float)(_rng.NextDouble() * _pw); }
            if (sf.X < 0) sf.X += _pw;
            if (sf.X >= _pw) sf.X -= _pw;
        }
    }

    private int GetActiveSnowCount(int snowMode)
    {
        int baseCount = Math.Max(36, (_pw * _ph) / 145);
        int mul = (snowMode == 2) ? 2 : 1;
        return Math.Min(_snowflakes.Count, (snowMode == 1) ? (int)(baseCount * 1.4f) : baseCount * mul);
    }

    public byte[] GetOutputBuffer()
    {
        return _outBuf;
    }

    public string RenderFrame(int width, int height, string petName, int frame, int lightMode, int snowLevel, bool vtEnabled, bool forceFullRedraw)
    {
        int len = RenderFrameBytes(width, height, petName, frame, lightMode, snowLevel, vtEnabled, forceFullRedraw);
        return (len > 0) ? Encoding.UTF8.GetString(_outBuf, 0, len) : string.Empty;
    }

    public int RenderFrameBytes(int width, int height, string petName, int frame, int lightMode, int snowLevel, bool vtEnabled, bool forceFullRedraw)
    {
        bool resized = (width != _w || height != _h);
        if (resized)
        {
            RebuildStaticScene(width, height);
            forceFullRedraw = true;
        }

        StepDynamicAnimations(frame, snowLevel);
        UpdateAuroraCacheIfNeeded(frame);

        int auroraPx = _auroraLimitY * _pw;
        int pxTotal = _pw * _ph;
        Array.Copy(_skyR, 0, _r, 0, auroraPx);
        Array.Copy(_skyG, 0, _g, 0, auroraPx);
        Array.Copy(_skyB, 0, _b, 0, auroraPx);
        Array.Copy(_baseR, auroraPx, _r, auroraPx, pxTotal - auroraPx);
        Array.Copy(_baseG, auroraPx, _g, auroraPx, pxTotal - auroraPx);
        Array.Copy(_baseB, auroraPx, _b, auroraPx, pxTotal - auroraPx);

        // B) 밤하늘 별자리 반짝임 (3프레임 단위 위상으로 배경 노이즈 최소화)
        int starPhaseFrame = (frame / 3) * 3;
        int activeStars = Math.Min(_stars.Count, Math.Max(30, (_pw * _ph) / 130));
        for (int i = 0; i < activeStars; i++)
        {
            SkyStar st = _stars[i];
            int sx = ClampInt((int)(st.Rx * (_pw - 2)) + 1, 0, _pw - 1);
            int sy = ClampInt((int)(st.Ry * (_ph * 0.72f)), 0, _ph - 1);
            int idx = sy * _pw + sx;
            if (_occludeMask[idx] != 0) continue;

            float twinkle = 0.5f + 0.5f * (float)Math.Sin(starPhaseFrame * 0.22f + st.Phase);
            float lum = st.BaseIntensity * (0.28f + 0.78f * twinkle);
            if (st.ColorIndex == 0) { _r[idx] += lum * 1.0f; _g[idx] += lum * 0.96f; _b[idx] += lum * 0.82f; }
            else if (st.ColorIndex == 1) { _r[idx] += lum * 0.72f; _g[idx] += lum * 0.88f; _b[idx] += lum * 1.05f; }
            else if (st.ColorIndex == 2) { _r[idx] += lum * 1.0f; _g[idx] += lum * 0.80f; _b[idx] += lum * 0.65f; }
            else { _r[idx] += lum * 0.88f; _g[idx] += lum * 0.80f; _b[idx] += lum * 1.05f; }
        }

        // C) 유성우
        if (_meteor.Active)
        {
            for (int t = 0; t < 11; t++)
            {
                float fx = _meteor.X - _meteor.Vx * t * 0.42f;
                float fy = _meteor.Y - _meteor.Vy * t * 0.42f;
                float fade = 1.0f - (t / 11.0f);
                if (fade <= 0.0f) continue;
                AddDualBloomSky(fx, fy, (t == 0) ? 1.1f : 0.75f, (t == 0) ? 2.4f : 1.3f, 1.0f * fade, (0.88f - t * 0.03f) * fade, (0.55f + t * 0.04f) * fade);
            }
        }

        // D) 불꽃놀이 로켓 · 플래시 · 이중 링 파티클
        for (int i = 0; i < _flashes.Count; i++)
        {
            BurstFlash fl = _flashes[i];
            float fade = 1.0f - ((float)fl.Life / fl.MaxLife);
            float fr = ((fl.Color >> 16) & 255) / 255.0f;
            float fg = ((fl.Color >> 8) & 255) / 255.0f;
            float fb = (fl.Color & 255) / 255.0f;
            AddGlowClippedSky(fl.X, fl.Y, 6.8f, fr * fade * 0.75f, fg * fade * 0.75f, fb * fade * 0.75f);
        }

        for (int i = 0; i < _rockets.Count; i++)
        {
            Rocket rk = _rockets[i];
            AddDualBloomSky(rk.X, rk.Y, 0.9f, 2.0f, 1.2f, 1.05f, 0.75f);
            float rr = ((rk.Color1 >> 16) & 255) / 255.0f;
            float rg = ((rk.Color1 >> 8) & 255) / 255.0f;
            float rb = (rk.Color1 & 255) / 255.0f;
            for (int t = 0; t < rk.TrailCount; t++)
            {
                float tf = (1.0f - t * 0.24f) * 0.85f;
                AddGlowClippedSky(rk.TrailX[t], rk.TrailY[t], 1.0f, rr * tf, rg * tf, rb * tf);
            }
        }

        for (int i = 0; i < _sparks.Count; i++)
        {
            Spark sp = _sparks[i];
            float lr = (float)sp.Life / sp.MaxLife;
            float fade = 1.0f - lr;
            if (sp.HasCrackle && lr > 0.65f && ((frame + i) % 3 == 0)) fade *= 1.45f;

            float sr = ((sp.Color >> 16) & 255) / 255.0f;
            float sg = ((sp.Color >> 8) & 255) / 255.0f;
            float sbCol = (sp.Color & 255) / 255.0f;

            AddDualBloomSky(sp.X, sp.Y, 0.85f, 1.9f, sr * fade * 1.1f, sg * fade * 1.1f, sbCol * fade * 1.1f);
            for (int t = 0; t < sp.TrailCount; t++)
            {
                float tf = fade * (0.52f - t * 0.16f);
                if (tf > 0.02f) AddGlowClippedSky(sp.TrailX[t], sp.TrailY[t], 0.85f, sr * tf, sg * tf, sbCol * tf);
            }
        }

        // E) 트리 꼭대기 8방향 크리스마스 별 + 회전 렌즈 플레어
        float starPulse = 0.88f + 0.22f * (float)Math.Sin((frame / 2) * 0.48f);
        AddGlow(_starX, _starY, _starBaseRadius * 2.1f, 0.85f * starPulse, 0.64f * starPulse, 0.20f * starPulse);

        float rotAngle = (frame / 2) * 0.10f;
        for (int ray = 0; ray < 8; ray++)
        {
            double ang = (Math.PI * 0.25 * ray) + ((ray % 2 == 1) ? rotAngle * 0.25 : 0.0);
            float rayLen = ((ray % 2 == 0) ? _starBaseRadius * 1.42f : _starBaseRadius * 0.90f) * starPulse;
            int steps = Math.Max(3, (int)Math.Ceiling(rayLen));
            float cosA = (float)Math.Cos(ang);
            float sinA = (float)Math.Sin(ang);
            for (int s = 0; s <= steps; s++)
            {
                float d = (float)s / steps * rayLen;
                float lum = (1.0f - (float)s / (steps + 0.5f)) * 1.35f;
                AddGlow(_starX + cosA * d, _starY + sinA * d, 0.8f, 1.05f * lum, 0.92f * lum, 0.42f * lum);
            }
        }
        AddGlow(_starX, _starY, 1.2f, 1.5f, 1.4f, 1.0f);

        // F) 흐르는 3D 나선 리본 가랜드 반짝임
        float garlandTime = (frame / 2) * 0.56f;
        for (int i = 0; i < _garlandPixels.Count; i++)
        {
            int idx = _garlandPixels[i];
            int gx = idx % _pw;
            int gy = idx / _pw;
            float wave = 0.55f + 0.45f * (float)Math.Sin(gx * 0.35f + gy * 0.25f - garlandTime);
            _r[idx] += 0.52f * wave;
            _g[idx] += 0.42f * wave;
            _b[idx] += 0.20f * wave;
        }

        // G) 대형 보석 오너먼트 + 요정 전구 + 지면 반사광
        int mode = lightMode % 4;
        int bulbFrame = (frame / 2) * 2;
        for (int i = 0; i < _ornaments.Count; i++)
        {
            Ornament orn = _ornaments[i];
            int colorRgb = GetOrnamentRgb(orn, bulbFrame, mode);
            float orr = ((colorRgb >> 16) & 255) / 255.0f;
            float org = ((colorRgb >> 8) & 255) / 255.0f;
            float orb = (colorRgb & 255) / 255.0f;

            float twinkle = orn.IsFairy
                ? (0.38f + 0.82f * Math.Max(0.0f, (float)Math.Sin(bulbFrame * 0.35f + orn.Phase)))
                : (0.82f + 0.42f * Math.Max(0.0f, (float)Math.Sin(bulbFrame * 0.25f + orn.Phase)));

            if (orn.IsFairy)
            {
                AddGlow(orn.X, orn.Y, 1.15f, orr * twinkle * 0.95f, org * twinkle * 0.95f, orb * twinkle * 0.95f);
            }
            else
            {
                AddGlow(orn.X, orn.Y, orn.Size * 0.85f, orr * twinkle * 1.15f, org * twinkle * 1.15f, orb * twinkle * 1.15f);
                AddGlow(orn.X, orn.Y, orn.Size * 1.85f, orr * twinkle * 0.42f, org * twinkle * 0.42f, orb * twinkle * 0.42f);
                AddGlow(orn.X - 0.25f, orn.Y - 0.25f, 0.7f, 0.55f * twinkle, 0.55f * twinkle, 0.55f * twinkle);
            }

            if (!orn.IsFairy && _treeBottomY - orn.Y <= 8)
            {
                int gy = _frontGroundY[ClampInt(orn.X, 0, _pw - 1)];
                AddGlow(orn.X, gy + 1.0f, 3.6f, orr * 0.22f * twinkle, org * 0.22f * twinkle, orb * 0.22f * twinkle);
            }
        }

        // H) 오두막 창문 불빛, 굴뚝 연기 & 가로등 플리커
        if (_hasCabin)
        {
            float fireFlicker = 0.86f + 0.14f * (float)Math.Sin(bulbFrame * 0.38f);
            float sumWx = 0.0f;
            float sumWy = 0.0f;
            int winCount = _cabinWindowPixels.Count;
            for (int i = 0; i < winCount; i++)
            {
                int idx = _cabinWindowPixels[i];
                sumWx += (idx % _pw);
                sumWy += (idx / _pw);
                _r[idx] = 1.35f * fireFlicker;
                _g[idx] = 0.88f * fireFlicker;
                _b[idx] = 0.30f * fireFlicker;
            }
            if (winCount > 0)
            {
                float centerWx = sumWx / winCount;
                float centerWy = sumWy / winCount;
                AddGlow(centerWx, centerWy, 5.2f, 0.42f * fireFlicker, 0.24f * fireFlicker, 0.05f * fireFlicker);
                int wGroundY = _frontGroundY[ClampInt((int)centerWx, 0, _pw - 1)];
                AddGlow(centerWx, wGroundY + 1.0f, 5.0f, 0.30f * fireFlicker, 0.16f * fireFlicker, 0.03f * fireFlicker);
            }

            for (int i = 0; i < _smokePuffs.Count; i++)
            {
                SmokePuff sm = _smokePuffs[i];
                float fade = 1.0f - (sm.Life / sm.MaxLife);
                float rad = 0.8f + (sm.Life / sm.MaxLife) * 1.4f;
                AddGlow(sm.X, sm.Y, rad, 0.22f * fade, 0.26f * fade, 0.34f * fade);
            }
        }

        if (_hasLantern)
        {
            float lampPulse = 0.90f + 0.12f * (float)Math.Sin(bulbFrame * 0.30f + 1.2f);
            AddGlow(_lanternX, _lanternY, 5.4f, 0.68f * lampPulse, 0.46f * lampPulse, 0.13f * lampPulse);
            int lgY = _frontGroundY[ClampInt(_lanternX, 0, _pw - 1)];
            AddGlow(_lanternX, lgY + 0.8f, 4.4f, 0.32f * lampPulse, 0.20f * lampPulse, 0.04f * lampPulse);
        }

        // I) 전경 겨울 얼음 연못 실시간 수면 반사 (2프레임 단위 위상 갱신)
        int rippleFrame = (frame / 2) * 2;
        int pondStartY = Math.Max(0, _pondTopY - 1);
        float mirrorBaseY = _trunkBottomY - 0.5f;
        float reflectScale = Math.Max(2.4f, Math.Min(4.2f, _treeH / Math.Max(4.0f, _pondRY * 2.0f)));
        for (int y = pondStartY; y < _ph; y++)
        {
            int rowBase = y * _pw;
            float rippleOffset = (float)Math.Sin(y * 1.45f + rippleFrame * 0.24f) * 1.25f;
            float shimmerLine = 0.5f + 0.5f * (float)Math.Sin(y * 2.1f - rippleFrame * 0.32f);
            float pondOffsetY = Math.Max(0.0f, y - _pondTopY);
            int srcY = (int)Math.Round(mirrorBaseY - pondOffsetY * reflectScale);
            if (srcY < 0 || srcY >= _ph) continue;
            int srcRowBase = srcY * _pw;

            for (int x = 0; x < _pw; x++)
            {
                int idx = rowBase + x;
                byte pwVal = _pondMask[idx];
                if (pwVal == 0) continue;

                int srcX = ClampInt((int)Math.Round(x + rippleOffset), 0, _pw - 1);
                int srcIdx = srcRowBase + srcX;

                float refWeight = (pwVal / 255.0f) * (0.52f + 0.14f * shimmerLine);
                _r[idx] = Lerp(_r[idx], _r[srcIdx] * 0.88f, refWeight);
                _g[idx] = Lerp(_g[idx], _g[srcIdx] * 0.94f, refWeight);
                _b[idx] = Lerp(_b[idx], _b[srcIdx] * 1.06f, refWeight);
            }
        }

        // J) 3단계 피사계 심도 눈송이
        int drawSnow = GetActiveSnowCount(snowLevel % 3);
        for (int i = 0; i < drawSnow; i++)
        {
            Snowflake sf = _snowflakes[i];
            int sx = ((int)Math.Round(sf.X)) % _pw;
            if (sx < 0) sx += _pw;
            int sy = ClampInt((int)Math.Floor(sf.Y), 0, _ph - 1);

            if (sf.Depth == 0)
            {
                int idx = sy * _pw + sx;
                if (_occludeMask[idx] == 2) continue;
                _r[idx] += 0.36f; _g[idx] += 0.44f; _b[idx] += 0.58f;
            }
            else if (sf.Depth == 1)
            {
                int idx = sy * _pw + sx;
                _r[idx] += 0.76f; _g[idx] += 0.82f; _b[idx] += 0.92f;
            }
            else
            {
                AddGlow(sf.X, sf.Y, 1.45f, 0.64f, 0.72f, 0.86f);
            }
        }

        // K) ACES Filmic 톤매핑 + Bayer 2x2 디더링 → Half-Block(▀) 셀 버퍼 변환
        int gfxRows = _ph / 2;
        for (int y = 0; y < gfxRows; y++)
        {
            int pyTop = y * 2;
            int pyBot = pyTop + 1;
            int rowBase = y * _w;
            int topOffset = pyTop * _pw;
            int botOffset = pyBot * _pw;

            for (int x = 0; x < _w; x++)
            {
                int topIdx = topOffset + x;
                int botIdx = botOffset + x;
                int topRgb = ToneMapAces(_r[topIdx], _g[topIdx], _b[topIdx], x, pyTop);
                int botRgb = ToneMapAces(_r[botIdx], _g[botIdx], _b[botIdx], x, pyBot);

                int cIdx = rowBase + x;
                if (topRgb == botRgb)
                {
                    _currChars[cIdx] = ' ';
                    _currFg[cIdx] = topRgb;
                    _currBg[cIdx] = topRgb;
                }
                else
                {
                    _currChars[cIdx] = '\u2580';
                    _currFg[cIdx] = topRgb;
                    _currBg[cIdx] = botRgb;
                }
                _currBold[cIdx] = false;
            }
        }

        // L) 하단 3줄 HUD 오버레이 (인사 배너 + 현재 모드 상태 + 고대비 키캡 안내 바)
        int hudBg = PackRgb(8, 13, 28);
        int guideBarBg = PackRgb(14, 22, 44);
        int bannerRow = _h - 3;
        int statusRow = _h - 2;
        int guideRow = _h - 1;
        FillHudRowBackground(bannerRow, hudBg);
        FillHudRowBackground(statusRow, hudBg);
        FillHudRowBackground(guideRow, guideBarBg);

        string safeName = SanitizeName(petName);
        DrawGradientBanner(bannerRow, "~*~  Merry Christmas, " + safeName + "!  ~*~", frame, hudBg);

        string modeName = LightModeNames[mode];
        string sName = SnowModeNames[snowLevel % 3];
        string statusFull = "조명 " + (mode + 1) + "/4: " + modeName + "    |    눈 세기: " + sName;
        string statusShort = modeName + " | " + sName;
        string statusText = (_w >= 72) ? statusFull : statusShort;
        DrawCenteredGuide(statusRow, statusText, PackRgb(198, 220, 248), hudBg, true);

        DrawKeyGuideBar(guideRow, guideBarBg);

        return BuildAnsiBytes(gfxRows, bannerRow, statusRow, guideRow, vtEnabled, forceFullRedraw);
    }

    public void RunLoop(string petName, bool vtEnabled)
    {
        Stream stdout = Console.OpenStandardOutput();
        int frame = 0;
        int lightMode = 0;
        int snowLevel = 1;
        int lastWinW = -1;
        int lastWinH = -1;
        int winW = 164;
        int winH = 48;
        bool shouldExit = false;

        try
        {
            if (vtEnabled)
            {
                WriteBytesDirect(stdout, "\x1B[?1049h\x1B[?25l\x1B[2J\x1B[H");
            }
            else
            {
                try { Console.Clear(); Console.CursorVisible = false; } catch { }
            }

            Stopwatch sw = new Stopwatch();
            while (!shouldExit)
            {
                sw.Reset();
                sw.Start();

                try
                {
                    while (Console.KeyAvailable)
                    {
                        ConsoleKeyInfo keyInfo = Console.ReadKey(true);
                        if (keyInfo.Key == ConsoleKey.Q || keyInfo.Key == ConsoleKey.Escape)
                        {
                            shouldExit = true;
                            break;
                        }
                        if (keyInfo.Key == ConsoleKey.Spacebar)
                        {
                            lightMode = (lightMode + 1) % 4;
                        }
                        else if (keyInfo.Key == ConsoleKey.F)
                        {
                            TriggerFirework(3);
                        }
                        else if (keyInfo.Key == ConsoleKey.S)
                        {
                            snowLevel = (snowLevel + 1) % 3;
                        }
                        else if (keyInfo.Key == ConsoleKey.F11)
                        {
                            LabPetConsoleNative.TryToggleMaximizeConsoleWindow();
                        }
                    }
                }
                catch
                {
                }

                if (shouldExit) break;

                // 창 크기 확인은 8프레임마다 수행해 콘솔 API 왕복을 줄인다
                if ((frame & 7) == 0 || lastWinW < 0)
                {
                    try
                    {
                        winW = Console.WindowWidth;
                        winH = Console.WindowHeight;
                    }
                    catch
                    {
                    }
                }

                bool forceFull = (frame == 0);
                if (winW != lastWinW || winH != lastWinH)
                {
                    lastWinW = winW;
                    lastWinH = winH;
                    forceFull = true;
                    if (vtEnabled)
                    {
                        WriteBytesDirect(stdout, "\x1B[2J\x1B[H");
                    }
                    else
                    {
                        try { Console.Clear(); } catch { }
                    }
                }

                if (frame > 0 && (frame % 200) == 0)
                {
                    lightMode = (lightMode + 1) % 4;
                }

                int outLen = RenderFrameBytes(winW, winH, petName, frame, lightMode, snowLevel, vtEnabled, forceFull);
                if (!vtEnabled)
                {
                    try { Console.SetCursorPosition(0, 0); } catch { }
                }

                if (outLen > 0)
                {
                    stdout.Write(_outBuf, 0, outLen);
                    stdout.Flush();
                }

                frame++;

                // 정밀 25fps(40ms/frame) 페이싱 (남은 시간 동안 12ms 단위 짧은 슬립으로 키 입력 즉시 반응)
                long elapsedMs = sw.ElapsedMilliseconds;
                int remainMs = (int)(40L - elapsedMs);
                while (remainMs > 0)
                {
                    try
                    {
                        if (Console.KeyAvailable) break;
                    }
                    catch
                    {
                    }
                    int slice = (remainMs > 12) ? 12 : remainMs;
                    Thread.Sleep(slice);
                    remainMs -= slice;
                }
            }
        }
        finally
        {
            if (vtEnabled)
            {
                WriteBytesDirect(stdout, "\x1B[0m\x1B[?25h\x1B[?1049l");
            }
            else
            {
                try { Console.CursorVisible = true; } catch { }
                try { Console.WriteLine(); } catch { }
            }
        }
    }

    private static void WriteBytesDirect(Stream stdout, string ascii)
    {
        byte[] b = Encoding.ASCII.GetBytes(ascii);
        stdout.Write(b, 0, b.Length);
        stdout.Flush();
    }

    private int BuildAnsiBytes(int gfxRows, int bannerRow, int statusRow, int guideRow, bool vtEnabled, bool fullRedraw)
    {
        _outLen = 0;

        if (!vtEnabled)
        {
            for (int y = 0; y < _h; y++)
            {
                int maxCols = (y == _h - 1) ? (_w - 1) : _w;
                for (int x = 0; x < maxCols; x++)
                {
                    char ch = _currChars[y * _w + x];
                    if (ch == (char)1) continue;
                    WriteUtf8Char((ch == '\u2580') ? '#' : ch);
                }
                if (y < _h - 1) WriteByte((byte)'\n');
            }
            CopyCurrentToPrev();
            return _outLen;
        }

        if (fullRedraw)
        {
            WriteByte(27); WriteByte((byte)'['); WriteByte((byte)'H');

            for (int y = 0; y < _h; y++)
            {
                int maxCols = (y == _h - 1) ? (_w - 1) : _w;
                int lastFg = -1;
                int lastBg = -1;
                bool lastBold = false;

                for (int x = 0; x < maxCols; x++)
                {
                    int idx = y * _w + x;
                    char ch = _currChars[idx];
                    if (ch == (char)1) continue;

                    int fg = _currFg[idx];
                    int bg = _currBg[idx];
                    bool bold = _currBold[idx];

                    if (bold != lastBold)
                    {
                        WriteByte(27);
                        WriteAscii(bold ? "[1m" : "[22m");
                        lastBold = bold;
                    }
                    if (bg != lastBg)
                    {
                        WriteBgRgb(bg);
                        lastBg = bg;
                    }
                    if (ch != ' ' && fg != lastFg)
                    {
                        WriteFgRgb(fg);
                        lastFg = fg;
                    }

                    WriteUtf8Char(ch);
                }

                WriteByte(27); WriteAscii("[0m");
                if (y < _h - 1) WriteByte((byte)'\n');
            }

            CopyCurrentToPrev();
            return _outLen;
        }

        // 고속 차분 출력: 상대 커서 이동(ESC[nC), ±6 허용 오차, 그래픽 영역 bold 생략
        int curY = -1;
        int curX = -1;
        int activeFg = -1;
        int activeBg = -1;
        bool activeBold = false;

        for (int y = 0; y < gfxRows; y++)
        {
            int rowBase = y * _w;
            for (int x = 0; x < _w; x++)
            {
                int idx = rowBase + x;
                char ch = _currChars[idx];
                int fg = _currFg[idx];
                int bg = _currBg[idx];

                bool same = (ch == _prevChars[idx])
                    && (!_prevBold[idx])
                    && IsRgbNear(bg, _prevBg[idx], 8)
                    && (ch == ' ' || IsRgbNear(fg, _prevFg[idx], 8));

                if (same) continue;

                if (y == curY && x >= curX)
                {
                    int dx = x - curX;
                    if (dx > 0)
                    {
                        WriteByte(27);
                        WriteByte((byte)'[');
                        if (dx > 1) WriteInt(dx);
                        WriteByte((byte)'C');
                    }
                }
                else
                {
                    WriteByte(27);
                    WriteByte((byte)'[');
                    WriteInt(y + 1);
                    WriteByte((byte)';');
                    WriteInt(x + 1);
                    WriteByte((byte)'H');
                }

                if (bg != activeBg)
                {
                    WriteBgRgb(bg);
                    activeBg = bg;
                }
                if (ch != ' ' && fg != activeFg)
                {
                    WriteFgRgb(fg);
                    activeFg = fg;
                }

                WriteUtf8Char(ch);
                _prevChars[idx] = ch;
                _prevFg[idx] = fg;
                _prevBg[idx] = bg;
                _prevBold[idx] = false;
                curY = y;
                curX = x + 1;
            }
        }

        EmitHudRowIfChanged(bannerRow, _w, ref activeFg, ref activeBg, ref activeBold);
        EmitHudRowIfChanged(statusRow, _w, ref activeFg, ref activeBg, ref activeBold);
        EmitHudRowIfChanged(guideRow, _w - 1, ref activeFg, ref activeBg, ref activeBold);

        if (_outLen > 0)
        {
            WriteByte(27);
            WriteAscii("[0m");
        }

        return _outLen;
    }

    private void EmitHudRowIfChanged(int y, int maxCols, ref int activeFg, ref int activeBg, ref bool activeBold)
    {
        if (y < 0 || y >= _h) return;

        bool changed = false;
        int rowBase = y * _w;
        for (int x = 0; x < maxCols; x++)
        {
            int idx = rowBase + x;
            if (_currChars[idx] != _prevChars[idx]
                || _currFg[idx] != _prevFg[idx]
                || _currBg[idx] != _prevBg[idx]
                || _currBold[idx] != _prevBold[idx])
            {
                changed = true;
                break;
            }
        }

        if (!changed) return;

        WriteByte(27);
        WriteByte((byte)'[');
        WriteInt(y + 1);
        WriteAscii(";1H");

        for (int x = 0; x < maxCols; x++)
        {
            int idx = rowBase + x;
            char ch = _currChars[idx];
            if (ch == (char)1) continue;

            int fg = _currFg[idx];
            int bg = _currBg[idx];
            bool bold = _currBold[idx];

            if (bold != activeBold)
            {
                WriteByte(27);
                WriteAscii(bold ? "[1m" : "[22m");
                activeBold = bold;
            }
            if (bg != activeBg)
            {
                WriteBgRgb(bg);
                activeBg = bg;
            }
            if (ch != ' ' && fg != activeFg)
            {
                WriteFgRgb(fg);
                activeFg = fg;
            }
            WriteUtf8Char(ch);
        }

        for (int x = 0; x < _w; x++)
        {
            int idx = rowBase + x;
            _prevChars[idx] = _currChars[idx];
            _prevFg[idx] = _currFg[idx];
            _prevBg[idx] = _currBg[idx];
            _prevBold[idx] = _currBold[idx];
        }
    }

    private void EnsureOutCapacity(int needed)
    {
        if (_outLen + needed <= _outBuf.Length) return;
        int newCap = _outBuf.Length * 2;
        while (newCap < _outLen + needed) newCap *= 2;
        Array.Resize(ref _outBuf, newCap);
    }

    private void WriteByte(byte b)
    {
        EnsureOutCapacity(1);
        _outBuf[_outLen++] = b;
    }

    private void WriteAscii(string s)
    {
        int len = s.Length;
        EnsureOutCapacity(len);
        for (int i = 0; i < len; i++)
        {
            _outBuf[_outLen++] = (byte)s[i];
        }
    }

    private void WriteInt(int val)
    {
        EnsureOutCapacity(6);
        if (val >= 100)
        {
            if (val >= 1000)
            {
                _outBuf[_outLen++] = (byte)('0' + (val / 1000) % 10);
            }
            _outBuf[_outLen++] = (byte)('0' + (val / 100) % 10);
            _outBuf[_outLen++] = (byte)('0' + (val / 10) % 10);
            _outBuf[_outLen++] = (byte)('0' + (val % 10));
        }
        else if (val >= 10)
        {
            _outBuf[_outLen++] = (byte)('0' + (val / 10));
            _outBuf[_outLen++] = (byte)('0' + (val % 10));
        }
        else
        {
            _outBuf[_outLen++] = (byte)('0' + val);
        }
    }

    private void WriteFgRgb(int rgb)
    {
        EnsureOutCapacity(20);
        _outBuf[_outLen++] = 27;
        _outBuf[_outLen++] = (byte)'[';
        _outBuf[_outLen++] = (byte)'3';
        _outBuf[_outLen++] = (byte)'8';
        _outBuf[_outLen++] = (byte)';';
        _outBuf[_outLen++] = (byte)'2';
        _outBuf[_outLen++] = (byte)';';
        WriteInt((rgb >> 16) & 255);
        _outBuf[_outLen++] = (byte)';';
        WriteInt((rgb >> 8) & 255);
        _outBuf[_outLen++] = (byte)';';
        WriteInt(rgb & 255);
        _outBuf[_outLen++] = (byte)'m';
    }

    private void WriteBgRgb(int rgb)
    {
        EnsureOutCapacity(20);
        _outBuf[_outLen++] = 27;
        _outBuf[_outLen++] = (byte)'[';
        _outBuf[_outLen++] = (byte)'4';
        _outBuf[_outLen++] = (byte)'8';
        _outBuf[_outLen++] = (byte)';';
        _outBuf[_outLen++] = (byte)'2';
        _outBuf[_outLen++] = (byte)';';
        WriteInt((rgb >> 16) & 255);
        _outBuf[_outLen++] = (byte)';';
        WriteInt((rgb >> 8) & 255);
        _outBuf[_outLen++] = (byte)';';
        WriteInt(rgb & 255);
        _outBuf[_outLen++] = (byte)'m';
    }

    private void WriteUtf8Char(char ch)
    {
        if (ch < 0x80)
        {
            EnsureOutCapacity(1);
            _outBuf[_outLen++] = (byte)ch;
            return;
        }
        if (ch == '\u2580')
        {
            EnsureOutCapacity(3);
            _outBuf[_outLen++] = 0xE2;
            _outBuf[_outLen++] = 0x96;
            _outBuf[_outLen++] = 0x80;
            return;
        }
        if (ch < 0x800)
        {
            EnsureOutCapacity(2);
            _outBuf[_outLen++] = (byte)(0xC0 | (ch >> 6));
            _outBuf[_outLen++] = (byte)(0x80 | (ch & 0x3F));
            return;
        }
        EnsureOutCapacity(3);
        _outBuf[_outLen++] = (byte)(0xE0 | (ch >> 12));
        _outBuf[_outLen++] = (byte)(0x80 | ((ch >> 6) & 0x3F));
        _outBuf[_outLen++] = (byte)(0x80 | (ch & 0x3F));
    }

    private static bool IsRgbNear(int c1, int c2, int tol)
    {
        if (c1 == c2) return true;
        int dr = ((c1 >> 16) & 255) - ((c2 >> 16) & 255);
        if (dr < -tol || dr > tol) return false;
        int dg = ((c1 >> 8) & 255) - ((c2 >> 8) & 255);
        if (dg < -tol || dg > tol) return false;
        int db = (c1 & 255) - (c2 & 255);
        return db >= -tol && db <= tol;
    }

    private void CopyCurrentToPrev()
    {
        Array.Copy(_currChars, _prevChars, _currChars.Length);
        Array.Copy(_currFg, _prevFg, _currFg.Length);
        Array.Copy(_currBg, _prevBg, _currBg.Length);
        Array.Copy(_currBold, _prevBold, _currBold.Length);
    }

    private void FillHudRowBackground(int y, int bg)
    {
        if (y < 0 || y >= _h) return;
        int rowBase = y * _w;
        for (int x = 0; x < _w; x++)
        {
            int idx = rowBase + x;
            _currChars[idx] = ' ';
            _currFg[idx] = PackRgb(225, 238, 252);
            _currBg[idx] = bg;
            _currBold[idx] = false;
        }
    }

    private void DrawGradientBanner(int y, string text, int frame, int bg)
    {
        if (y < 0 || y >= _h) return;
        int dispW = GetDisplayWidth(text);
        int startCol = Math.Max(0, (_w - dispW) / 2);

        int[] bannerPalette = new int[]
        {
            PackRgb(255, 102, 134),
            PackRgb(255, 224, 98),
            PackRgb(108, 245, 168),
            PackRgb(98, 228, 255),
            PackRgb(255, 152, 218)
        };

        int col = startCol;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            int cw = IsWideChar(ch) ? 2 : 1;
            if (col + cw >= _w) break;

            int colorIdx = Math.Abs(i - (frame / 2)) % bannerPalette.Length;
            int fg = (ch == '~' || ch == '*')
                ? (((frame % 4) < 2) ? PackRgb(255, 236, 112) : PackRgb(255, 128, 178))
                : bannerPalette[colorIdx];

            int idx = y * _w + col;
            _currChars[idx] = ch;
            _currFg[idx] = fg;
            _currBg[idx] = bg;
            _currBold[idx] = true;

            if (cw == 2 && col + 1 < _w)
            {
                int idx2 = y * _w + col + 1;
                _currChars[idx2] = (char)1;
                _currFg[idx2] = fg;
                _currBg[idx2] = bg;
                _currBold[idx2] = true;
            }
            col += cw;
        }
    }

    private void DrawCenteredGuide(int y, string text, int fgColor, int bg, bool bold)
    {
        if (y < 0 || y >= _h) return;
        int maxCols = _w - 1;
        StringBuilder clipped = new StringBuilder();
        int curW = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            int cw = IsWideChar(ch) ? 2 : 1;
            if (curW + cw > maxCols) break;
            clipped.Append(ch);
            curW += cw;
        }

        string s = clipped.ToString();
        int startCol = Math.Max(0, (maxCols - curW) / 2);
        DrawHudSegment(y, startCol, maxCols, s, fgColor, bg, bold);
    }

    private void DrawKeyGuideBar(int y, int barBg)
    {
        if (y < 0 || y >= _h) return;
        int maxCols = _w - 1;

        string[] keys;
        string[] labels;
        int gap;

        if (_w >= 112)
        {
            keys = new string[] { " SPACE ", " F ", " S ", " F11 ", " Q / ESC " };
            labels = new string[] { " 조명 변경", " 폭죽 발사", " 눈 세기", " 창 크게", " 종료" };
            gap = 3;
        }
        else if (_w >= 76)
        {
            keys = new string[] { " SPACE ", " F ", " S ", " Q / ESC " };
            labels = new string[] { " 조명", " 폭죽", " 눈", " 종료" };
            gap = 2;
        }
        else
        {
            keys = new string[] { "SPACE", "F", "S", "Q" };
            labels = new string[] { ":조명", ":폭죽", ":눈", ":종료" };
            gap = 1;
        }

        int totalW = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            totalW += GetDisplayWidth(keys[i]) + GetDisplayWidth(labels[i]);
            if (i < keys.Length - 1) totalW += gap;
        }

        int col = Math.Max(0, (maxCols - totalW) / 2);
        int keyBg = PackRgb(42, 66, 112);
        int keyFg = PackRgb(255, 234, 128);
        int labelFg = PackRgb(242, 248, 255);

        for (int i = 0; i < keys.Length; i++)
        {
            col = DrawHudSegment(y, col, maxCols, keys[i], keyFg, keyBg, true);
            col = DrawHudSegment(y, col, maxCols, labels[i], labelFg, barBg, true);
            if (i < keys.Length - 1)
            {
                col = Math.Min(maxCols, col + gap);
            }
        }
    }

    private int DrawHudSegment(int y, int col, int maxCols, string text, int fgColor, int bgColor, bool bold)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            int cw = IsWideChar(ch) ? 2 : 1;
            if (col + cw > maxCols) break;

            int idx = y * _w + col;
            _currChars[idx] = ch;
            _currFg[idx] = fgColor;
            _currBg[idx] = bgColor;
            _currBold[idx] = bold;

            if (cw == 2 && col + 1 < maxCols)
            {
                int idx2 = y * _w + col + 1;
                _currChars[idx2] = (char)1;
                _currFg[idx2] = fgColor;
                _currBg[idx2] = bgColor;
                _currBold[idx2] = bold;
            }
            col += cw;
        }
        return col;
    }

    private void AddGlow(float cx, float cy, float radius, float rCol, float gCol, float bCol)
    {
        float r2 = radius * radius;
        if (r2 < 0.05f) return;
        float invR2 = 1.0f / r2;
        int xMin = Math.Max(0, (int)Math.Floor(cx - radius));
        int xMax = Math.Min(_pw - 1, (int)Math.Ceiling(cx + radius));
        int yMin = Math.Max(0, (int)Math.Floor(cy - radius));
        int yMax = Math.Min(_ph - 1, (int)Math.Ceiling(cy + radius));

        for (int y = yMin; y <= yMax; y++)
        {
            float dy = y - cy;
            float dy2 = dy * dy;
            int rowBase = y * _pw;
            for (int x = xMin; x <= xMax; x++)
            {
                float dx = x - cx;
                float d2 = dx * dx + dy2;
                if (d2 >= r2) continue;
                float t = 1.0f - d2 * invR2;
                float falloff = t * t;
                int idx = rowBase + x;
                _r[idx] += rCol * falloff;
                _g[idx] += gCol * falloff;
                _b[idx] += bCol * falloff;
            }
        }
    }

    private void AddGlowClippedSky(float cx, float cy, float radius, float rCol, float gCol, float bCol)
    {
        float r2 = radius * radius;
        if (r2 < 0.05f) return;
        float invR2 = 1.0f / r2;
        int xMin = Math.Max(0, (int)Math.Floor(cx - radius));
        int xMax = Math.Min(_pw - 1, (int)Math.Ceiling(cx + radius));
        int yMin = Math.Max(0, (int)Math.Floor(cy - radius));
        int yMax = Math.Min(_ph - 1, (int)Math.Ceiling(cy + radius));

        for (int y = yMin; y <= yMax; y++)
        {
            float dy = y - cy;
            float dy2 = dy * dy;
            int rowBase = y * _pw;
            for (int x = xMin; x <= xMax; x++)
            {
                int idx = rowBase + x;
                if (_occludeMask[idx] != 0) continue;
                float dx = x - cx;
                float d2 = dx * dx + dy2;
                if (d2 >= r2) continue;
                float t = 1.0f - d2 * invR2;
                float falloff = t * t;
                _r[idx] += rCol * falloff;
                _g[idx] += gCol * falloff;
                _b[idx] += bCol * falloff;
            }
        }
    }

    private void AddDualBloomSky(float cx, float cy, float coreRadius, float haloRadius, float rCol, float gCol, float bCol)
    {
        AddGlowClippedSky(cx, cy, coreRadius, rCol * 1.1f, gCol * 1.1f, bCol * 1.1f);
        AddGlowClippedSky(cx, cy, haloRadius, rCol * 0.42f, gCol * 0.42f, bCol * 0.42f);
    }

    private int GetOrnamentRgb(Ornament orn, int frame, int mode)
    {
        if (mode == 0)
        {
            int idx = Math.Abs((orn.Y / 2) - (orn.X / 4) + (frame / 3)) % BulbPalette.Length;
            return BulbPalette[idx];
        }
        if (mode == 1)
        {
            int phase = (frame + orn.Phase) % 18;
            if (phase < 3) return PackRgb(255, 255, 240);
            return BulbPalette[orn.Index % BulbPalette.Length];
        }
        if (mode == 2)
        {
            int slot = ((orn.Y / 3) + (frame / 4)) % 3;
            if (slot == 0) return PackRgb(255, 218, 78);
            if (slot == 1) return PackRgb(245, 58, 88);
            return PackRgb(255, 246, 222);
        }

        float t = 0.5f + 0.5f * (float)Math.Sin(orn.Y * 0.14f + orn.X * 0.08f - frame * 0.24f);
        if (t < 0.33f) return LerpRgbInt(PackRgb(72, 238, 255), PackRgb(92, 250, 178), t * 3.0f);
        if (t < 0.66f) return LerpRgbInt(PackRgb(92, 250, 178), PackRgb(208, 128, 255), (t - 0.33f) * 3.0f);
        return LerpRgbInt(PackRgb(208, 128, 255), PackRgb(255, 108, 188), (t - 0.66f) * 3.0f);
    }

    private static int ToneMapAces(float r, float g, float b, int x, int y)
    {
        float dither = (Bayer2x2[(x & 1) | ((y & 1) << 1)] - 0.375f) / 255.0f;
        float mr = Clamp01(AcesCurve(r * 1.16f) + dither);
        float mg = Clamp01(AcesCurve(g * 1.16f) + dither);
        float mb = Clamp01(AcesCurve(b * 1.16f) + dither);
        return PackRgb((int)(mr * 255.0f), (int)(mg * 255.0f), (int)(mb * 255.0f));
    }

    private static float AcesCurve(float v)
    {
        if (v <= 0.0f) return 0.0f;
        float num = v * (2.51f * v + 0.03f);
        float den = v * (2.43f * v + 0.59f) + 0.14f;
        float res = num / den;
        if (res < 0.0f) return 0.0f;
        if (res > 1.0f) return 1.0f;
        return res;
    }

    private static int LerpRgbInt(int c1, int c2, float t)
    {
        t = Clamp01(t);
        int r1 = (c1 >> 16) & 255;
        int g1 = (c1 >> 8) & 255;
        int b1 = c1 & 255;
        int r2 = (c2 >> 16) & 255;
        int g2 = (c2 >> 8) & 255;
        int b2 = c2 & 255;
        return PackRgb(
            (int)(r1 + (r2 - r1) * t),
            (int)(g1 + (g2 - g1) * t),
            (int)(b1 + (b2 - b1) * t));
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Clamp01((x - e0) / (e1 - e0));
        return t * t * (3.0f - 2.0f * t);
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * Clamp01(t);
    }

    private static float Clamp01(float v)
    {
        if (v < 0.0f) return 0.0f;
        if (v > 1.0f) return 1.0f;
        return v;
    }

    private static float ClampFloat(float v, float min, float max)
    {
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }

    private static int ClampInt(int v, int min, int max)
    {
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }

    private static int PackRgb(int r, int g, int b)
    {
        return (ClampInt(r, 0, 255) << 16) | (ClampInt(g, 0, 255) << 8) | ClampInt(b, 0, 255);
    }

    private static string SanitizeName(string petName)
    {
        if (string.IsNullOrEmpty(petName)) return "LabPet";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < petName.Length; i++)
        {
            char ch = petName[i];
            if (!char.IsControl(ch)) sb.Append(ch);
        }
        string s = sb.ToString().Trim();
        if (s.Length == 0) return "LabPet";
        if (s.Length > 32) s = s.Substring(0, 32).TrimEnd();
        return s;
    }

    private static bool IsWideChar(char ch)
    {
        int code = (int)ch;
        return (code >= 0x1100 && code <= 0x11FF)
            || (code >= 0x2E80 && code <= 0x9FFF)
            || (code >= 0xAC00 && code <= 0xD7AF)
            || (code >= 0xF900 && code <= 0xFAFF)
            || (code >= 0xFF01 && code <= 0xFF60)
            || (code >= 0xFFE0 && code <= 0xFFE6);
    }

    private static int GetDisplayWidth(string text)
    {
        int w = 0;
        for (int i = 0; i < text.Length; i++) w += IsWideChar(text[i]) ? 2 : 1;
        return w;
    }
}
'@

if (-not ('LabPetChristmasEngine' -as [type])) {
    Add-Type -TypeDefinition $engineSource -ErrorAction Stop
}

$vtEnabled = $false
if (-not [string]::IsNullOrEmpty($env:WT_SESSION) -or $PSVersionTable.PSVersion.Major -ge 7) {
    $vtEnabled = $true
}
else {
    $vtEnabled = [LabPetConsoleNative]::TryEnableVirtualTerminal()
}

[LabPetConsoleNative]::TryDisableQuickEditMode()
if ([string]::IsNullOrEmpty($env:WT_SESSION)) {
    [LabPetConsoleNative]::TryConfigureHighDensityWindow(164, 48, 11)
}

$engine = New-Object LabPetChristmasEngine
$engine.RunLoop($PetName, $vtEnabled)
"""";
    }

    /// <summary>
    /// %AppData%\LabPet\scripts\christmas_tree.ps1 파일이 없으면 생성하고, 최신 내용으로 원자적으로 기록한다.
    /// Windows PowerShell 5.1이 UTF-8 한글 문자를 오독하지 않도록 UTF-8 BOM을 포함한다.
    /// </summary>
    public string EnsureScriptFile()
    {
        var targetPath = ScriptFilePath;
        var directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);

        var content = BuildScriptContent();
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        var expectedBytes = GetBytesWithBom(encoding, content);
        if (File.Exists(targetPath))
        {
            var existingBytes = File.ReadAllBytes(targetPath);
            if (existingBytes.AsSpan().SequenceEqual(expectedBytes))
            {
                return targetPath;
            }
        }

        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(expectedBytes, 0, expectedBytes.Length);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        return targetPath;
    }

    private static byte[] GetBytesWithBom(Encoding encoding, string content)
    {
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(content);
        var result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }

    /// <summary>
    /// 소형 고정폭 글꼴 축소가 가능한 Windows 클래식 콘솔 호스트(conhost.exe) 경로를 찾는다. 없으면 null을 반환한다.
    /// </summary>
    public static string? ResolveConsoleHostPath()
    {
        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(systemDir))
        {
            var conhostPath = Path.Combine(systemDir, "conhost.exe");
            if (File.Exists(conhostPath))
            {
                return conhostPath;
            }
        }

        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(winDir))
        {
            var fallbackPath = Path.Combine(winDir, "System32", "conhost.exe");
            if (File.Exists(fallbackPath))
            {
                return fallbackPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Windows CommandLineToArgvW 규칙에 맞게 단일 명령줄 인자를 따옴표로 안전하게 이스케이프한다.
    /// </summary>
    internal static string QuoteCommandLineArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var sb = new StringBuilder(value.Length + 4);
        sb.Append('"');
        var backslashes = 0;

        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }

            if (backslashes > 0)
            {
                sb.Append('\\', backslashes);
                backslashes = 0;
            }

            sb.Append(ch);
        }

        if (backslashes > 0)
        {
            sb.Append('\\', backslashes * 2);
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// 크리스마스 트리 애니메이션 창 실행용 ProcessStartInfo를 생성한다.
    /// WinExe GUI 프로세스에서도 독립 콘솔 창(conhost.exe)이 정상 생성되도록 UseShellExecute=true를 사용한다.
    /// </summary>
    public static ProcessStartInfo BuildLaunchStartInfo(
        string scriptPath,
        string? petName,
        string? consoleHostPath)
    {
        var safeName = SanitizePetName(petName);
        var scriptArgs = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File {QuoteCommandLineArgument(scriptPath)} -PetName {QuoteCommandLineArgument(safeName)}";

        if (!string.IsNullOrWhiteSpace(consoleHostPath))
        {
            return new ProcessStartInfo
            {
                FileName = consoleHostPath,
                Arguments = $"powershell.exe {scriptArgs}",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
        }

        return new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = scriptArgs,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
    }

    /// <summary>
    /// 외부 PowerShell이나 .ps1 스크립트 없이 LabPet.Client.exe 내장 C# 엔진(--christmas-tree)을 실행하는 ProcessStartInfo를 생성한다.
    /// conhost.exe 또는 실행 파일을 직접 시작하고 ArgumentList로 인자를 전달하여 셸 메타문자 해석을 방지한다.
    /// </summary>
    public static ProcessStartInfo BuildNativeLaunchStartInfo(
        string clientExePath,
        string? petName,
        string? consoleHostPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientExePath);
        var useConsoleHost = !string.IsNullOrWhiteSpace(consoleHostPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = useConsoleHost ? consoleHostPath! : clientExePath,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };
        if (useConsoleHost)
        {
            startInfo.ArgumentList.Add(clientExePath);
        }
        startInfo.ArgumentList.Add(NativeCliFlag);
        startInfo.ArgumentList.Add(SanitizePetName(petName));
        return startInfo;
    }

    /// <summary>
    /// 현재 프로세스의 LabPet.Client.exe 실행 파일 경로를 확인한다. (dotnet test 호스트 등에서는 null 반환)
    /// </summary>
    public static string? ResolveCurrentClientExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && File.Exists(processPath)
            && string.Equals(Path.GetFileName(processPath), "LabPet.Client.exe", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, "LabPet.Client.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// LabPet.Client.exe --christmas-tree [PetName] 진입점에서 호출되어 PowerShell 없이 내장 C# 크리스마스 트리 엔진을 즉시 실행한다.
    /// </summary>
    public static int RunNativeConsoleMode(IReadOnlyList<string> args)
    {
        try
        {
            using var instance = new ChristmasTreeInstance();
            if (!instance.IsOwner)
            {
                if (instance.TryActivateExisting())
                {
                    ChristmasTreeStartup.SignalReady();
                    return 0;
                }
                ChristmasTreeStartup.SignalFailure();
                return 1;
            }
            var result = RunNativeConsoleModeCore(args, instance);
            if (result != 0) ChristmasTreeStartup.SignalFailure();
            return result;
        }
        catch (Exception ex)
        {
            try { ChristmasTreeStartup.SignalFailure(); } catch { }
            try { Console.Error.WriteLine("크리스마스트리 초기화 실패: " + ex.Message); } catch { }
            return 1;
        }
    }

    private static int RunNativeConsoleModeCore(IReadOnlyList<string> args, ChristmasTreeInstance instance)
    {
        string? rawName = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], NativeCliFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    rawName = args[i + 1];
                }
                break;
            }
        }

        var safeName = SanitizePetName(rawName);
        if (!LabPetConsoleNative.EnsureConsoleAttachedOrAllocated())
        {
            return 1;
        }

        using var consoleState = new ChristmasConsoleState();
        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.OutputEncoding = utf8NoBom;
            Console.InputEncoding = utf8NoBom;
        }
        catch
        {
        }

        try
        {
            Console.Title = "LabPet - Merry Christmas!";
        }
        catch
        {
        }

        var vtEnabled = LabPetConsoleNative.TryEnableVirtualTerminal();
        LabPetConsoleNative.TryDisableQuickEditMode();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")))
        {
            LabPetConsoleNative.TryConfigureHighDensityWindow(
                DefaultWindowColumns,
                DefaultWindowRows,
                DefaultFontPixelHeight);
        }

        try
        {
            if (vtEnabled)
            {
                // 1열 ASCII 고정폭 셀 기본 비율(가로/세로 ≈ 0.5)을 폴백으로 사용하고, 호스트 측정값이 있으면 우선 적용한다.
                var cellAspect = consoleState.TryGetGlyphAspect(out var measuredAspect)
                    ? measuredAspect : 0.5f;
                new ChristmasTreeConsoleRunner().Run(safeName, cellAspect,
                    initialized: () =>
                    {
                        instance.MarkReady();
                        ChristmasTreeStartup.SignalReady();
                    });
            }
            else
            {
                // ANSI 미지원 환경에서는 기존 비 ANSI 출력 경로를 보존하고 시작 완료를 알린다.
                Console.WriteLine("ANSI 색상을 지원하지 않아 호환 모드로 실행합니다.");
                instance.MarkReady();
                ChristmasTreeStartup.SignalReady();
                var engine = new LabPetChristmasEngine();
                engine.RunLoop(safeName, vtEnabled: false);
            }
            return 0;
        }
        catch (Exception ex)
        {
            // 크리스마스트리 전용 자식 프로세스의 실패를 종료 코드로 전달한다.
            try { Console.Error.WriteLine("크리스마스트리 실행 실패: " + ex.Message); } catch { }
            return 1;
        }
    }

    /// <summary>
    /// 초기 고밀도 픽셀(164x48 셀, 11px 고정폭 폰트) 창 모드에서 크리스마스 트리를 실행한다.
    /// 1순위: conhost.exe + LabPet.Client.exe --christmas-tree (외부 PowerShell/ExecutionPolicy/백신 차단 무관)
    /// 2순위: LabPet.Client.exe --christmas-tree 자체 AllocConsole()
    /// 3순위: powershell.exe 스크립트 폴백
    /// </summary>
    public (bool Success, string Message) Launch(string? petName)
    {
        try
        {
            // 사용자 요청을 받은 본체에서 직접 활성화한다. 자식의 전경 전환 권한에 의존하지 않는다.
            // 트리가 없으면 이 확인용 잠금을 해제한 뒤 자식을 시작한다.
            using var existing = new ChristmasTreeInstance();
            if (!existing.IsOwner)
            {
                return existing.TryActivateExisting()
                    ? (true, "기존 크리스마스트리 창을 활성화했어요!")
                    : (false, "크리스마스트리가 이미 실행 중이지만 창 활성화를 확인하지 못했습니다. 새 창은 만들지 않았습니다.");
            }
        }
        catch
        {
            return (false, "크리스마스트리 실행 상태를 확인하지 못했습니다. 중복 실행을 방지하기 위해 새 창은 만들지 않았습니다.");
        }
        const string successMessage = "크리스마스 트리를 띄웠어요! (Space: 조명 | F: 폭죽 | S: 눈 | Q: 종료)";
        var conhostPath = ResolveConsoleHostPath();
        var clientExePath = ResolveCurrentClientExecutablePath();

        if (!string.IsNullOrWhiteSpace(clientExePath))
        {
            if (!string.IsNullOrWhiteSpace(conhostPath))
            {
                try
                {
                    var nativeConhostInfo = BuildNativeLaunchStartInfo(clientExePath, petName, conhostPath);
                    var conhostResult = ChristmasTreeStartup.StartAndWait(nativeConhostInfo);
                    if (conhostResult.Success || !conhostResult.CanFallback)
                    {
                        return (conhostResult.Success, conhostResult.Message);
                    }
                }
                catch
                {
                    // conhost 래퍼 실패 시 직접 실행으로 폴백
                }
            }

            try
            {
                var nativeDirectInfo = BuildNativeLaunchStartInfo(clientExePath, petName, consoleHostPath: null);
                var directResult = ChristmasTreeStartup.StartAndWait(nativeDirectInfo);
                if (directResult.Success || !directResult.CanFallback)
                {
                    return (directResult.Success, directResult.Message);
                }
            }
            catch
            {
                // 네이티브 실행 실패 시 스크립트 폴백으로 진행
            }
        }

        string scriptPath;
        try
        {
            scriptPath = EnsureScriptFile();
        }
        catch
        {
            return (false, "크리스마스 트리 스크립트를 준비하지 못했습니다.");
        }

        if (!string.IsNullOrWhiteSpace(conhostPath))
        {
            try
            {
                var conhostInfo = BuildLaunchStartInfo(scriptPath, petName, conhostPath);
                using var proc = Process.Start(conhostInfo);
                if (proc is not null && !proc.WaitForExit(150))
                {
                    return (true, successMessage);
                }
            }
            catch
            {
                // conhost.exe 실행 실패 시 powershell.exe 폴백
            }
        }

        try
        {
            var psInfo = BuildLaunchStartInfo(scriptPath, petName, consoleHostPath: null);
            using var proc = Process.Start(psInfo);
            return proc is not null && !proc.WaitForExit(150)
                ? (true, successMessage)
                : (false, "크리스마스 트리 창을 실행하지 못했습니다.");
        }
        catch
        {
            return (false, "크리스마스 트리 창을 실행하지 못했습니다.");
        }
    }
}
