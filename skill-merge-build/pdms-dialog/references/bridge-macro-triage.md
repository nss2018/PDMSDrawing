# 桥接宏执行异常分诊（pdms_exec 没结果时怎么办）

来源：2026-10-27 双 Design 窗口现场排障（Paragon SWX + Design WPC，drawingPDMS 桥 18753/18754）。

## 症状决策树

```
pdms_exec 客户端超时 / 没拿到结果
├─ ECONNREFUSED（连不上端口）
│    → 该端口的 PDMS 进程死了或 addin 没载。Get-Process des + 窗口标题核对。
│      进程没了 = 会话没了（数据没 SAVEWORK 就丢）。换进程/端口或让用户重开 Design。
├─ PING_OK 但 exec 无结果
│    → 三选一：
│    a) exec 线程被模态弹窗卡死  → scan 找弹窗 → dump → close（pdms-dialog 主流程）
│    b) 宏本身在跑（大 coll / 远程库慢）→ 别再发命令（会排队），等 bridge.log 出现
│       "cmd_N got result <chars>" 行 = 宏结束。结果可能已被桥接删掉（见下），out 文件为准。
│    c) 宏早炸了但错误是 TTY 非模态（无弹窗）→ 日志无 "got result"，out 文件空/缺。
│       只能缩小宏二分定位（每节独立 openfile/writefile/closefile，炸了前节数据还在）。
└─ 客户端超时但宏其实完成了
     → 桥接读走并删除了 result.txt，响应发给已断开的客户端 = 丢失。
       判据：bridge.log 有 "got result N chars"（N 大 = 有数据）。
       预防：宏一律写**专用持久 out 文件**（如 mds_p4b.txt），result.txt 只写 DONE 信号。
```

## 关键事实（决定判读方式）

1. **PING 不经过 PDMS exec 线程**（listener 线程直接回 PING_OK）。PING_OK ≠ 会话可用，只证明进程+listener 活着。
2. **result.txt 是桥接的**：`PdmsBridge.cs` 每条命令前删除、宏内 openfile 重建、命令结束后读走再删。
   → 探测/生产宏的主输出**必须**写自己的持久文件；`result.txt` 只当完成信号。
3. **客户端超时参数**（node 侧 env，默认 20s 太短）：
   `PDMS_BRIDGE_TIMEOUT`（server 等桥接响应）、`CALL_TIMEOUT`（bridge_call 整体）。远程库慢的宏给 240000+。
   客户端超时 ≠ 宏失败 ≠ 宏成功——一切以 bridge.log + out 文件为准。
4. **bridge.log 判读**（桥接队列目录，如 D:\pdmsAgent\bridge\bridge.log；多实例可能写同一文件）：
   - `cmd_N got result <chars> ... (sent in Ns)` = 宏跑完且结果被捕获（chars≈13 是 PING）
   - `handle err: 无法将数据写入传输连接...` = 响应发出时客户端已断（结果丢了，但宏已完成）
   - 只有 "sent op" 没有 "got result" = 宏还在跑 / 卡死
5. **多 Design 窗口 = 多桥**：每个 des.exe 进程各起一个 listener，端口顺延（18753/18754/...）。
   发命令前先确认**目标工程在哪个端口**：`bridge_ports.txt`（pid|port|时间）+ 窗口标题（`-Project - WPC`）+
   进程 StartTime 对齐。发错端口 = 在别的工程跑宏（轻则 MDS_MISSING，重则建错地方）。
6. **mini 活体宏**（测 exec 线程是否空闲，~1s）：
   ```
   openfile <桥接目录>\result.txt write !f1
   handle ANY
     openfile <桥接目录>\result.txt overwrite !f1
   endhandle
   writefile $!f1 |ALIVE|
   closefile $!f1
   ```
   秒回 ALIVE = exec 线程空闲；超时 = 线程被占（弹窗/大查询）。

## 宏瘦身原则（防 b/c 类故障）

- 已知引用直接 `CE /ABS/REF`（字面量，快），**不要** `coll all for <site>` 全库扫
  （实测 /MDS site 71137 后代，coll 分钟级）。
- 小集合（restraint/hanger 内 ≤20 成员）才 coll；循环一律 `.lt(N)` 封顶。
- 每节独立 openfile→writefile→closefile：炸在哪节一眼看清，前节数据不丢。
- 宏体纪律全部见 `pdms-pml` 技能坑 10–15（无 `<`、无 `CE $!var`、无注释、writefile 管道无空格、
  变量小写、handle ANY 就近保护、属性读套默认值模板）。
