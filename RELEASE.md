# 发布说明（维护者 checklist）

本仓库的发版是**一次有意的动作**：平时提交到 `main` 只构建/测试/打包（产物留在 CI artifact），
只有打 tag 才会真正推到 nuget.org 并建 GitHub Release。决策依据见 [`docs/0001-tickengine-design.md`](docs/0001-tickengine-design.md) 的 **D16**。

- 发布什么：只发核心库 **`TickEngine`**（其余项目在 `Directory.Build.props` 里 `IsPackable=false`）
- 谁发布：`.github/workflows/build.yml` 的 `publish` job，用 nuget.org **可信发布（OIDC）**，仓库里**没有任何长期密钥**
- 版本号唯一来源：`src/TickEngine/TickEngine.csproj` 的 `<Version>`

## 一、一次性配置（已完成，后续换账号/改仓库时复核）

nuget.org → 账号 → **Trusted Publishing**，策略字段必须与工作流严格对应：

| 策略字段 | 值 | 工作流里的对应物 |
|---|---|---|
| Package owner | `Mr.Ming` | `NuGet/login@v1` 的 `user: Mr.Ming`（**profile 名，不是邮箱**） |
| Repository Owner | `Txingou` | 仓库 `github.com/Txingou/TickEngine` |
| Repository | `TickEngine` | 同上 |
| Workflow File | `build.yml` | 文件必须叫 `.github/workflows/build.yml`（只填文件名），**发布 job 必须留在该文件内** |
| Environment | `production` | 发布 job 的 `environment: production`（不写就换不到临时 API Key） |
| Scopes | Push new packages and package versions | 推 `.nupkg` / `.snupkg` |

- GitHub 仓库侧**不需要配置任何 secret**（`GITHUB_TOKEN` 由 Actions 自动提供）。
- GitHub 的 `production` 环境目前**没有保护规则** → tag 推上去即自动发布。
  若日后在该环境上加了 **Required reviewers**，工作流不用改，但每次发版都会**停在发布 job 上等人工批准**。
- 策略可能需要"首次成功发布"才从**临时激活**转为**永久激活**（该状态只对私有仓库出现；本仓库是 public，通常无需关心）。

## 二、发版步骤

```bash
# 1) 确定版本号（语义化；预发布用 0.2.0-alpha.1 这类带连字符的形式）
# 2) 改唯一来源
#    src/TickEngine/TickEngine.csproj  →  <Version>0.2.0</Version>
# 3) 提交到 main 并推送
git add -A && git commit -m "release: 0.2.0" && git push origin main
# 4) 等 CI 在 main 上跑绿（不发布，只构建/测试/打包）
# 5) 打 tag 并推送 → 触发发布
git tag -a v0.2.0 -m "TickEngine 0.2.0"
git push origin v0.2.0
```

发布 job 会依次做：校验 `tag` 与 csproj `<Version>` 一致 → 打包 → OIDC 换临时 API Key →
推 `.nupkg` + `.snupkg` → 用 GitHub 自动生成的 release notes 建 Release（版本号含 `-` 时标为 prerelease）。

## 三、发版前的本地验证（建议照做；CI 只是第二道网）

以下命令都在**仓库根**执行（GitHub 上的仓库根就是这个 `TickEngine` 目录；本地开发工作区把该仓库放在
`.../Hiwonder/experiments/TickEngine`，所以本地要先 `cd` 进去）：

```bash
dotnet build TickEngine.sln -c Release                 # 6 个项目（Windows）
dotnet test  tests/TickEngine.Tests -c Release         # 41 项
dotnet run --project src/TickEngine.ConsoleDemo -c Release -- --smoke --seconds 3 --no-beatlog
dotnet run --project src/TickEngine.WinformDemo -c Release -- --smoke-probe
dotnet run --project src/TickEngine.AvaloniaUIDemo -c Release -- --smoke-probe
dotnet run --project src/TickEngine.Probe.AvaloniaApp -c Release -- --self-test
# 打包 + 看包内容（应含 README.md / LICENSE / lib/net8.0/TickEngine.dll / TickEngine.xml）
dotnet pack src/TickEngine/TickEngine.csproj -c Release -o artifacts
```

GUI 冒烟（`--probe-smoke` / `--smoke-probe` / `--self-test`）**只在本地跑**：它们会真开窗口，
CI 上不做，避免把"runner 桌面会话不稳"误判成代码缺陷。

## 四、失败排查

> CI 红了先看**注解**：失败用例名 + 断言消息、以及冒烟失败时的最后 30 行输出，都会以注解直接显示在
> Actions 运行页与提交的检查结果里。公开仓库的**日志与 artifact 需要登录才能下载**，所以关键信息
> 一律走注解（这是本仓库 CI 的诊断前提）。

| 症状 | 原因 / 处理 |
|---|---|
| `Verify tag matches` 步骤失败 | tag 与 csproj `<Version>` 不一致。要么改 csproj 重新提交后再打 tag，要么删掉 tag 重打 |
| `NuGet login` 报错 | 策略字段与工作流不符：文件名不是 `build.yml`、`environment` 不是 `production`、`user` 不是策略 owner 的 profile 名、或策略被置为 inactive |
| `403`/`409` 推送失败 | 版本号已存在于 nuget.org（版本号不可复用）→ 升版本号重发；`--skip-duplicate` 已让"包已存在"不视为失败 |
| 包已推上但 Release 建失败 | 直接重跑 workflow（`gh run rerun <id>` 或 Actions 页面 Re-run failed jobs）；推送步骤是 `--skip-duplicate`，重跑安全 |

## 五、语义与约束

- **已发布的版本号不可复用**：nuget.org 不允许覆盖同版本包；发现问题只能发新版本（必要时把旧版本 unlist）。
- **tag 视为不可变**：不要移动已发布的 tag；重发请用新版本号。
- 失败重试的最短路径是**删 tag 重推同一个 tag**：

  ```bash
  git push origin :refs/tags/v0.2.0   # 删远端 tag
  git tag -d v0.2.0                   # 删本地 tag
  # 修好问题后重新打同一个 tag 并推送
  ```

- 版本号语义：`0.x` 期间 API 仍可能变动；破坏性变更时提升次版本号（`0.1.0` → `0.2.0`）。
