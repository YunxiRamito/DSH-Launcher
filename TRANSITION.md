# 大肥鱼Go 1.4.9 过渡说明

`1.4.9` 是品牌迁移版本，不是内部协议大改版本。目标是在不打断旧用户的前提下，
先把可见品牌和仓库地址切到 `大肥鱼Go / Dafeiyu-Go`。

## 这版已经改名

- 设置窗口、托盘提示、通知、程序属性和安装完成文案使用“大肥鱼Go”。
- 程序集产品名和标题使用 `Dafeiyu-Go`。
- 启动器主发布仓库改为：
  `YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run`
- 安装器目标仓库名定为：
  `YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Setup`
- 网络请求的用户代理改为 `Dafeiyu-Go/<版本>`。

## 这版刻意不改

以下名称属于升级、自启和卸载协议，`1.4.9` 继续沿用旧值：

- `DeepSeek Harness.exe`
- `DeepSeek Harness.Core.exe`
- `%LOCALAPPDATA%\DeepSeekHarness`
- `HKCU\Software\DeepSeekHarness`
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeepSeekHarness`
- 计划任务 `DeepSeekHarnessAutostart`
- 快捷方式 `DeepSeek Harness.lnk`
- npm 包 `@yunxiramito/dsh-launcher`
- 发布包 `DeepSeekHarness-<版本>.zip`

`1.4.9` 自更新会依次尝试新仓库和旧仓库地址，旧客户端仍可通过旧地址升级到本版。

## 发布顺序

1. 先在 GitHub 将启动器重命名为目标仓库名。
2. 构建 `1.4.9`，确认 `manifest.json` 指向新仓库。
3. 发布 npm、等待 npmmirror 同步。
4. 创建 GitHub Release，上传 `DeepSeekHarness-1.4.9.zip`。
5. 旧版启动器通过旧仓库重定向或旧地址回退升级到 `1.4.9`。
6. 再发布安装器 `1.4.9`，让它从新启动器仓库读取清单。

## 后续版本

内部文件名、目录和注册表键计划至少保留到 `1.5.x`。若后续要物理改名，必须先加入
旧路径检测和一次性迁移，不能直接替换常量。
