# -*- coding: utf-8 -*-
"""DXF 块化后处理（2026-10-24 用户「表格和布置图画完后分别做成块，这样CAD里好挪动」）。

用法:
    python dxf_block.py <dxf_path> <tx0> <ty0> <tx1> <ty1> [表块名] [图块名] [max_x] [colorize] [style]

行为:
  - 完全落在表格矩形 [tx0,ty0]-[tx1,ty1] 内的实体 → 块「设备一览表」
  - 其余全部实体 → 块「布置图」
  - 两块各以 INSERT 回插到原点(0,0)、块内保持绝对坐标 → CAD 里视觉完全不变，
    但可整体选中/移动各自的块。
  - 幂等：块名已存在且 modelspace 已有对应 INSERT 时跳过块化，但仍执行表格修正。
  - (10p) 表格修正 fix_table（幂等，每次运行都会执行）：
      1) 表格文字下移 5h/6（≈一个字高）：PLOT DXF 导出器把 Hbody 定位点写成
         baseline = at_y + h/3，居中需要 at_y - h/2，合计差 5h/6 → 文字原先
         压在行的上边线上（用户截图 2026-10-24「表格有错位」）。
      2) 序号/标高列（列中心 x）文字补 halign=Center（原导出按左对齐落在列中心，
         "-0.300" 会顶出右边框）。
      3) 中文表头/标题死块注入：导出器丢全部非 ASCII BTEX（全图 741 文字 0 中文实证）。
         按表格矩形反推 x0T/y0T，把标题「设备一览表」+ 表头四列做成静态块
         「设备一览表表头」，以 INSERT(0,0) 嵌进表格块（死块无属性，随表格块整体挪动）。
         （2026-10-24 用户「表头不行了，做成死块直接插入」；此前注入散 TEXT + 原地升版
         R2007 导致 CAD 打开空文件，已废弃升版逻辑。）
      4) 误分进表格块的轴网气泡（文字 h>3.4、圆弧段、短散线）挪回「布置图」块。
      5) 删除模板残留文字（原点附近的 "-"）。
  - (10aa) 第 10 参 style="embed" → 埋板英文表头分支（headlessDrawing EquipTableCore 直绘
      英文表头，ASCII 导出不丢字，无需死块注入）：
      a) 表格块内全部 TEXT 设宽度因子 0.8（用户「设备文字宽改成0.8」；中文分支同样生效）；
      b) 全表文字下移 5h/6 后一律 halign=Center（embed 表所有单元格都是居中版式）；
      c) 气泡挪出阈值放宽 h>4.2（embed 标题字 h=4 不误挪，轴网气泡 h≈4.4 仍会挪走）；
      d) 跳过中文表头死块注入。

退出码: 0=成功(含幂等跳过) 1=失败(输出错误到 stderr)
"""
import re
import sys


def _unescape(s):
    r"""DXF 转义名（\u+8bbe 或 \U+8bbe 两种大小写）→ 中文明文。"""
    return re.sub(r"\\[uU]\+([0-9a-fA-F]{4})", lambda m: chr(int(m.group(1), 16)), s or "")


def _blk_lookup(name_map, name):
    """按名查块（大小写不敏感——部分 ezdxf 版本会把 ASCII 块名归一成小写，
    中文名无此问题，2026-10-25 embed 冒烟实证 'SHE_BI'→'she_bi'）。"""
    n = name_map.get(name)
    if n is not None:
        return n
    low = (name or "").lower()
    for k, v in name_map.items():
        if k.lower() == low:
            return v
    return None


def fix_table(doc, rect, tbl_name, drw_name, log=print, style=""):
    """(10p) 表格文字修正 + 中文表头注入 + 杂散元素归位（幂等）。
    (10aa) style="embed" → 埋板英文表头分支：全表居中、文字宽 0.8、跳过中文死块注入。"""
    try:
        tx0, ty0, tx1, ty1 = rect
        emb = (style or "").strip().lower() == "embed"
        name_map = {_unescape(n): n for n in doc.blocks.block_names()}
        if _blk_lookup(name_map, tbl_name) is None:
            log("tablefix: 无表格块，跳过")
            return
        blk = doc.blocks.get(_blk_lookup(name_map, tbl_name) or "")
        drw_d = _blk_lookup(name_map, drw_name)
        drw = doc.blocks.get(drw_d) if drw_d else None
        # 与 EquipTableCore 落盘约定对应: rx0 = x0T-2, ry1 = y0T+8
        x0T = tx0 + 2.0
        y0T = ty1 - 8.0
        tblW = (tx1 - 2.0) - x0T
        cXh = x0T + 6.0                    # 序号列中心
        cXe = x0T + tblW - 9.0             # 标高列中心 (10w: 12+33+40+24+18/2=116 → tblW-9)
        cXt = x0T + tblW / 2.0             # 标题中心
        try:
            doc.appids.add("TBLFIX")
        except Exception:
            pass

        def _tagged(e):
            try:
                e.get_xdata("TBLFIX")
                return True
            except Exception:
                return False

        # 1) 文字下移 5h/6 + 2) 居中列 halign + (10aa) 文字宽度因子 0.8
        #    embed：标题 h=4 也要下移（Hbody baseline 偏移与字高成正比）→ 阈值放宽 4.05；
        #    全表居中版式 → 所有下移文字一律 halign=Center（中文分支维持列中心近似判定）。
        #    (10ac) 2026-10-26 embed 分支表格文字统一 ARIAL 字体（宽度因子 0.8 见下）。
        if emb:
            try:
                doc.styles.add("ARIAL", font="arial.ttf")
            except Exception:
                pass  # already exists
        n_shift = n_center = n_width = n_font = 0
        # (10ad) 2026-10-26 用户「第二行英文标题字宽 0.5」：embed 表头行（含两行字的下行）宽 0.5，
        #        标题行/数据行维持 0.8。表头行带由矩形确定性推导：ry1 = y0T + titleH(8) + 2
        #        → y0T = ty1 - 10；表头带 [y0T-10, y0T] = [ty1-20, ty1-10]（标题带 [ty1-10, ty1]）。
        #        幂等：表头字下移 5h/6≈2.1mm 后仍在带内（-4.6/-9.6 > -10），重跑判定不漂移。
        hdr_y0 = (ty1 - 20.0) if emb else None
        hdr_y1 = (ty1 - 10.0) if emb else None
        for e in blk:
            if e.dxftype() != "TEXT":
                continue
            # (10aa) 设备文字宽度因子 0.8（幂等：重复设置无副作用；PDF/Draft 侧无宽度属性不生效）
            # (10ad) embed 表头行带内文字宽 0.5
            try:
                wf = 0.5 if (emb and hdr_y0 <= e.dxf.insert.y <= hdr_y1) else 0.8
                if abs((e.dxf.get("width", 1.0) or 1.0) - wf) > 1e-6:
                    e.dxf.width = wf
                    n_width += 1
            except Exception:
                pass
            # (10ac) embed 表格文字统一 ARIAL 字体（幂等）
            if emb:
                try:
                    if (e.dxf.get("style", "") or "") != "ARIAL":
                        e.dxf.style = "ARIAL"
                        n_font += 1
                except Exception:
                    pass
            if _tagged(e):
                continue
            h = e.dxf.height
            p = e.dxf.insert
            if h <= (4.05 if emb else 3.0):  # embed 含标题字 h=4；cn 气泡字 h≈4.4 不动，稍后整体挪走
                e.dxf.insert = (p.x, p.y - h * 5.0 / 6.0)
                n_shift += 1
                if emb or abs(p.x - cXh) <= 1.0 or abs(p.x - cXe) <= 1.0 or abs(p.x - cXt) <= 1.0:
                    e.dxf.halign = 1            # Bottom-Center
                    e.dxf.align_point = (p.x, p.y - h * 5.0 / 6.0)
                    n_center += 1
            try:
                e.set_xdata("TBLFIX", [(1000, "10p")])
            except Exception:
                pass
        log("tablefix: 文字下移 %d 个，居中 %d 个，宽度0.8 %d 个，ARIAL %d 个" % (n_shift, n_center, n_width, n_font))

        # 4) 误入表格块的轴网气泡 → 挪回布置图块（圆弧段/短散线/大字）
        #    (10aa) embed：阈值 3.4→4.2——标题字 h=4 属表格内容不挪；轴网气泡 h≈4.4 仍挪走
        n_moved = 0
        big_h = 4.2 if emb else 3.4
        if drw is not None:
            for e in list(blk):
                move = False
                if e.dxftype() == "TEXT":
                    move = e.dxf.height > big_h
                elif e.dxftype() == "POLYLINE":
                    try:
                        bulged = any(abs(v.dxf.bulge) > 1e-9 for v in e.vertices)
                    except Exception:
                        bulged = False
                    pts = [(v.dxf.location[0], v.dxf.location[1]) for v in e.vertices]
                    length = sum(((pts[i][0] - pts[i + 1][0]) ** 2 + (pts[i][1] - pts[i + 1][1]) ** 2) ** 0.5
                                 for i in range(len(pts) - 1)) if len(pts) > 1 else 0.0
                    move = bulged or (0 < length < 8.0)
                if move:
                    try:
                        blk.unlink_entity(e)
                        drw.add_entity(e)
                        n_moved += 1
                    except Exception:
                        pass
            log("tablefix: 气泡/杂散挪回布置图块 %d 个" % n_moved)

        # 3) 中文标题/表头 → 死块「设备一览表表头」直接插入表格块（幂等）
        #    (10aa) embed 分支跳过：英文表头由 Draft 直绘（ASCII 导出不丢字），注入中文反而重复
        HDR_NAME = "设备一览表表头"
        hdr_stored = _blk_lookup(name_map, HDR_NAME)
        if emb:
            log("tablefix: embed 英文表头分支——跳过中文表头死块注入")

        # 清理旧版散 TEXT 表头（style=TBL_CN 的即此前注入的），统一换成死块。
        # 两块都清：旧版标题 h=3.5 可能被气泡搬移逻辑挪进了布置图块。
        loose_hdr = [e for e in blk
                     if e.dxftype() == "TEXT" and e.dxf.get("style", "") == "TBL_CN"]
        if drw is not None:
            loose_hdr += [e for e in drw
                          if e.dxftype() == "TEXT" and e.dxf.get("style", "") == "TBL_CN"]
        for e in loose_hdr:
            try:
                (drw if e in drw else blk).delete_entity(e)
            except Exception:
                pass
        if loose_hdr:
            log("tablefix: 清理旧版散 TEXT 表头 %d 个 → 换死块" % len(loose_hdr))

        def _hdr_inserted():
            if hdr_stored is None:
                return False
            return any(e.dxftype() == "INSERT" and _unescape(e.dxf.name).lower() == HDR_NAME.lower()
                       for e in blk)

        if not emb and not _hdr_inserted():
            try:
                doc.styles.add("TBL_CN", font="simsun.ttc")
            except Exception:
                pass  # 已存在
            if hdr_stored is not None:
                doc.blocks.delete_block(hdr_stored, safe=False)
            hdr = doc.blocks.new(name=HDR_NAME, base_point=(0, 0, 0))
            base = {"layer": "GT_10", "style": "TBL_CN", "width": 0.8}  # (10aa) 设备文字宽 0.8

            def _txt(s, x, y, h, center=False):
                d = dict(base)
                d.update({"text": s, "height": h, "insert": (x, y)})
                if center:
                    d.update({"halign": 1, "align_point": (x, y)})
                hdr.add_text(s, dxfattribs=d)

            _txt("设备一览表", cXt, y0T + 4.5 - 3.5 / 2.0, 3.5, center=True)
            hy = y0T - 2.5 - 2.5 / 2.0
            # (10w) 列宽 12/33/40/24/18=127：描述列 x0+47 不变；荷载列（DESC 横线后段）x0+12+33+40+2=x0+87
            _txt("代号", cXh, hy, 2.5, center=True)
            _txt("设备名称", x0T + 14.0, hy, 2.5)
            _txt("描述DESC", x0T + 47.0, hy, 2.5)
            _txt("荷载", x0T + 87.0, hy, 2.5)
            _txt("标高(m)", cXe, hy, 2.5, center=True)
            # 死块以 (0,0) 插入表格块，块内保持绝对坐标 → 视觉位置不变，且随表格整体挪动
            blk.add_blockref(HDR_NAME, (0, 0, 0))
            log("tablefix: 表头死块已插入表格块（标题+表头 5 文字, TBL_CN/SimSun）")
        elif _hdr_inserted():
            log("tablefix: 表头死块已存在，跳过")

        # 5) 模板残留文字（原点附近 "-"）
        if drw is not None:
            for e in list(drw):
                if e.dxftype() == "TEXT" and (e.dxf.text or "") == "-":
                    p = e.dxf.insert
                    if abs(p.x) < 5.0 and abs(p.y) < 5.0:
                        try:
                            drw.delete_entity(e)
                            log("tablefix: 已删模板残留 '-' @原点")
                        except Exception:
                            pass
    except Exception as ex:
        log("tablefix 异常(继续): %s: %s" % (type(ex).__name__, ex))


def main():
    try:
        import ezdxf
        from ezdxf import bbox as ezbbox
    except ImportError:
        print("NEED_EZDXF")
        return 1
    try:
        dxf_path = sys.argv[1]
        tx0, ty0, tx1, ty1 = (float(v) for v in sys.argv[2:6])
        tbl_name = sys.argv[6] if len(sys.argv) > 6 else "设备一览表"
        drw_name = sys.argv[7] if len(sys.argv) > 7 else "布置图"
        # (10aa) 第 10 参：表格样式（embed=埋板英文表头，headlessDrawing last_tblrect.json style 字段透传）
        tbl_style = sys.argv[10] if len(sys.argv) > 10 else ""

        doc = ezdxf.readfile(dxf_path)
        msp = doc.modelspace()

        # 幂等：已有同名 INSERT 就不再块化（块名可能以 '\u+XXXX' 转义存储，先解码；
        # 比较大小写不敏感——部分 ezdxf 版本把 ASCII 块名归一成小写）
        existing = {_unescape(e.dxf.name).lower() for e in msp if e.dxftype() == "INSERT"}
        blocked = tbl_name.lower() in existing or drw_name.lower() in existing
        if blocked:
            print("SKIP already-blocked")
        else:
            name_map = {_unescape(n): n for n in doc.blocks.block_names()}
            ents = [e for e in msp]
            tbl, drw = [], []
            for e in ents:
                inside = False
                try:
                    if e.dxftype() == "TEXT":
                        # (10aa) 文字按插入点归类：英文表头字串长（宽 0.8 后仍 30mm+），
                        # bbox 必然越出表格矩形 → 全被分进布置图块丢表头（冒烟实证）。
                        # 轴网气泡（h>4.2）随后由 fix_table 挪回布置图块，不受影响。
                        p = e.dxf.insert
                        inside = (tx0 - 0.5 <= p.x <= tx1 + 0.5 and ty0 - 0.5 <= p.y <= ty1 + 0.5)
                    else:
                        bb = ezbbox.extents([e], fast=True)
                        if bb.has_data:
                            inside = (bb.extmin.x >= tx0 - 0.5 and bb.extmin.y >= ty0 - 0.5
                                      and bb.extmax.x <= tx1 + 0.5 and bb.extmax.y <= ty1 + 0.5)
                except Exception:
                    pass
                (tbl if inside else drw).append(e)

            def wrap(name, group):
                if not group:
                    return 0
                old = _blk_lookup(name_map, name)
                if old is not None:
                    doc.blocks.delete_block(old, safe=False)
                blk = doc.blocks.new(name=name, base_point=(0, 0, 0))
                for e in group:
                    msp.unlink_entity(e)
                    blk.add_entity(e)
                msp.add_blockref(name, (0, 0, 0))
                return len(group)

            n_t = wrap(tbl_name, tbl)
            n_d = wrap(drw_name, drw)
            print("OK table=%d drawing=%d" % (n_t, n_d))

        # (10p) 表格修正：块化/跳过后都执行（幂等）
        fix_table(doc, (tx0, ty0, tx1, ty1), tbl_name, drw_name, log=print, style=tbl_style)

        # (10y) 2026-10-25 用户「用系统原来的配色」：colorize 改为默认关——DXF 保持 PLOT 导出的
        # 原始配色；仅当第 9 参显式传 "colorize"（DLL 侧参数 dxfColor=1）时才回填 (10o) 黄/红/紫。
        max_x = None
        if len(sys.argv) > 8:
            try:
                max_x = float(sys.argv[8])
                if max_x <= 0:
                    max_x = None
            except ValueError:
                max_x = None
        want_color = len(sys.argv) > 9 and sys.argv[9].strip().lower() == "colorize"
        if want_color:
            try:
                from dxf_colorize import colorize
                colorize(doc, max_x=max_x, log=print)
            except Exception as exc:
                print("colorize skip: %s: %s" % (type(exc).__name__, exc))
        else:
            print("colorize: off (keep original colors)")

        # 注意：不要原地升版 dxfversion！ezdxf 不支持对已读入文档改 DXF 版本，
        # 强升 R2007 会写出 CAD 无法解析的文件（2026-10-24 用户「CAD空文件」实证）。
        # 保持导出器原版本保存；中文经块/样式按原编码（DWGCODEPAGE）写出。

        # 目标被 CAD 占用时自动另存 *_修正.dxf
        out_path = dxf_path
        try:
            doc.saveas(out_path)
        except PermissionError:
            import os
            root, ext = os.path.splitext(dxf_path)
            out_path = root + "_修正" + (ext or ".dxf")
            doc.saveas(out_path)
        print("OK saved " + out_path)
        return 0
    except Exception as ex:
        sys.stderr.write("ERR %s: %s\n" % (type(ex).__name__, ex))
        return 1


if __name__ == "__main__":
    sys.exit(main())
