# AtlasBuilder —— 地图贴图图集工具

把一张地图用到的贴图块预先合成为少数几张 2048² 大图，配合 `Server.Web` 的 `/Atlas` 静态托管，
把地图编辑器的贴图请求从「一块一个 HTTP 请求」降到「一页一个请求」。

- **按图单独出图集**，不跨图共享：一张图只用全库约 12% 的块，全局共享会让每张图下载
  33~60 页、九成是别的图的块，单图字节涨 1.8~4 倍。
- 产物由 `Server.Web` 挂在 `/Atlas` 下，前端 `map-editor.js` 自动使用。
  **取不到 `index.json` 时自动退回逐块请求** —— 所以删掉产物目录就等于关掉这个功能，
  不需要回退 dll。
- 跨平台 `net10.0`，不依赖 WinForms，可以在 Linux 部署机上直接生成。

## 编译

```bash
dotnet build AtlasBuilder/AtlasBuilder.csproj -v q --nologo
# → AtlasBuilder/bin/Debug/net10.0/AtlasBuilder（Windows 上带 .exe）
```

## 三个概念

| 概念 | 说明 |
|---|---|
| **客户端根目录** | 含 `Data/*.Zl`（贴图库）和 `Map/*.map`（地图）的目录。`--client` 指这里，**不是** `Data` 本身 |
| **产物目录** | `--out`。默认 `<程序目录>/../Atlas`，即程序放在 `<Server.Web>/AtlasBuilder/` 时正好落到 `<Server.Web>/Atlas` |
| **图名** | `--maplist` 用的名字，不带 `.map`。`0` / `00` / `0_000` 是**三张不同的图** |

## 子命令

| 子命令 | 写盘 | 用途 | 典型耗时 |
|---|---|---|---|
| `scan` | ❌ 只统计 | 算「现状 vs 图集」的请求数 / 页数 / 字节，决定值不值得做 | 1~2 分钟（要真拼真编码量体积）|
| `sizes <地图名>` | ❌ | 看一张图的贴图尺寸分布，用来判断 `--pack` | 几秒 |
| `digest <库>:<图> ...` | ❌ | 给指定贴图算 PNG 的 MD5，做解码改动的像素回归 | 秒级 |
| `build` | ✅ | 全量（或 `--maplist` 指定几张）出图集。先写 `{图}.tmp` 再整体 `Directory.Move`，**不留半成品** | `0` 3.4s / `15` 21.5s |
| `update` | ✅ | 按 `manifest.json` 的 `Tag`（`长度-mtime`）跳过没变的图；清理已删图的目录与残留 `.tmp` | 无变化 0.1s |
| `verify` | ❌ | 结构自洽 + `--pixels N` 像素对拍 | 结构秒级；`--pixels` 要解码整页 PNG，按 N 线性涨 |

## 参数表

| 参数 | 默认 | 说明 |
|---|---|---|
| `--client <目录>` | 写死的开发机路径 | 客户端根目录。默认值见 `Program.cs` 的 `Options.ClientRoot`，换机器必然对不上 —— **所有命令都建议显式给**，否则贴图库读不到：`build` 全图失败、`verify` 假通过 |
| `--maps <目录>` | `<client>/Map` | 只改「地图从哪读」，**不选图**（选图用 `--maplist`）|
| `--maplist a,b,c` | 全部 | 只处理这几张图；逗号 / 分号 / 空格分隔；可带可不带 `.map` |
| `--out <目录>` | `<程序目录>/../Atlas` | 产物目录 |
| `--page <边长>` | `2048` | 图集页边长。`4096` 请求更少，但一页 67 MB 显存 |
| `--pack shelf\|bucket` | `shelf` | `shelf` = 跨库混装（实测更省）；`bucket` = 按高度精确分桶，**仅用于对照** |
| `--jobs <N>` | CPU 核数 | 并行度。PNG 编码吃 CPU，串行跑 258 张要一个多小时 |
| `--pixels <N>` | `0` | 仅 `verify`：每张图抽 N 块逐像素对拍。`0` = 只校验结构 |
| `--top <N>` | `12` | 仅 `scan` / `sizes`：列表显示前 N 项 |
| `--sample <N>` | `40` | 仅 `scan`：采样多少块估算单块平均体积 |
| `--permap <N>` | `0` | 仅 `scan`：对最重的前 N 张图额外做「按图单独装箱」实测 |

**`update` 的三个「全量重建」触发器**（`manifest.json` 里任一不一致就重来，几十分钟）：

1. `PageSize` 变了（`--page`）
2. `Padding` 变了（`Program.AtlasPadding`，当前 2）
3. `Pack` 变了（`--pack`）或 `Decoder` 变了（`ZlLibraryReader.Version`，改了读取/解码逻辑就要 +1）

触发时打印：

```
! 页大小 / 装箱策略 / 解码版本 与已有产物不同 → 全部重建
```

## 典型用法

```bash
# ① 先算账（只统计、不写盘）
./AtlasBuilder scan --client <客户端根目录> --permap 5

# ② 看一张图的尺寸分布
./AtlasBuilder sizes 15 --client <客户端根目录> --top 20

# ③ 出图集：只出 0 和 15 两张
./AtlasBuilder build  --client <客户端根目录> --out <产物目录> --maplist 0,15

# ④ 增量更新（日常用这条，没改地图时几乎零耗时）
./AtlasBuilder update --client <客户端根目录> --out <产物目录>

# ⑤ 校验：结构 + 每张图抽 10 块逐像素对拍
./AtlasBuilder verify --client <客户端根目录> --out <产物目录> --pixels 10

# ⑥ 像素回归：改 ZlLibraryReader 前后各跑一次，比 MD5
./AtlasBuilder digest 4:6293 0:100 --client <客户端根目录>
```

`--maplist` 的写法：

```bash
./AtlasBuilder build --maplist 0        # 只出 0.map
./AtlasBuilder build --maplist 0,00,15  # 三张
./AtlasBuilder build                    # 全部 258 张（约 3 GB，几十分钟）
```

> **磁盘取舍**：按图单独出图集不跨图共享，258 张累计约 **3 GB**。
> 建议只对「地图编辑器里点开过的图」按需 `build --maplist <图名>`。

## 产物布局

```
<--out 目录>/
├── manifest.json          全局清单：Generated / PageSize / Padding / Pack / Decoder / ClientRoot
│                          + Maps{地图 → Tag, Pages, Bytes, Blocks, Version, BuildMs}
├── 0/
│   ├── index.json         帧表：Pages[页文件名] + Frames[库索引][图下标] = [页号, x, y, w, h]
│   ├── 0.png
│   └── ...
└── 15/
    ├── index.json
    └── ...
```

`--out` 默认是 `<程序目录>/../Atlas`（`AppContext.BaseDirectory` 的上一级），所以按
「程序放 `<Server.Web>/AtlasBuilder/`」部署时，不写 `--out` 正好落在 `<Server.Web>/Atlas`，
与 `Server.Web/Program.cs` 里挂的 `/Atlas` 一致。**不在这套布局里就一定要显式写 `--out`。**

服务端读图集的目录可用环境变量 `ZIRCON_ATLAS_DIR` 覆盖。部署后核对两者一致：

```bash
systemctl cat zircon-web.service | grep -E 'WorkingDirectory|ExecStart'
ls -d <ContentRoot>/Atlas && head -c 200 <ContentRoot>/Atlas/manifest.json
```

## 输出怎么读

### `build` / `update`

```
输出目录：<产物目录>
地图目录：<客户端根目录>/Map（2 张）
页边长  ：2048（padding 2，装箱 Shelf）

  [  1/  2] 0              7 页   29.0MB   4859 块   3421 ms
  [  2/  2] 15            30 页  170.6MB  22852 块  21504 ms

------------------------------------------------------------------------------
全量构建完成：新建 2 张 / 跳过 0 张
产物合计：2 张图、37 页、199.6MB
耗时：24.9s
```

- 行首 `[done/总数]`：`done` 是**已完成**计数，不是当前序号（并行时两者不等）。
- 出现 `失败 N 张` → 退出码 1，逐条看 `FAIL <图名> <原因>`。
- `清理 N 张` = 本次删掉的孤儿图集目录数（`update` 才有）。

### `verify`

```
校验 2 张图、27,711 个帧、199.6MB（每张图抽 10 块，实际对拍 20 块）
结果：**全部一致** ✔
```

有问题时最多列 40 条、退出码 1：

```
结果：发现 3 个问题
  - 0: 索引写 7 页，目录里有 6 个 png
  - 15: 库 4 图 6293 的矩形越界 (2040,100,32,32)
  - 有 7 个贴图库读不到（索引 0,1,...）：--client 当前为 <路径>，应指向含 Data\*.Zl 的目录
```

**关键看「实际对拍 N 块」**：如果是 0，说明贴图库一个都没读到，此时的「全部一致」是假阳性。

### `scan`

用来做决策，关键看两栏：

- `=== 按贴图库 ===`：全量口径的字节比，**说明不了单图体验**（页是跨图共享的）
- `=== 单图对照（按现状字节，前 N） ===`：**这栏才决定成败** —— 一张图只下它触及的那几页

## 常见问题

| 现象 | 真因 | 处理 |
|---|---|---|
| `verify` 打印「全部一致」但明显不对 | `--client` 指错 → 贴图库读不到 → 对拍整块跳过 | 看「实际对拍 N 块」是不是 0；是 0 就是假阳性，显式给 `--client` |
| `build` 全图 `FAIL 这张图没有可用的贴图（贴图库缺失？）` | `--client` 没指向含 `Data/*.Zl` 的目录 | 显式给 `--client` |
| `update --maplist X` 之后别的图集目录没了 | 存活集算错 | 已修成「地图目录里的全部 `.map`」。升级前别拿产物目录试 `--maplist` |
| `update` 只处理两张，结尾却打「2 张图、7 页」 | 合计从本次循环累加 | 已修：从 `manifest.json` 算 |
| `verify` 报「像素与贴图库不一致」 | PNG 存未预乘颜色，`alpha=0` 的像素 RGB 无意义（Skia 抹成 0），真实数据有「全透明但 RGB 非零」的块 | 已修：两边 alpha 都为 0 时跳过 RGB；只要一边 alpha > 0 就必须一致 |
| 动画格在图集里不动、退回逐块请求 | `.map` 只存首帧下标，后续帧 = `首帧+1..首帧+count-1` | 已修：`MapScanner.Emit` 按 `count` 展开，判据与 `map-editor.js` 的 `count > 1 && animationFrame < 255` 一致 |
| 页面贴图全黑、控制台无报错 | 前端对帧纹理调了 `destroy(true)`，把整页 GPU 纹理一起释放 | 帧纹理只 `destroy(false)`；页纹理淘汰时才 `destroy(true)`，且保护当前版本 |
| `/Atlas/...` 一直 404 | `Atlas` 目录不在进程 ContentRoot 下 | 设 `ZIRCON_ATLAS_DIR`，或核对 `WorkingDirectory` |
| `/Atlas/...` 200 但 0 字节 | 静态文件提供器目录不存在 | `ls` 确认目录与文件真在 |
| 页面 PNG 每次刷新都重下 | `index.json` 的 `no-cache` 头串到了 PNG 上 | `OnPrepareResponse` 只对 `.json` 给 `no-cache`，其余 `immutable`（URL 带 `?v={Version}`）|
| `./AtlasBuilder` 报 `Permission denied` | 传输时丢了可执行位 | `chmod +x ./AtlasBuilder` |
| `./AtlasBuilder` 报 `libSkiaSharp.so` 找不到 | 发布时漏了 native 资产 | 确认发布目录里有 `libSkiaSharp.so`（应随 publish 自动带）|

## 实测基线（2026-11-13，含动画帧）

| 地图 | 唯一块 | 页数 | 请求（页 + 索引）| 字节 | 耗时 |
|---|---|---|---|---|---|
| `0` | 4,859 | 7 | **8** | 29.0 MB | 3.4s |
| `15` | 22,852 | 30 | **31** | 170.6 MB | 21.5s |
| 两张合计 | — | 37 | 39 | 199.6 MB | — |

改动后跑出明显偏离上表的数字，先怀疑「动画帧没收全」或「`--client` 指错了」。

## 源码结构

| 文件 | 作用 |
|---|---|
| `Program.cs` | 命令分发 + 参数解析 + `scan` / `sizes` / `digest` |
| `AtlasCommands.cs` | `build` / `update` / `verify` |
| `AtlasWriter.cs` | `manifest.json` / `index.json` 读写、版本短哈希、`ReaderCache` |
| `AtlasPlan.cs` | `AtlasPlanner.Plan`（跨库合装）+ `Composite`（真拼真编码）|
| `ShelfPacker.cs` | `shelf`（混装，默认）/ `bucket`（精确分桶，仅对照）|
| `MapScanner.cs` | 扫 `.map`，**按动画帧数展开引用** |
| `../ZlMedia/ZlLibraryReader.cs` | `.Zl` 读取 / PNG 编解码（`Version` 参与产物版本号）|
| `../Server.Web/Program.cs` | 把 `<ContentRoot>/Atlas` 挂到 `/Atlas` |
| `../Server.Web/wwwroot/js/map-editor.js` | 拉 `index.json`、切帧纹理、未命中退回逐块 |
