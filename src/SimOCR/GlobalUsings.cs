// 本项目同时使用 WPF 与 WinForms（托盘），隐式 using 会互相冲突，
// 因此在 Directory.Build.props 里关闭 ImplicitUsings，改由这里精确控制全局 using。
// WPF / WinForms 的类型一律在各文件里显式 using。

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
