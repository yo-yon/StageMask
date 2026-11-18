namespace StageMask.Core.Settings;

/// <summary>
/// 設定ファイルに保存する機密情報(NGワード)を暗号化・復号する。
/// Windows では DPAPI(ログオン中のユーザーだけが復号できる)を使う実装を渡す。
/// </summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    /// <summary>復号する。改ざん・別ユーザーのデータなどで復号できない場合は例外を投げる。</summary>
    string Unprotect(string protectedText);
}
