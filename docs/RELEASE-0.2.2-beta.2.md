# CloseMeansExit 0.2.2-beta.2 · Beta 测试版

**目前仅实现了基本功能，真实应用兼容性和使用体验仍在完善中。欢迎试用并反馈问题。**

如果觉得好用，别忘了给项目点个 **Star ⭐**！

本次 Beta 修正了云端 Windows 测试夹具的启动和身份核验处理。基本功能沿用 0.2.2 基线，实际应用兼容性仍需逐项验证。

## 已实现的基本功能

- 发现窗口消失后仍留在后台的应用，显示程序路径、进程工作集和可操作提醒。
- 在提醒中加入黑名单或暂不处理；保存规则后，从下一次符合条件的关闭操作开始执行。
- 标准关闭与 Alt+F4 观察、按应用选择自绘关闭区域兼容、托盘暂停与退出。
- 按完整路径与进程身份核验执行实例，并保护其他可见窗口、最小化窗口与保存对话框。
- 对经过完整路径及父子关系核验的 Steam CEF 窗口，识别其对应的 Steam 主程序。

## 下载与使用

下载与你设备匹配的 Windows 便携 ZIP，完整解压后运行 `CloseMeansExit.exe`。自包含包无需另装 .NET。

首次发现只提醒，不会直接结束应用。加入黑名单或启用兼容后，请重新打开并关闭目标应用验证。切换版本前，先从托盘退出旧版。

自绘关闭兼容和子进程范围由使用者明确选择。强制结束可能中断下载、同步或游戏，并丢失未保存内容；还有其他窗口时仍会取消执行。

## 验证范围

0.2.2 基线已在 Windows x64 通过 207 条核心断言、原生窗口保护和专用夹具的提醒保存链路检查。链路测试的后续强关闭信号由程序注入，不能据此宣称真实应用的实体鼠标点叉或 Alt+F4 均已通过验收。公开 Beta 的构建结果以对应 GitHub Actions 记录为准。

Windows ARM64 产物仅表示完成对应构建，尚未通过 ARM64 实机验收。更多范围与限制见仓库 README、`docs/TESTING.md` 和 `docs/VALIDATION-0.2.2.md`。

## Feedback / English

This is a **Beta test release with only basic functionality implemented**. Compatibility and the overall experience are still being improved. See the README and validation records before relying on it for a particular app.

Feedback and reproducible issue reports are welcome. **If you find it useful, please give the project a Star ⭐!**
