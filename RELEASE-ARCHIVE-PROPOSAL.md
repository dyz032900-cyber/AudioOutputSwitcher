# Audio Output Switcher 版本归档建议

检查日期：2026-10-10（Asia/Shanghai）。本次仅新增归档建议文档；未运行构建或打包，未移动、覆盖、删除任何既有文件。

## 保存原则

- 历史 EXE、安装包、ZIP 和配置原件全部保留；任何清理必须先获得用户明确授权。
- 每次可发布构建使用唯一目录，目标存在即停止，不覆盖。重复构建也应分配新 build ID。
- 后续归档以复制并校验为准，不迁移原件。归档目录不直接运行，避免程序修改其中的 INI。
- 缺失的版本号、构建时间、提交映射、测试和签名证据如实标记未知，不根据文件夹名称补造。

## 当前盘点

当前分支为 `codex/package`，HEAD 为 `de2d8545a7a2a6773d3a263b6d0f492bea75ae30`。工作区已有源码、README、预览工具和历史 INI 修改，以及未跟踪文件；未更改这些内容。

| 位置 | 内容与状态 |
| --- | --- |
| `package-20260612-final/` | 1 个便携 ZIP；1 个 stage（20260613-0109），包含 EXE、INI、README；未纳入 Git 跟踪 |
| `package-20260615-latest/` | 1 个便携 ZIP；2 个 stage（1431、1452）；ZIP 对应 1452 的 EXE；1452 的工作区 INI 已改变，且多出快捷方式 |
| 根目录 | 当前 EXE 和 compilecheck EXE；均未纳入 Git 跟踪 |
| `tools/` | 2 个辅助工具 EXE，不属于用户发布包 |

共发现 7 个 EXE、2 个 ZIP。未发现 MSI、MSIX、7z 或独立安装包；现有 ZIP 均含 EXE、INI、README，可标记为便携包。7 个磁盘 EXE 的文件版本和产品版本均为 `0.0.0.0`，Authenticode 状态均为 `NotSigned`。ZIP 内 EXE 哈希与对应磁盘 EXE 一致。

以下时间为文件修改时间（已转换为北京时间），不是经构建日志验证的构建时间。

| 产物 | 字节数 | 修改时间（+08:00） |
| --- | ---: | --- |
| 20260612-final ZIP | 203080 | 2026-06-13 01:16:07 |
| 20260613-0109 EXE | 266752 | 2026-06-13 01:05:27 |
| 20260615-latest ZIP | 203076 | 2026-06-15 14:52:54 |
| 20260615-1431 EXE | 266752 | 2026-06-15 14:31:26 |
| 20260615-1452 EXE | 266752 | 2026-06-15 14:52:53 |
| 根目录当前 EXE | 274944 | 2026-10-10 19:13:45 |
| 根目录 compilecheck EXE | 278016 | 2026-08-22 15:48:39 |

### SHA-256 基线

路径均相对于项目根目录。以下校验值来自本次读取实际文件。

```text
A35E3301D8FF9D62BBD1D129E9879E44883B8942DC52F48A2DE8E214DB188BF5  package-20260612-final/AudioOutputSwitcher-package.zip
8F472ADB3A4154F873A8A6F41EF688B5A22258B48F141B949AF484201DD38176  package-20260612-final/AudioOutputSwitcher-stage-20260613-0109/AudioOutputSwitcher.exe
200145594BECB4A7A6A3DC5C0F6D0F8483E10653E009A8F91D36671B9ECA0A66  package-20260615-latest/AudioOutputSwitcher-package.zip
42577EA8A9A4F83DC70199C493FFB93313F1F64E4E00144CA74C77FB2A1353B5  package-20260615-latest/AudioOutputSwitcher-stage-20260615-1431/AudioOutputSwitcher.exe
78CA9D3F8DCF6B400E0EEB6380DC0C10CE486566DDBD265BEC93DA2226D2246B  package-20260615-latest/AudioOutputSwitcher-stage-20260615-1452/AudioOutputSwitcher.exe
1BC27A594DE39F1BE319EA057D7BA2ED273195AD8D158F000DEE49A2CF641BF7  AudioOutputSwitcher.exe
F97ABBB526DDCA5A144612F54B33248AE8A84A64873412F845EBF4BA909DAE95  AudioOutputSwitcher-compilecheck.exe
0D4B24B94E1ADC6EBC276DC6E6477C5681210BAA87E19E7E9A7BCC2317285606  tools/GenerateSpeakerIcon.exe
52607C161D0472EDDFEBF77747D0F1387C6D09BEFE4314A44099351F539C280B  tools/RenderUiPreview.exe
```

### 来源与完整性说明

- 20260615 ZIP 和两个 stage EXE 均与 HEAD 中 Git blob 一致，最后收录提交为上述 HEAD。这只能证明“收录提交”，尚不能证明“构建源码提交”。
- 20260612 包、根目录 EXE 的构建源码提交未知。当前源码有未提交修改，不能将根目录 EXE 直接标为由 HEAD 构建。
- 两个 ZIP 中的 INI 均为 590 字节，SHA-256 为 `7C39FE730134EAFAEBE10131C8853EDCA14A821DC2C451429C665103A3DE3E1F`。
- 1452 stage 现有 INI 为 597 字节，修改时间为 2026-10-03，SHA-256 为 `6A5F87C044F3CF7D6BB3D69FAC10E3EE2B489945D4FEA16A185F0B9108DB72A2`。因此 stage 目录不能直接视为原始发布包副本。
- 两个 ZIP 中的 README 与各 stage README 哈希一致：`0EF8A6DF0E012D5E2CE20C81676F942128DE94F9E12C506A92A3A840A472D225`。
- 未发现独立更新日志和明确发布版本元数据；历史功能差异待源码与发布证据核实。

## 建议结构（尚未创建）

```text
releases/
  INDEX.md
  v<版本号>/
    <YYYYMMDD-HHmmss+0800>-<短提交>-<唯一序号>/
      manifest.json
      CHANGELOG.md
      ROLLBACK.md
      SHA256SUMS.txt
      artifacts/
        AudioOutputSwitcher-<版本号>-portable.zip
        <实际存在的安装包>
      evidence/
        build.log
        validation.md
        signatures.json
  legacy/
    <原始包标识>-<归档唯一序号>/
      manifest.json
      SHA256SUMS.txt
      artifacts/
      evidence/
```

历史包使用 legacy 标识，不擅自赋予语义版本。当前 EXE 和 compilecheck 先作为来源未确认的候选产物登记，不标记为正式发布。`INDEX.md` 仅指向不可变目录，不设置会覆盖旧文件的 latest ZIP。

每份 manifest 至少记录：版本号、唯一 build ID、发布状态、真实构建时间及其证据、归档时间、时区、便携包与安装包路径/大小/SHA-256、构建源码完整 Git SHA、收录提交、工作区是否有未提交修改、构建命令与工具链、目标架构/运行时、签名状态/证书指纹/时间戳、更新日志路径、验证结果、上一版本与回滚说明。未知字段用 null 并说明原因；安装包尚未生成时明确写 missing，不以便携 EXE 代替安装包。

## 后续打包流程要求

现有 `package.ps1` 会删除同日同标签的旧 ZIP，且分钟级 stage 名称可能复用；`build.ps1` 默认写入根目录现有 EXE。本次没有执行或修改这两个脚本。后续发布前应先改为唯一输出目录并拒绝覆盖，再运行构建。

建议流程：确认源码状态 → 分配版本/build ID → 构建到新目录 → 完成功能验证 → 如需签名则先签名 → 打包 → 对最终字节计算哈希并核对签名 → 写入 manifest、更新日志与回滚说明 → 校验后纳入索引。若源码未提交，需保留源码快照或补丁及哈希，不能仅记录 HEAD。

现有 `.gitignore` 忽略通用 ZIP 和根目录 EXE，只对部分 package 路径例外；未来采用 releases 结构时需同步审查忽略规则，保证产物有明确持久保存位置。遵循现有 PACKAGING.md 的 `codex/package` 分支约定；本次不提交或推送。

## 回滚说明模板

1. 记录当前版本及配置位置，退出程序，将当前程序目录和 INI 复制到新的备份目录，保留原件。
2. 核对目标历史便携包的 SHA-256，将它解压到新的运行目录，不直接运行归档目录，也不覆盖现用目录。
3. 先用该历史包附带配置验证启动、设备切换、热键与退出；新旧 INI 的兼容性未知，未经验证不直接混用。
4. 若已启用自启动，在验证后将自启动目标切换到确认过的运行路径，确保没有多个版本同时运行。
5. 保留升级前后的目录和配置，记录结果。未来如出现安装版，单独补充安装/卸载与配置兼容步骤；任何清理仍需用户明确授权。

以上是建议流程，本次未进行回滚或运行应用验证。
