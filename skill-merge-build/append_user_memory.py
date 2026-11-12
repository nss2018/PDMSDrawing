"""Repair legacy backspace artifacts and append cross-client skill sync notes."""
import io

P = r"C:\Users\Nie\.workbuddy\MEMORY.md"
s = io.open(P, encoding="utf-8").read()
before = s.count("\x08") + s.count("\\bsk")
s = s.replace("\x08sk", "bsk").replace("\\bsk", "bsk")

NOTE = """

## 本机双客户端技能同步（2026-11-08）
- WorkBuddy 用户技能在 C:\\Users\\Nie\\.workbuddy\\skills；ACCIO 技能根 = D:\\skill（全局共享）+ C:\\Users\\Nie\\.accio\\accounts\\<账号>\\skills（当前常用 7100633559，PDMS 资料号 1796501114）。
- 同步脚本双写：D:\\repos\\pdmsDrawing\\tools\\sync_skills_to_accio.py 与 C:\\Users\\Nie\\.workbuddy\\scripts\\sync_skills_to_accio.py。增量不删、覆盖前 zip 备份、SHA256 校验、--dry-run 预演、--retire 可选归档旧副本。
- ACCIO 的 skills_config.json / skills.jsonc / remote_skills_cache.json 由客户端自托管，**禁止手改**（会产生看着配好但运行时不加载的假 ID）；只同步目录，改完重启 ACCIO。
- ACCIO 的 agent-core/skills 是 10-28 旧源，别再跑旧 sync_skills.py（会把 account/D:\\skill 回滚）；也别往 agent-core 同步。
"""

if "本机双客户端技能同步（2026-11-08）" not in s:
    s = s.rstrip() + NOTE
with io.open(P, "w", encoding="utf-8", newline="\n") as f:
    f.write(s)
print("repaired artifacts:", before)
print("size:", len(s))
print("tail:")
print("\n".join(s.splitlines()[-7:]))
