# Steam账户切换器 V1.1（白色主题版）

一个单文件、免安装、零依赖的 Steam 账户快速切换小工具（C# WinForms，用 Windows 自带的 .NET Framework 编译器即可构建，无需安装任何 SDK）。

## 使用

双击运行 `Steam账户快速切换.exe`：

1. **账户卡片**：显示当前选择的账户（头像、账户名、昵称、上次登录日期）。点击卡片会向下展开账户列表。
2. **展开列表**：点击任意账户即可选中；鼠标悬停到账户上，右侧会出现 **×** 删除按钮，可移除该账户的本机登录记录（仅删除 `loginusers.vdf` 中的记录，不影响 Steam 账户本身）；底部为“新建账户”入口。
3. **离线**：勾选后将以离线模式启动 Steam（写入 `WantsOfflineMode`，无网络时也能进游戏）。
4. **选项**（弹出窗口中设置）——
   - 静默启动（`-silent`，Steam 启动后直接缩到托盘）
   - 禁用内置浏览器（`-no-browser`，无网络/内嵌页面打不开时使用）
   - 保持以离线模式启动：需配合“记住选择的选项”使用——两者同时勾选时，每次打开软件默认勾选离线；只勾选“记住”时其他选项仍会记忆，但离线每次默认不勾选
   - 记住选择的选项：开启时保存上述勾选，下次启动仍然生效；取消后每次启动均恢复默认不勾选
   - 启动 Steam 后退出程序：检测到 Steam 成功运行后自动关闭本工具
5. **启动 Steam**：将所选账户设为自动登录并按当前参数启动。若 Steam 正在运行会先提示退出。

## 新建账户

在 Steam 中“更改账户”退出当前登录，登录新账户时勾选“记住密码”，该账户会自动出现在列表中。

## 原理

- 读取注册表 `HKCU\Software\Valve\Steam` 定位 Steam。
- 解析/回写 `Steam\config\loginusers.vdf`（保留原有编码，切换 `AutoLogin`/`MostRecent`、`WantsOfflineMode` 字段，兼容新旧版 Steam）。
- 头像自动识别 Steam 本地缓存目录 `config\avatarcache\<SteamID>.png`（Steam 登录时自动缓存），并复制一份到 `C:\Users\<用户名>\SteamSwitcherCache\` 做离线备份；无头像显示“?”占位图。
- 缓存（头像备份、记住的账户、启动参数）统一保存在 `C:\Users\<用户名>\SteamSwitcherCache\`。
- 删除账户 = 从 `loginusers.vdf` 移除对应条目并清理本工具的头像缓存。
- 窗口与 exe 图标使用工作目录下的 `steam.ico`（由编译时 `/win32icon` 原样嵌入，无损；替换该文件后重新编译即可更换）。

## 编译

Windows 10/11 自带 .NET Framework 编译器，在源码目录执行一条命令即可，无需安装任何 SDK：

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 /target:winexe /platform:anycpu /win32icon:steam.ico /out:Steam账户快速切换.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll Switcher.cs
```

生成 `Steam账户快速切换.exe`。图标取自工作目录下的 `steam.ico`（不存在则去掉 `/win32icon:steam.ico` 参数）。

## 项目结构

```
├── Switcher.cs   # 全部源码（单文件，含 VDF 解析、UI、头像加载）
├── steam.ico     # 程序图标
├── LICENSE       # MIT 许可证
└── .gitignore
```

欢迎提 Issue 和 PR。

## 许可证

[MIT](LICENSE)
