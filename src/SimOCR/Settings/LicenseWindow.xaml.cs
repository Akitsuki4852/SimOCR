using System.Reflection;
using System.Windows;

namespace SimOCR.Settings;

/// <summary>
/// 只读的许可证查看窗：左边是第三方组件声明（NOTICE），右边是 Apache-2.0 全文（LICENSE）。
///
/// 文本来源是**编进程序集的两个嵌入资源**（见 SimOCR.csproj），不是磁盘上的文件 ——
/// 单文件 exe 旁边没有 LICENSE / NOTICE（publish.ps1 只保留那一个 exe），
/// 而便携 zip 与安装器目录里同时也有实体文件，两条分发形态都能满足
/// Apache-2.0 §4 对"随附许可证与归属声明"的要求。
/// </summary>
internal partial class LicenseWindow : Window
{
    private const string NoticeResource = "SimOCR.NOTICE.txt";
    private const string LicenseResource = "SimOCR.LICENSE.txt";

    internal LicenseWindow()
    {
        InitializeComponent();
        NoticeText.Text = ReadEmbedded(NoticeResource, NotFoundNotice);
        LicenseText.Text = ReadEmbedded(LicenseResource, NotFoundLicense);
    }

    /// <summary>读不到资源时的兜底文案：让人知道这是构建问题，而不是"本项目没有声明"。</summary>
    private const string NotFoundNotice =
        "（本构建未包含 NOTICE 资源）\n\n" +
        "第三方组件声明见项目主页的 NOTICE 文件：\n" +
        "https://github.com/Akitsuki4852/SimOCR/blob/master/NOTICE";

    private const string NotFoundLicense =
        "（本构建未包含 LICENSE 资源）\n\n" +
        "许可证全文见项目主页的 LICENSE 文件：\n" +
        "https://github.com/Akitsuki4852/SimOCR/blob/master/LICENSE";

    private static string ReadEmbedded(string resourceName, string fallback)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return fallback;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
