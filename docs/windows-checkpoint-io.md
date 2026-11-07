# Windows 权重读取与原生崩溃诊断

v0.3.8 针对 Windows 上大 safetensors 文件的映射风险修复读取路径，不改变设备资源规划、BF16 权重、Turbo 的 8 步采样、用户所选尺寸或界面。

## 失败样本与证据边界

v0.3.7 的 RTX 4070 Laptop 8 GB / 32 GB 内存报告中，512 文生图关闭增强后已选择双组件磁盘流式，并成功完成模型初始化。进程在文本编码阶段以 `0xC0000005` 原生退出，故障模块为 `torch_cpu.dll`；异常读取地址与 embedding 在 safetensors 文件中的绝对偏移一致。同配置单独重跑成功，不代表 App 的连续运行已验收。

这支持映射基址异常的判断，不能只凭报告认定关闭 `safe_open` 上下文必然导致悬垂张量。safetensors 的正常存储引用允许返回张量存活。该失败样本没有随仓库提供完整 dump，具体的 Windows 映射错误码和完整原生调用栈仍待验证。

固定版本的上游源码与同类修复提供了依据：

- [safetensors v0.8.0](https://github.com/huggingface/safetensors/blob/v0.8.0/bindings/python/src/lib.rs) 的 mmap / PyTorch 路径使用全文件存储映射。
- [PyTorch v2.11.0 MapAllocator](https://github.com/pytorch/pytorch/blob/v2.11.0/aten/src/ATen/MapAllocator.cpp) 在 Windows 普通文件分支使用写时复制映射，该分支的 `MapViewOfFile` 返回值未像共享内存分支一样检查。
- [Transformers PR #48341](https://github.com/huggingface/transformers/pull/48341) 已合并 Windows 使用 pread 的修改，说明全文件写时复制映射可能耗尽 commit。

可用物理内存与 commit 余量是不同指标。整文件映射会产生提交额度开销，短暂峰值也可能发生在周期采样之间；因此物理内存仍有空闲、没有 CUDA OOM，并不能完全排除映射阶段的内存资源问题。

## 读取策略

- Windows 推理 worker 在导入 LightX2V/Transformers 前，为 safetensors 选择 `pread`，覆盖普通模型与 VAE 加载。该策略与显卡厂商、显存大小无关，不将高配设备改为磁盘流式。
- 磁盘流式适配器使用文件头索引和显式文件 I/O，将所需层读入自有 CPU 张量。保持单个 CPU staging block 与单个 GPU block 的复用。
- 流式 embedding 按 token ID 去重，只读取需要的词表行，再恢复原顺序。不会为了词表查询映射整个约 16.3 GiB 的编码器文件，也不会复制整个约 1.16 GiB 的词表。
- 文件头、形状、dtype、偏移和短读异常均在 Python 层报错，保留原错误；不在原生访问异常后盲目重复生成，也不回退到更大内存占用的完整编码器。

## 诊断与复测

每次生成仍使用独立任务目录。原生异常会保留十六进制退出码、worker PID 与 faulthandler 记录。Windows 已经生成且可访问的匹配 dump 可被记录；App 不修改 WER 注册表，不保证系统一定生成 dump，也不复制或上传转储。

主机日志区分记录者与被采样进程，并标记会话与采样范围。`GetPerformanceInfo` 的系统历史 commit 峰值不等于本次任务的 commit 峰值；本次峰值只能由该会话的实际样本统计，采样间隔内的瞬态仍可能遗漏。

RTX 手工复测应先关闭增强，以相同提示词和 seed 在 512 / 1:1 连续生成，并多次完全退出后重新启动 App；至少记录 5 次实际任务。成功后测试 1K、参考图与增强。请保存任务目录，不删除模型或重装环境来掩盖读取问题。

AMD 开发机上的回归与 CUDA 实机验收分别记录在 [本版发布说明](releases/v0.3.8.md)。开发机上的资源预算夹具验证不等于在 8 GB 独显、32 GB 物理内存上完成容量验收，也不等于冷缓存测试。
