"""Explicit operator-run initialization; secrets exist only in the migration child environment."""
import getpass
import os
from pathlib import Path
import subprocess
import tomllib

root = Path(__file__).resolve().parents[1]
config = Path(os.environ.get("YF_CONFIG", str(root / "config.local.toml")))
settings = tomllib.loads(config.read_text(encoding="utf-8-sig"))
database = os.environ.get("YF_DATABASE_URL", settings["database"]["url"])
if not database:
    raise SystemExit("请先配置数据库连接；初始化不会创建数据库实例。")
password = getpass.getpass("首次管理员密码（12–64 字符，不回显）：")
if password != getpass.getpass("再次输入初始密码："):
    raise SystemExit("两次密码不一致；未运行迁移。")
env = os.environ.copy()
env["DATABASE_URL"] = database
env["YF_BOOTSTRAP_PASSWORD"] = password
try:
    result = subprocess.run(["cargo", "run", "-p", "migration", "--locked", "--", "up"], cwd=root, env=env)
finally:
    env.pop("YF_BOOTSTRAP_PASSWORD", None)
    env.pop("DATABASE_URL", None)
    password = None
raise SystemExit(result.returncode)
