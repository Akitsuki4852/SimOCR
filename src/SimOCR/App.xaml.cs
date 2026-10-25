using System.Windows;

namespace SimOCR;

/// <summary>
/// 应用入口。SimOCR 是托盘常驻型工具：没有主窗口，ShutdownMode = OnExplicitShutdown。
/// 组合根（服务装配）在 <see cref="AppComposition"/> 中完成，见后续提交。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppComposition.Current.Start(this, e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppComposition.Current.Shutdown();
        base.OnExit(e);
    }
}
