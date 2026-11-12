# 元素定位与导航（定位导航树）

> 模块 2/9 · 来源：用户实战 + PDMS Query Commands 公开资料（pipingguide.net / piping8.com）+ 项目验证（坑3）

## 每个元素有两个"地址"

| 标识 | 形式 | 特点 |
| --- | --- | --- |
| **Ref（参考号）** | 数字对，如 `= 234/702` | 数据库唯一、**创建后永不变**；最可靠的定位方式 |
| **Name（名字）** | `/` 开头全路径，如 `/PIPING`、`/SITE-1/ZONE-1/PIPE-1` | 可读性好，但**可以改名**；也支持列表位次名 `ELBO 2 OF /P1/B1` |

命令行输出里：**数字对是 ref，带 `/` 的是 name**。

## 查询：看当前元素（CE）是什么

```pml
Q REF        -- 输出 CE 的参考号，如 = 234/702
Q NAME       -- 输出 CE 的名字：可能以 / 开头，也可能是列表位次名（ELBO 2 OF /P1/B1）
Q CE         -- 输出 CE 的 name（或参考号）
Q ATT        -- CE 全部属性
Q MEM        -- CE 的成员
Q OWN        -- CE 的属主
Q POS        -- CE 相对属主的位置
```

## 定位：跳到某个元素

**知道 name → 命令行直接输**（一般都带 `/`）：

```sh
/PIPING                            -- 直接敲全路径名即定位到该元素
```

**知道 ref → 命令行直接输**（`=` 形式）：

```sh
=234/702                           -- = 前缀 + 参考号，直接定位
```

> 用户实战：命令行里拿到的 ref（查询输出、错误信息、coll 结果）可以直接回输定位。**名字会变，ref 不会**——跨会话、跨宏、写进结果文件回读的定位，一律用 ref。
> 公开资料原话：Q REF "is the best way of ensuring that you get to the element you want. Names can change but reference numbers are fixed so you always get the same element."

## PML 里的定位写法

```pml
CE /SITE-1/ZONE-1/PIPE-1       -- 按 name 路径设为当前元素（项目验证）
CE /*                          -- 回到 world 顶层
CE =234/702                    -- 按 ref 设 CE；= 形式可直接 CE（项目验证，坑3）
```

## 实战模式：coll 收集 → ref 回定位

`coll` 收集结果保存的就是元素 ref（`=234/702` 形式），这是批量处理的基石——收集到 ref，`CE` 回去读属性：

```pml
Var !W Coll All For CE
Do !i Indices !W
  !ref = !W[!i].string()       -- 取出 ref，带 = 前缀
  CE $!ref                     -- = 形式可直接 CE（坑3：coll 成员没有 .type/.name）
  !t = !!ce.type
  $P $!ref is $!t
Enddo
```

可运行版见 `assets/examples/locate_demo.pml`。

## 列表导航

```sh
NEXT        -- 列表中下一个元素
PRE         -- 上一个元素
SAME        -- 回到上一个 CE
```

配合 `Handle (2,113)`（List exhausted）判断列表到头，用法见 [control-flow](control-flow.md) 模块。
