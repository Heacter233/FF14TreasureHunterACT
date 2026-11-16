# CI 辅助文件

本目录只服务于 **GitHub Actions**，不参与插件本身的运行。

## `act-stub/` —— ACT 引用桩

### 为什么需要

插件（`Plugin.cs`）实现了 ACT 的 `IActPluginV1` 接口，编译时必须引用
`Advanced Combat Tracker.exe`。但这个文件：

- 不在本仓库；2.5 MB 的第三方程序，不适合随源码分发
- 官网 `advancedcombattracker.com` 对 CI 请求返回 403 / 软 404，无法自动下载
- 找不到稳定的公开镜像

因此这里用「只声明、不实现」的方式还原插件实际用到的 ACT API，
**仅用于 CI 编译期**。

### 关键技术点：强名称

ACT 是强命名程序集：

```
Advanced Combat Tracker, Version=3.8.5.288, Culture=neutral, PublicKeyToken=a946b61e93d97868
```

如果桩的身份不一致，编译出的插件 DLL 会记录错误的引用，
在真实 ACT 中启动就会绑定失败（`FileNotFoundException` / `TypeLoadException`）。

所以桩做了两件事：

1. 程序集名、版本（3.8.5.288）与真实 ACT 完全一致
2. 用 ACT 的**公钥**做 `PublicSign`，使 `PublicKeyToken` 一致

`act-public.snk` 是从真实 ACT 程序集元数据里提取的**公钥**（148 字节 CAPI blob），
不含私钥 —— 公钥本来就是随程序集公开的信息，用于此处不涉及任何密钥泄露。

> 该桩只保证**编译通过**和**引用身份正确**，不包含 ACT 的任何实现逻辑，
> 也不可能替代真实 ACT 运行。

### 覆盖范围

当前桩只声明了插件用到的 4 个成员：

| 成员 | 用途 |
|---|---|
| `IActPluginV1.InitPlugin(TabPage, Label)` | 插件入口 |
| `IActPluginV1.DeInitPlugin()` | 插件卸载 |
| `ActGlobals.oFormActMain.OnLogLineRead` | 订阅日志行 |
| `LogLineEventArgs.logLine` | 读取日志文本 |

**如果插件以后用到了新的 ACT API，CI 会编译失败** —— 这是有意设计的：
提醒你同步更新桩（`ActStub.cs`），否则 CI 与真实构建会脱节。

### 本地验证（可选）

```powershell
# 1. 构建桩
dotnet build _ci/act-stub/ActStub.csproj -c Release

# 2. 用桩编译插件
msbuild FF14TreasureHunterACT.sln /t:Rebuild /p:Configuration=Release /p:Platform=x64 `
  /p:ActDir="_ci\act-stub\bin\Release\net472"

# 3. 校验引用身份
#    产物应引用 Version=3.8.5.288, PublicKeyToken=a946b61e93d97868
```

> 本地正常开发**不需要**它 —— 直接 `/p:ActDir` 指向你安装的 ACT 目录即可，
> 编译结果与用桩编译在功能上等价（因为引用身份相同）。
