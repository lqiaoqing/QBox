# 前台生命周期探针

Windows 上从仓库根运行：

```powershell
dotnet run --project tools/QBox.DesktopProbe -c Release -- work/desktop-lifecycle/state
dotnet run --project tools/QBox.DesktopProbe -c Release -- --resources-only
```

使用独立状态、屏幕外 STA 窗口和占位图标，实际调用 `MainWindow` 的查询、目录刷新、隐藏/显示及图标队列逻辑。验证内容不变时保留结果行，隐藏输入/刷新不启动查询，唤出使用最新覆盖层，旧图标任务可清空且队列继续可用。报告写入状态目录上一级的 `report.json`。

工作台用例构造 235 条命中，验证展开、续填和切页后仍选中同一目标，切回快捷框后恢复 40 条上限。同目录下还生成快捷搜索、工作台搜索、剪贴板预览和设置的 WPF 渲染图，供检查布局。

探针不创建正式 `App`，不进入其 Startup、热键、托盘、后台工作进程或默认用户状态入口。会只读枚举系统应用并调用 Shell 取测试图标。它不是屏幕渲染、物理输入法/热键、真实卷更新或端到端唤起延迟验收；计时只描述该探针的窗口与结果回填。

测试通过反射驱动内部状态，窗口字段/方法改名时需要同步探针。自动检查中的 `LatestQueryGate`、目录等价和实时视图检查仍独立执行，不由此探针替代。

`--resources-only` 单独验证真实 App.xaml 的资源和程序集名解析，随后立即退出，不进入 Dispatcher/Startup 或正式状态入口。
