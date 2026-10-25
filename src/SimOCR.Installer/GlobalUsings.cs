// 安装器不涉及 WPF/WinForms（NativeAOT 不支持），隐式 using 统一在这里开。
// 与主工程保持同样的显式风格：ImplicitUsings=disable（见 Directory.Build.props）。

global using System;
global using System.Collections.Generic;
global using System.Globalization;
global using System.IO;
global using System.Linq;
global using System.Text;
global using System.Threading;
