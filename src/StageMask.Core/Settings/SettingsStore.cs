using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StageMask.Core.Settings;

/// <summary>
/// 設定をJSONファイルに保存・読み込みする。
/// NGワードは平文で書かず、<see cref="ISecretProtector"/> で暗号化して保存する。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ISecretProtector? _protector;

    /// <summary>暗号化されたNGワードを復号できなかった(別ユーザー・別PC・改ざんなど)。</summary>
    private bool _unprotectFailed;

    public string FilePath { get; }

    /// <summary>読み込み・保存時の注意事項(なければ null)。</summary>
    public string? Warning { get; private set; }

    public SettingsStore(string filePath, ISecretProtector? protector = null)
    {
        FilePath = filePath;
        _protector = protector;
    }

    /// <summary>既定の保存先: %APPDATA%\StageMask\settings.json</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StageMask", "settings.json");

    /// <summary>読み込む。ファイルがない・壊れている場合は既定値を返す(壊れたファイルは .bak に退避)。</summary>
    public AppSettings Load()
    {
        AppSettings settings;
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            settings = Deserialize(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch { /* 退避に失敗しても続行 */ }
            Warning = "設定ファイルが壊れていたため、既定の設定で起動しました(元のファイルは settings.json.bak)";
            return new AppSettings();
        }

        UnprotectNgWords(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        ProtectNgWords(settings);
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, Serialize(settings));
        File.Move(tmp, FilePath, overwrite: true);
    }

    private void UnprotectNgWords(AppSettings settings)
    {
        var d = settings.Detection;
        d.NgWords = new();
        if (string.IsNullOrEmpty(d.NgWordsProtected)) return;

        if (_protector is null)
        {
            _unprotectFailed = true;
            Warning = "NGワードを復号する手段がないため、NGワードなしで起動しました";
            return;
        }
        try
        {
            d.NgWords = SplitLines(_protector.Unprotect(d.NgWordsProtected));
        }
        catch (Exception)
        {
            // 別のユーザー・別のPCで暗号化されたデータなど。暗号文は保存時に残す(上書きで失わない)
            _unprotectFailed = true;
            Warning = "保存されたNGワードを復号できませんでした(別のユーザー・PCで作成された可能性があります)。NGワードを登録し直してください";
        }
    }

    private void ProtectNgWords(AppSettings settings)
    {
        var d = settings.Detection;
        var words = d.NgWords.Select(w => w.Trim()).Where(w => w.Length > 0).ToList();

        if (words.Count == 0)
        {
            // 復号に失敗したまま空で保存すると、元のNGワードが失われるので暗号文を残す
            if (!_unprotectFailed) d.NgWordsProtected = null;
            return;
        }

        if (_protector is null)
        {
            // 暗号化できない環境では、NGワードを平文で保存しない
            d.NgWordsProtected = null;
            Warning = "NGワードを暗号化できないため、NGワードは保存していません(このセッション中のみ有効)";
            return;
        }

        d.NgWordsProtected = _protector.Protect(string.Join("\n", words));
        _unprotectFailed = false;
    }

    private static System.Collections.Generic.List<string> SplitLines(string text)
        => text.Split('\n').Select(s => s.Trim('\r', ' ', '\t')).Where(s => s.Length > 0).ToList();

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, JsonOptions);

    public static AppSettings? Deserialize(string json)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        if (s is null) return null;
        // null になりうるコレクション・子オブジェクトを補完
        s.ManualMasks ??= new();
        s.WindowRules ??= new();
        s.Detection ??= new();
        s.Detection.NgWords ??= new();
        s.Appearance ??= new();
        s.Hotkeys ??= new();
        s.Diagnostics ??= new();
        foreach (var m in s.ManualMasks) m.Content ??= new();
        foreach (var r in s.WindowRules) r.Content ??= new();
        s.Appearance.LabelImagePath ??= "";

        // v0.5.3 で保存された「大部分とみなす割合」の既定値 25% は、タッチパッドのスクロールを
        // 検知できなかったため、v0.5.4 で 10% に下げた。既定値のまま保存されていた場合だけ置き換える
        if (s.Version < 2)
        {
            if (s.Detection.LargeChangePercent == 25) s.Detection.LargeChangePercent = 10;
            s.Version = 2;
        }
        return s;
    }
}
