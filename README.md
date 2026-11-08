# Emby 插件合集：Strm Size Probe / Media Merge

两个面向 Emby（.NET 8，Emby 4.9 / 4.10 实测可用）的社区插件，源码独立、可分别编译安装。
两者都只操作 Emby 的**库层数据**，不移动、不重命名、不删除任何媒体文件。

| 插件 | 目录 | 作用 | 默认安全姿态 |
| --- | --- | --- | --- |
| **Strm Size Probe** | `StrmSizeProbe/` | 探测 `.strm` 指向的真实文件大小，写回 Emby 的 `Size` 字段 | `写入媒体库` 默认关闭，只出报告 |
| **Media Merge（多版本合并）** | `MediaMerge/` | 把 TMDB 相同、路径不同的重复电影条目合并为一条多版本记录 | `干跑模式` 默认开启，只出报告 |

## 环境要求

- Emby Server 4.9.x / 4.10.x（.NET 8）
- 编译需 .NET SDK 8.0

## 编译

插件引用的 `MediaBrowser.*.dll` 属于 Emby 私有程序集，不随仓库分发，需从你自己的 Emby 安装中复制。

```bash
git clone <this-repo> && cd <this-repo>
mkdir -p ref
# Docker 部署：从容器内复制
docker cp emby:/system/MediaBrowser.Common.dll       ref/
docker cp emby:/system/MediaBrowser.Model.dll        ref/
docker cp emby:/system/MediaBrowser.Controller.dll   ref/
docker cp emby:/system/Emby.Web.GenericEdit.dll      ref/
# 裸机部署：源目录通常为 /opt/emby-server/system/

dotnet build StrmSizeProbe/EmbyStrmSize.csproj -c Release
dotnet build MediaMerge/EmbyMediaMerge.csproj     -c Release
```

产物：`StrmSizeProbe/bin/Release/net8.0/EmbyStrmSize.dll`、`MediaMerge/bin/Release/net8.0/EmbyMediaMerge.dll`。
若 `ref/` 放在别处，可覆盖：`dotnet build ... -p:EmbyRefDir=/path/to/dlls`。

## 预编译版本

`dist/` 内为按本仓库源码构建的可用 dll（.NET 8，`DebugType=None`、`Deterministic=true`，不含调试符号与本机构建路径），可直接安装，无需自己编译：

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `dist/EmbyStrmSize.dll` | 43 KB | Strm Size Probe |
| `dist/EmbyMediaMerge.dll` | 43 KB | Media Merge（多版本合并） |

校验完整性：

```bash
cd dist && sha256sum -c SHA256SUMS.txt
```

想自行复核：按上文「编译」重建时加 `-p:DebugType=None -p:DebugSymbols=false -p:Deterministic=true -p:ContinuousIntegrationBuild=true`，产物 SHA256 应与 `dist/SHA256SUMS.txt` 一致。
替换前建议先备份 `config/plugins/` 下的同名 dll。

## 安装

```bash
# 把 dll 放进 Emby 配置目录下的 plugins
docker cp EmbyStrmSize.dll emby:/config/plugins/
docker cp EmbyMediaMerge.dll emby:/config/plugins/
docker restart emby
```

重启后在「控制台 → 插件 → 我的插件」可见两个插件；配置项在插件详情页内。
插件启动日志前缀分别为 `[StrmSize]` 与 `[MediaMerge]`。

## 卸载 / 回滚

1. 插件页停用或删除 dll；
2. 删除 `config/plugins/configurations/` 下对应 json；
3. **Media Merge 曾实跑合并的库建议先备份 `config/data/library.db` 再操作**，合并本身不可通过卸载插件撤销（可用 Emby 自带「拆分版本」人工还原）。

## 声明

- 个人自用插件，无官方支持，不保证适配未来 Emby 版本；
- 请在测试库验证后再用于生产库，涉及写库/合并前务必备份；
- MIT License，见 `LICENSE`。
