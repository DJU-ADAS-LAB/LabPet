using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using LabPet.Client.Rendering.Terminal3D;

namespace LabPet.Client.SystemIntegration;

/// <summary>3D ASCII 장면·비차단 키보드 입력·단일 버퍼 HUD 출력을 묶는 네이티브 실행 루프.</summary>
internal sealed class ChristmasTreeConsoleRunner
{
    private const string KeyGuideText =
        "Space 회전 | ←→ 속도 | ↑↓ 확대 | S 눈 | L 조명 | F 불꽃 | R 재시작 | F11 최대화 | Q/ESC 종료";

    private readonly ChristmasTreeScene _scene = new();
    private readonly ChristmasParticleField _particles = new();
    private readonly AsciiDepthSurface _surface = new();
    private readonly AsciiAnsiFrameWriter _writer = new();

    public void Run(string petName, float cellAspect, Action? initialized = null)
    {
        var projector = new TerminalPointProjector(_surface);
        var clock = Stopwatch.StartNew();
        var previous = clock.Elapsed.TotalSeconds;
        var width = -1;
        var height = -1;
        var exit = false;
        var ready = false;
        var nextFirework = 7.5;
        var detailStride = 1;
        var smoothedRenderSeconds = 0.012;

        var safeName = TerminalChristmasTreeService.SanitizePetName(petName);
        var bannerUnit = "  *  메리 크리스마스 · " + safeName + "님, 따뜻한 연말 보내세요!  ";
        var bannerDoubled = bannerUnit + bannerUnit;
        Span<char> elapsedBuffer = stackalloc char[24];

        var cursorVisible = true;
        try { cursorVisible = Console.CursorVisible; } catch { }

        try
        {
            Console.Write("\u001b[?1049h\u001b[?25l\u001b[0m\u001b[48;2;0;0;0m\u001b[2J");
            Console.Out.Flush();

            while (!exit)
            {
                var frameStart = clock.Elapsed.TotalSeconds;
                var delta = Math.Clamp(frameStart - previous, 0, 0.25);
                previous = frameStart;

                // 입력 폭주가 렌더링을 지연시키지 않도록 프레임당 최대 32건만 비차단 소비
                for (var i = 0; i < 32 && Console.KeyAvailable; i++)
                {
                    switch (Console.ReadKey(true).Key)
                    {
                        case ConsoleKey.Q:
                        case ConsoleKey.Escape: exit = true; break;
                        case ConsoleKey.Spacebar: _scene.ToggleRotation(); break;
                        case ConsoleKey.LeftArrow: _scene.AdjustRotation(-0.12f); break;
                        case ConsoleKey.RightArrow: _scene.AdjustRotation(0.12f); break;
                        case ConsoleKey.UpArrow: _scene.AdjustZoom(0.12f); break;
                        case ConsoleKey.DownArrow: _scene.AdjustZoom(-0.12f); break;
                        case ConsoleKey.S: _particles.CycleSnow(); break;
                        case ConsoleKey.L: _scene.CycleLighting(); break;
                        case ConsoleKey.F: _particles.TriggerFirework(_scene.ElapsedSeconds); break;
                        case ConsoleKey.R:
                            _scene.RestartIntroduction();
                            _particles.Reset();
                            nextFirework = 7.5;
                            break;
                        case ConsoleKey.F11: LabPetConsoleNative.TryToggleMaximizeConsoleWindow(); break;
                    }
                }
                if (exit) break;

                if (LabPetConsoleNative.IsConsoleMinimized())
                {
                    // 최소화 중에는 투영·렌더링·출력을 중단하고 복원 시 전체 화면을 다시 그린다.
                    _writer.Invalidate();
                    width = -1;
                    height = -1;
                    Thread.Sleep(100);
                    continue;
                }

                _scene.Update(delta);

                int currentWidth;
                int currentHeight;
                try
                {
                    currentWidth = Console.WindowWidth;
                    currentHeight = Console.WindowHeight;
                }
                catch
                {
                    Thread.Sleep(100);
                    continue;
                }

                if (currentWidth < 20 || currentHeight < 10)
                {
                    _writer.Invalidate();
                    width = -1;
                    height = -1;
                    Thread.Sleep(100);
                    continue;
                }

                var resized = width != currentWidth || height != currentHeight;
                if (resized)
                {
                    width = currentWidth;
                    height = currentHeight;
                    Console.Write("\u001b[0m\u001b[48;2;0;0;0m\u001b[2J");
                    _writer.Invalidate();
                }

                var sceneWidth = Math.Max(1, width - 1);
                var sceneHeight = Math.Max(1, height - 5);
                _surface.Resize(sceneWidth, sceneHeight);

                _scene.Render(_surface, projector, cellAspect, detailStride);
                if (_scene.ElapsedSeconds >= nextFirework)
                {
                    _particles.TriggerFirework(_scene.ElapsedSeconds);
                    nextFirework = _scene.ElapsedSeconds + 10.5;
                }
                _particles.Render(projector, _scene.ElapsedSeconds, _scene.Zoom, cellAspect);

                var left = Math.Max(0, (sceneWidth - _surface.Columns) / 2);
                _writer.BeginFrame();
                _writer.AppendSurfaceDiff(_surface, left, top: 1, forceFullRedraw: resized);

                // 우측 상단: 소수점 둘째 자리 경과 시간
                var elapsedLen = FormatElapsedSeconds(_scene.ElapsedSeconds, elapsedBuffer);
                _writer.AppendHudLine(0, width, elapsedBuffer[..elapsedLen], rgb: 0xFFE5A0, alignRight: true);

                // 좌측 하단: LabPet 브랜딩 및 활성 상태
                var rotationState = _scene.RotationPaused ? "일시정지" : "회전 중";
                _writer.AppendHudLine(
                    height - 4,
                    width,
                    $"LabPet · 3D ASCII 크리스마스  |  조명: {_scene.LightModeLabel} · 눈: {_particles.SnowLevelLabel} · {rotationState}",
                    rgb: 0x8CE3FF);

                // 좌측 하단: 실제 렌더링 요소 수 및 눈 입자 수
                _writer.AppendHudLine(
                    height - 3,
                    width,
                    $"렌더 셀 {_surface.OccupiedCells} · 표면 샘플 {_scene.SubmittedSamples} · 눈 {_particles.SnowCount}",
                    rgb: 0x9EB4CC);

                // 하단: 현재 펫 이름이 포함된 마퀴 배너 (레퍼런스 톤의 민트 에메랄드 강조)
                var shift = (int)((_scene.ElapsedSeconds * 6.0) % bannerUnit.Length);
                _writer.AppendHudLine(height - 2, width, bannerDoubled.AsSpan(shift, bannerUnit.Length), rgb: 0x6CF5A8);

                // 최하단: 간결한 키보드 조작 안내
                _writer.AppendHudLine(height - 1, width, KeyGuideText, rgb: 0xC6D6E8);

                _writer.FlushTo(Console.Out, _surface);

                if (!ready)
                {
                    initialized?.Invoke();
                    ready = true;
                }

                // 적응형 해상도(샘플 스트라이드) 및 30~60FPS 프레임 페이싱
                var renderSeconds = clock.Elapsed.TotalSeconds - frameStart;
                smoothedRenderSeconds = smoothedRenderSeconds * 0.8 + renderSeconds * 0.2;
                if (smoothedRenderSeconds > 0.024 && detailStride < 2) detailStride = 2;
                else if (smoothedRenderSeconds < 0.011 && detailStride > 1) detailStride = 1;

                var framePeriod = smoothedRenderSeconds < (1.0 / 75.0) ? (1.0 / 60.0) : (1.0 / 30.0);
                var remaining = framePeriod - (clock.Elapsed.TotalSeconds - frameStart);
                while (remaining > 0)
                {
                    if (Console.KeyAvailable) break;
                    Thread.Sleep((int)Math.Clamp(remaining * 1000.0, 1, 5));
                    remaining = framePeriod - (clock.Elapsed.TotalSeconds - frameStart);
                }
            }
        }
        finally
        {
            try
            {
                Console.Write("\u001b[0m\u001b[?25h\u001b[?1049l");
                Console.CursorVisible = cursorVisible;
                Console.Out.Flush();
            }
            catch
            {
            }
        }
    }

    internal static int FormatElapsedSeconds(double elapsedSeconds, Span<char> destination)
    {
        var clamped = double.IsFinite(elapsedSeconds) && elapsedSeconds >= 0 ? elapsedSeconds : 0;
        if (!clamped.TryFormat(destination, out var written, "F2", CultureInfo.InvariantCulture))
        {
            return 0;
        }
        if (written < destination.Length)
        {
            destination[written++] = 's';
        }
        return written;
    }
}
