# DeskNote 插件

DeskNote（悬浮桌面便签）的官方插件源码。主仓库只保留**插件宿主**，插件本体在这里单独维护、单独发版。

## 目录

```
wallpaper/          壁纸穿透插件（静态壁纸 / 屏幕取景 / 读 Wallpaper Engine 项目）
tools/              插件开发期的取证与诊断工具（C#，按需自行编译）
```

## 安装到 DeskNote

插件以**标签（tag）**为单位被主仓库拉取：

- 主仓库的 `plugins/lock.json` 记录要用的仓库、标签和插件目录；
- 构建前跑 `node dev/tools/fetch-plugins.mjs`，脚本 `git clone --depth 1 --branch <tag>` 本仓库，把 `plugins` 列出的目录拷进 DeskNote 的 `plugins/<id>/`；
- 需要走代理时：`set HTTPS_PROXY=http://127.0.0.1:7897`。

手动安装（不构建）：把 `wallpaper/` 整个目录复制到 DeskNote 程序目录的 `plugins/` 下即可 —— 一个 `plugin.json` + 一个 `index.js` 就能被宿主加载。

## 发版约定

- 改插件后打新 tag（如 `v1.1.0`），再改主仓库 `plugins/lock.json` 里的 `tag`；
- 插件版本号写在 `wallpaper/plugin.json` 的 `version` 字段，和 tag 保持一致；
- 主仓库的安装包会**内置**拉到的插件，用户端体验不变（不开下载）。

## 工具

`tools/` 里是当时为壁纸穿透做的取证工具，不属于插件运行时：

| 文件 | 用途 |
| --- | --- |
| `WallpaperProbe.cs` | C# COM `IDesktopWallpaper` + 每屏 DPI 探测，输出每屏 rect/wallpaper/position，`--watch` 每 2 秒一行，`--debug` 打每步 HRESULT |
| `ProbeDiag.cs` | 逐个接口调用的最小诊断（定位枚举接口返回 `RPC_X_NULL_REF_POINTER` 用） |
| `PrintTest.cs` | `PrintWindow(PW_RENDERFULLCONTENT)` 抓 Wallpaper Engine 窗口的试验 |

编译示例（Windows 自带 .NET Framework 的 csc）：

```bat
csc /codepage:65001 /target:exe /out:wallpaper-probe.exe WallpaperProbe.cs
```

## 许可

MIT，见 `LICENSE`。
