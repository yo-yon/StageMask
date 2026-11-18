using System;
using System.IO;
using System.Linq;
using System.Text;
using StageMask.Core.Detection;
using StageMask.Core.Imaging;
using StageMask.Core.Settings;
using Xunit;

namespace StageMask.Core.Tests;

public class SecurityTests
{
    /// <summary>テスト用の暗号化(Base64 + 目印)。本番は DPAPI を使う。</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plainText) => "fake:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));
        public string Unprotect(string protectedText)
        {
            if (!protectedText.StartsWith("fake:")) throw new InvalidDataException("not protected by this protector");
            return Encoding.UTF8.GetString(Convert.FromBase64String(protectedText.Substring(5)));
        }
    }

    private static string TempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stagemask-sec-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    // ---------- NGワードの暗号化保存 ----------
    [Fact]
    public void NgWords_AreSavedEncrypted_AndLoadedBack()
    {
        var path = TempFile();
        var s = new AppSettings();
        s.Detection.NgWords.AddRange(new[] { "朝日商事", "山田太郎" });
        new SettingsStore(path, new FakeProtector()).Save(s);

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("朝日商事", raw);
        Assert.DoesNotContain("山田太郎", raw);
        Assert.Contains("NgWordsProtected", raw);

        var back = new SettingsStore(path, new FakeProtector()).Load();
        Assert.Equal("朝日商事|山田太郎", string.Join("|", back.Detection.NgWords));
    }

    [Fact]
    public void NgWords_WithoutProtector_AreNotWrittenToDisk()
    {
        var path = TempFile();
        var s = new AppSettings();
        s.Detection.NgWords.Add("朝日商事");
        var store = new SettingsStore(path, protector: null);
        store.Save(s);

        Assert.DoesNotContain("朝日商事", File.ReadAllText(path));
        Assert.Null(s.Detection.NgWordsProtected);
        Assert.NotNull(store.Warning);
    }

    [Fact]
    public void NgWords_UndecryptableBlob_IsKeptOnSave()
    {
        // 別ユーザーのデータなどで復号できない場合、空のまま保存して暗号文を消さない
        var path = TempFile();
        File.WriteAllText(path, "{ \"Detection\": { \"NgWordsProtected\": \"other-user-blob\" } }");
        var store = new SettingsStore(path, new FakeProtector());
        var s = store.Load();
        Assert.Empty(s.Detection.NgWords);
        Assert.NotNull(store.Warning);

        store.Save(s);
        Assert.Contains("other-user-blob", File.ReadAllText(path));

        // 新しく登録し直したら上書きされる
        s.Detection.NgWords.Add("新しい語");
        store.Save(s);
        Assert.DoesNotContain("other-user-blob", File.ReadAllText(path));
    }

    [Fact]
    public void NgWords_ClearedByUser_RemovesBlob()
    {
        var path = TempFile();
        var s = new AppSettings();
        s.Detection.NgWords.Add("朝日商事");
        var store = new SettingsStore(path, new FakeProtector());
        store.Save(s);
        s.Detection.NgWords.Clear();
        store.Save(s);
        Assert.Null(store.Load().Detection.NgWordsProtected);
    }

    // ---------- フェイルクローズ ----------
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(3);

    [Fact]
    public void Scan_NeverScanned_IsNotSafe()
    {
        Assert.False(new ScanStatusTracker().IsSafe(1, T0, MaxAge));
    }

    [Fact]
    public void Scan_RecentSuccess_IsSafe()
    {
        var t = new ScanStatusTracker();
        t.RecordSuccess(1, T0);
        Assert.True(t.IsSafe(1, T0.AddSeconds(2), MaxAge));
    }

    [Fact]
    public void Scan_StaleSuccess_IsNotSafe()
    {
        var t = new ScanStatusTracker();
        t.RecordSuccess(1, T0);
        Assert.False(t.IsSafe(1, T0.AddSeconds(4), MaxAge));
    }

    [Fact]
    public void Scan_FailureAfterSuccess_IsNotSafe_UntilNextSuccess()
    {
        var t = new ScanStatusTracker();
        t.RecordSuccess(1, T0);
        t.RecordFailure(1, T0.AddSeconds(1), "capture failed");
        Assert.False(t.IsSafe(1, T0.AddSeconds(1), MaxAge));
        Assert.Equal("capture failed", t.GetFailureReason(1));

        t.RecordSuccess(1, T0.AddSeconds(2));
        Assert.True(t.IsSafe(1, T0.AddSeconds(2), MaxAge));
        Assert.Null(t.GetFailureReason(1));
    }

    [Fact]
    public void Scan_RetainOnly_ForgetsOtherWindows()
    {
        var t = new ScanStatusTracker();
        t.RecordSuccess(1, T0);
        t.RecordSuccess(2, T0);
        t.RetainOnly(new long[] { 2 });
        Assert.False(t.IsSafe(1, T0, MaxAge));
        Assert.True(t.IsSafe(2, T0, MaxAge));
    }

    // ---------- 真っ黒な画像の判定 ----------
    private static byte[] Solid(int w, int h, byte v)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
        return px;
    }

    [Fact]
    public void BlackFrame_IsDetected()
    {
        Assert.True(FrameHeuristics.IsLikelyBlackFrame(Solid(200, 100, 0), 200, 100, 800));
    }

    [Fact]
    public void WhiteOrDarkThemeFrame_IsNotBlack()
    {
        Assert.False(FrameHeuristics.IsLikelyBlackFrame(Solid(200, 100, 255), 200, 100, 800));

        // ダークテーマ(背景 #1E1E1E)に文字がある画面
        var px = Solid(200, 100, 0x1E);
        for (int x = 0; x < 200 * 4; x += 4) { px[50 * 800 + x] = px[50 * 800 + x + 1] = px[50 * 800 + x + 2] = 230; }
        Assert.False(FrameHeuristics.IsLikelyBlackFrame(px, 200, 100, 800));
    }

    [Fact]
    public void InvalidFrame_IsTreatedAsBlack()
    {
        Assert.True(FrameHeuristics.IsLikelyBlackFrame(new byte[10], 200, 100, 800));
    }
}
