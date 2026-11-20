using LabPet.Client.Audio;
using LabPet.Client.Rendering;
using LabPet.Client.Solo;
using LabPet.Shared.Models;
using Xunit;

namespace LabPet.Tests;

public sealed class AudioAndSoloTests
{
    [Fact]
    public void AudioSpectrumProcessor_SeparatesBassAndTrebleBands()
    {
        var processor = new AudioSpectrumProcessor();
        const int sampleRate = 48000;
        var bassBlock = GenerateSineWave(100.0, sampleRate, AudioSpectrumProcessor.FftLength, amplitude: 0.6f);
        var (bass1, mid1, treble1, _) = processor.AnalyzeBlock(bassBlock, sampleRate, 0.0);

        Assert.True(bass1 > mid1 && bass1 > treble1, $"Expected bass dominant, got B={bass1}, M={mid1}, T={treble1}");

        processor.Reset();
        var trebleBlock = GenerateSineWave(5000.0, sampleRate, AudioSpectrumProcessor.FftLength, amplitude: 0.6f);
        var (bass2, _, treble2, _) = processor.AnalyzeBlock(trebleBlock, sampleRate, 0.0);

        Assert.True(treble2 > bass2, $"Expected treble dominant, got B={bass2}, T={treble2}");
    }

    [Fact]
    public void AudioSpectrumProcessor_DetectsAdaptiveBeatOnSuddenBassSpike()
    {
        var processor = new AudioSpectrumProcessor();
        const int sampleRate = 48000;
        var quietBlock = GenerateSineWave(90.0, sampleRate, AudioSpectrumProcessor.FftLength, amplitude: 0.03f);
        var punchBlock = GenerateSineWave(90.0, sampleRate, AudioSpectrumProcessor.FftLength, amplitude: 0.85f);

        // 이동 평균 기준선 12프레임 축적
        for (var i = 0; i < 12; i++)
        {
            processor.AnalyzeBlock(quietBlock, sampleRate, i * 0.022);
        }

        var (bass, _, _, isBeat) = processor.AnalyzeBlock(punchBlock, sampleRate, 0.50);
        Assert.True(bass > 0.2f);
        Assert.True(isBeat);
    }

    [Fact]
    public void SoloProgressionService_GrantsRewardsAndHandlesShopPurchase()
    {
        var svc = new SoloProgressionService();
        var profile = new PetProfile();

        // 키보드 80타 -> 2버킷 (+10 XP, +4 코인)
        svc.RecordKeystrokes(profile, 80);
        Assert.Equal(10, profile.SoloProgress.Xp);
        Assert.Equal(4, profile.SoloProgress.Coins);

        // 뽀모도로 1회 완료 -> +100 XP (레벨업 Lv.1->Lv.2, 잔여 10 XP), +50 코인 (총 54 코인)
        var (leveledUp, newLevel) = svc.RecordPomodoroCompleted(profile);
        Assert.True(leveledUp);
        Assert.Equal(2, newLevel);
        Assert.Equal(10, profile.SoloProgress.Xp);
        Assert.Equal(54, profile.SoloProgress.Coins);

        // 코인 차감 및 잔액 부족 차단 + 게임 정산 가산 검증
        var (failSpend, _) = svc.TrySpendCoins(profile, 60);
        Assert.False(failSpend);
        Assert.Equal(54, profile.SoloProgress.Coins);

        var (okSpend, _) = svc.TrySpendCoins(profile, 30);
        Assert.True(okSpend);
        Assert.Equal(24, profile.SoloProgress.Coins);

        svc.AddCoins(profile, 60);
        Assert.Equal(84, profile.SoloProgress.Coins);
    }

    private static float[] GenerateSineWave(double frequencyHz, int sampleRate, int length, float amplitude)
    {
        var buffer = new float[length];
        for (var i = 0; i < length; i++)
        {
            buffer[i] = amplitude * MathF.Sin((float)(2.0 * Math.PI * frequencyHz * i / sampleRate));
        }

        return buffer;
    }
}
