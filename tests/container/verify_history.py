"""只读核对独立 Mock 场景的数据库副本，不连接生产账号或修改记录。"""

import sqlite3
import sys
from pathlib import Path


def main() -> None:
    """确认合成场景只有一条入站 ping 和一次已确认 pong，重启后也复用此断言。"""
    path = Path(sys.argv[1]).resolve()
    with sqlite3.connect(path.as_uri() + "?mode=ro", uri=True) as database:
        rows = database.execute(
            "SELECT m.Direction, m.Delivery, s.Kind, s.Text "
            "FROM Messages m JOIN MessageSegments s ON s.MessageId=m.Id "
            "WHERE m.CanonicalId IS NULL ORDER BY m.Id, s.Position"
        ).fetchall()
        assert rows == [(0, 0, "text", "ping"), (1, 3, "text", "pong")], rows
        assert database.execute("SELECT COUNT(*) FROM PlatformEvents").fetchone()[0] == 0
    print("合成消息入库、确认回执和无重复发送验证通过。")


if __name__ == "__main__":
    main()
