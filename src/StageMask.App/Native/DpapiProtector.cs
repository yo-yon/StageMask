using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using StageMask.Core.Settings;

namespace StageMask.App.Native;

/// <summary>
/// Windows のデータ保護 API(DPAPI: CryptProtectData / CryptUnprotectData)で暗号化する。
/// 現在のユーザーの資格情報に結び付いた鍵を使うため、同じPCの同じユーザーでなければ復号できない。
/// </summary>
internal sealed class DpapiProtector : ISecretProtector
{
    private const string Prefix = "dpapi1:";
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    // 他アプリの DPAPI データと混ざらないようにするための追加エントロピー(秘密ではない)
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StageMask.NgWords.v1");

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public string Protect(string plainText)
    {
        var plain = Encoding.UTF8.GetBytes(plainText);
        try
        {
            return Prefix + Convert.ToBase64String(Transform(plain, protect: true));
        }
        finally
        {
            Array.Clear(plain); // 平文のバイト列を残さない
        }
    }

    public string Unprotect(string protectedText)
    {
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException("StageMask の暗号化形式ではありません");
        var plain = Transform(Convert.FromBase64String(protectedText.Substring(Prefix.Length)), protect: false);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = new DATA_BLOB();
        try
        {
            var inBlob = new DATA_BLOB { cbData = input.Length, pbData = inHandle.AddrOfPinnedObject() };
            var entBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = entHandle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref inBlob, "StageMask", ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero)
            {
                // 復号結果(平文)が入ったネイティブメモリを消去してから解放する
                if (!protect) Marshal.Copy(new byte[output.cbData], 0, output.pbData, output.cbData);
                LocalFree(output.pbData);
            }
            inHandle.Free();
            entHandle.Free();
        }
    }
}
