using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SCSKiller.App.Pages;

/// <summary>The user's own AES key for an encrypted Unreal game: checked against the game's files and stored on this PC only.</summary>
static class AesKeyDialog
{
    /// <summary>True once a key that opens the game's files is stored; the caller then re-reads the game.
    /// <paramref name="reason"/>: the Library's Why?, shown above the key.</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, string id, string title, string? reason = null)
    {
        var box = new TextBox { PlaceholderText = "0x 后跟 64 位十六进制数字", Header = "AES key" };
        var panel = new StackPanel { Spacing = 12, Children = { new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
            "SCSKiller 未能在游戏 exe 中找到密钥（受保护的 exe 会隐藏它）。如果你有密钥，请粘贴到此处：" +
            "它会与游戏文件核对，且只保存在本机。" }, box } };
        if (reason != null) panel.Children.Insert(0, new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = panel, CloseButtonText = "关闭", PrimaryButtonText = "解锁", DefaultButton = ContentDialogButton.Close,
        };
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return false;
        var key = box.Text.Trim();
        string? problem = null;
        bool ok;
        try { ok = await Task.Run(() => App.Core.SetEncryptionKey(id, key)); }   // opens the game's files
        catch (Exception ex) { (ok, problem) = (false, ex.Message); }
        if (!ok) await App.ShowAsync(new ContentDialog { XamlRoot = root, Title = "该密钥无效", Content = problem ?? "它打不开此游戏的文件。", CloseButtonText = "确定" });
        return ok;
    }
}
