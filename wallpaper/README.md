# 壁纸穿透

把桌面壁纸按真实位置映射到便签背景：窗口移动时壁纸跟着走，换壁纸自动更新。

宿主加载约定（与 DeskNote 的插件宿主一致）：

- 目录：`plugins/wallpaper/`
- 清单：`plugin.json`（`id` / `name` / `description` / `version` / `main`）
- 入口：`index.js`，通过 `window.deskPluginHost.register({ id, mount, unmount })` 挂载

三种模式：

| 模式 | 说明 |
| --- | --- |
| 静态 | 读 `HKCU\Control Panel\Desktop` 的 `WallPaper` / `WallpaperStyle` / `TileWallpaper`，按窗口所在屏幕裁切 |
| 屏幕取景 | `getDisplayMedia` 捕获桌面，主进程用 `setContentProtection` 把便签自己排除掉，支持动态壁纸 |
| 读 WE 项目 | 从 `HKCU\Software\Valve\Steam\SteamPath` 找 Wallpaper Engine 的 `config.json`，读 `selectedwallpapers\Monitor0\file`；`.mp4` / `.webm` 循环播放，场景（`scene.pkg`）显示同目录 `preview.jpg` |

限制：只处理 `Monitor0`；场景 / 网页类壁纸不做引擎重绘，只显示预览图（插件设置里也写了这条）。
