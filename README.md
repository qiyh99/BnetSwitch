# 战网账号切换管理器 (BnetSwitch)

一个 Windows 桌面小工具,在多个战网(网易国服)账号之间**一键免密切换**,不必每次重新走网易登录。技术栈 .NET 8 + WPF,**无需管理员权限**。已实测双向切换通过。

---

## 原理:换文件,不碰注册表

网易国服战网多账号切换的真相(实测):

- 每个账号的免密登录令牌都在注册表 `HKCU\Software\Blizzard Entertainment\Battle.net\UnifiedAuth` 里,**只要不登出,多个账号的令牌就一直并存**。
- **「当前登录哪个号」由 `%APPDATA%\Battle.net` 里的文件决定**(核心是 `Battle.net.config` 的 `SavedAccountNames`,第一个=当前号)。
- 所以 **切换 = 只把目标号那份 `%APPDATA%\Battle.net` 文件覆盖回去,注册表碰都不碰**。目标号的令牌本来就在 → 免密登入。

因此本工具全程只读写你自己的 `%APPDATA%\Battle.net` 文件,**不需要管理员权限**。

### ⚠️ 三条铁律(工具已内部处理,但你要知道)

1. **永远不要在战网里点「退出登录」** —— 登出会让该账号的服务端会话作废,以后要重新验证。加号请用工具的「登录新号」。
2. **绝不删/改注册表令牌** —— 那会把并存的令牌全删光。工具只换文件。
3. **关战网走正常关闭**(向所有窗口发系统会话结束消息,含最小化到托盘的),不强杀 —— 强杀会掉登录。工具已处理。

---

## 使用流程

**一次性登记每个号:**

1. 打开战网,登录账号 A → 打开本工具 → 点 **「保存当前登录为快照」**(工具会关闭战网、复制账号文件、再自动重启)。
2. 点 **「登录新号」** → 工具回到登录页(**不是登出**)→ 在战网登录账号 B → 回工具点 **「保存当前登录为快照」**。
3. 每个常用号重复。

**日常切换:** 列表里点 **「切换到此账号」** → 免密进入。列表已保存的号置顶、当前号绿色高亮。切换时会自动更新当前号的快照,保持最新。

**删除:** 每行的 **「删除」** 从工具移除该号快照(不影响战网登录)。

---

## 编译 / 运行

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download)。

```bash
dotnet build
bin/Debug/net8.0-windows/BnetSwitch.exe
```

**发布(多文件)+ 打安装包(推荐分发方式):**

```bash
dotnet publish -c Release -r win-x64 --self-contained false -o publish
# 产物:publish\ 目录(BnetSwitch.exe + 依赖 dll,框架依赖版,约 2~3MB)
# 再用 installer\app.iss(Inno Setup)把整个 publish\ 打成安装包分发
```

## 命令行 / 排错

```bash
BnetSwitch.exe --selftest       # 读账号库自检 → selftest.txt
BnetSwitch.exe --save           # 关战网→存当前登录号→重启 → save.txt
BnetSwitch.exe --switch <账号ID> # 无界面切换 → switch.txt
BnetSwitch.exe --addaccount     # 回登录页(不登出)以登新号 → addaccount.txt
```

数据在 `%LOCALAPPDATA%\BnetSwitch\`(`accounts\<id>\` 快照、`crash.log`、`settings.json`)。

---

## 项目结构

```
App.xaml(.cs)                    入口、全局异常、命令行 --selftest/--save/--switch/--addaccount
MainWindow.xaml(.cs)             主界面
Models/BattleAccount.cs          账号数据模型
Services/
  BattleNetPaths.cs              解析战网目录 + 自动定位 Battle.net.exe
  AccountReader.cs               从 CachedData.db 读账号列表与当前登录号
  AppDataStore.cs                ★ %APPDATA%\Battle.net 文件的存/还原/删 + 新建账号清指针(切换核心)
  BattleNetController.cs         ★ 关闭(EnumWindows+WM_ENDSESSION,不强杀)/ 启动战网
  AppSettings.cs                 本工具设置
ViewModels/MainViewModel.cs      刷新 / 登录新号 / 保存 / 切换 / 删除 的编排
（RegistryStore.cs 为早期错误的注册表方案,已弃用不引用)
```

---

## 开源与许可

本项目(**客户端**)基于 **GNU GPLv3** 开源,完整条款见 [LICENSE](LICENSE)。

- 你可以自由使用、研究、修改、分发本软件。
- 若你分发修改版,**必须同样以 GPLv3 开源你的修改**,并保留版权与许可声明。
- 本软件不含任何担保(见下方免责声明)。

> 说明:仓库只开源桌面客户端。激活码/广告等后端服务与官网**不在开源范围内**,与本客户端的核心切换功能无关——本工具的账号切换功能**完全免费、离线可用**,不依赖任何服务器。

## 免责声明

- 本工具是**独立的第三方开源项目**,与暴雪娱乐(Blizzard Entertainment)、网易(NetEase)**无任何隶属、授权、合作或背书关系**。
- "战网 / Battle.net""守望先锋 / Overwatch"等名称与商标归其各自权利人所有;本项目仅出于**描述兼容性**的目的提及,属正当的说明性使用。
- 本工具**只在你本机、对你自己的账号数据进行操作**:读写你自己的 `%APPDATA%\Battle.net` 文件以实现本地免密切换。它**不修改、不绕过、不破解**暴雪/网易的任何认证、授权或付费机制,**不上传你的账号凭据**到任何服务器。
- 守望先锋战绩查询功能通过**网易大神的公开接口**获取由你本人扫码授权的数据;本项目不含、不分发任何暴雪/网易的专有代码或资源。
- 本软件按"**现状**"(AS IS)提供,不作任何明示或默示担保。**因使用本工具导致的任何后果(包括但不限于账号异常、封禁、数据丢失)均由使用者自行承担**,作者不承担任何责任。
- 使用者应自行遵守暴雪/网易的用户协议与相关服务条款,并自行判断在其所在地区使用此类工具的合规性。若你不接受以上条款,请勿使用本工具。
