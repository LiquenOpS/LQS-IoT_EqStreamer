import argparse, socket, time, curses
import numpy as np

HEADER = b"EQ"
VERSION = 1

def drain_latest(sock):
    """把 socket 目前的資料吸乾，只回最後一包（符合 EQ 格式才回）。"""
    latest = None
    while True:
        try:
            data, _ = sock.recvfrom(4096)
        except BlockingIOError:
            break
        if len(data) >= 4 and data[:2] == HEADER and data[2] == VERSION:
            latest = data[3:]
    return latest

def main(stdscr, args):
    # 準備 UDP 監聽
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((args.host, args.port))
    sock.setblocking(False)

    curses.curs_set(0)
    stdscr.nodelay(True)

    # 等第一包以決定 bands
    stdscr.addstr(0, 0, f"Listening on {args.host}:{args.port} ... waiting EQ packets (q to quit)")
    stdscr.refresh()
    vals = None
    t0 = time.time()
    while vals is None:
        pkt = drain_latest(sock)
        if pkt is not None:
            vals = np.frombuffer(pkt, dtype=np.uint8)
            break
        if time.time() - t0 > 2:
            stdscr.addstr(1, 0, "Still waiting... ensure sender is broadcasting to this subnet/port.")
            stdscr.refresh()
            t0 = time.time()
        time.sleep(0.01)
        try:
            ch = stdscr.getch()
            if ch in (ord('q'), ord('Q')):
                return
        except curses.error:
            pass

    bands = len(vals)

    # 主 loop：每幀吸乾封包→畫畫面
    frame_interval = 1.0 / max(1, args.fps)
    while True:
        t_start = time.time()

        pkt = drain_latest(sock)
        if pkt is not None:
            if len(pkt) == bands:
                vals = np.frombuffer(pkt, dtype=np.uint8)
            else:
                # 若發送端的 BANDS 改了，就跟著改
                bands = len(pkt)
                vals = np.frombuffer(pkt, dtype=np.uint8)

        # 計算「盡量正方形」的繪圖區
        H, W = stdscr.getmaxyx()
        side = max(4, min(H - 2, W - 2))          # 取可用範圍內最大正方
        bar_h = side - 1                           # 垂直像素（行數）
        # 每柱寬度（至少 1），如果螢幕太窄就自動壓縮
        bar_w = max(1, side // max(1, bands))
        used_w = bar_w * bands
        ox = (W - used_w) // 2                    # 置中
        oy = (H - bar_h) // 2

        # 0..255 直接線性映射到高度
        heights = (vals.astype(np.float32) / 255.0 * bar_h + 0.5).astype(int)
        heights = np.clip(heights, 0, bar_h)

        # 繪圖
        stdscr.erase()
        stdscr.addstr(max(0, oy - 1), max(0, ox),
                      f"UDP EQ (square TUI)  bands={bands}  q:quit")

        # 畫底線
        base_y = oy + bar_h
        for x in range(used_w):
            X = ox + x
            if 0 <= base_y < H and 0 <= X < W:
                stdscr.addch(base_y, X, ord('_'))

        # 畫每一柱（無任何補償/平滑）
        for i, h in enumerate(heights):
            x0 = ox + i * bar_w
            # 清柱並填滿高度
            for y in range(bar_h):
                Y = base_y - 1 - y
                ch = ord('█') if y < h else ord(' ')
                for dx in range(bar_w):
                    X = x0 + dx
                    if 0 <= Y < H and 0 <= X < W:
                        stdscr.addch(Y, X, ch)

        stdscr.refresh()

        # 鍵盤
        try:
            ch = stdscr.getch()
            if ch in (ord('q'), ord('Q')):
                break
        except curses.error:
            pass

        # 控制 FPS
        dt = time.time() - t_start
        if dt < frame_interval:
            time.sleep(frame_interval - dt)

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=31337)
    ap.add_argument("--fps", type=int, default=30)
    args = ap.parse_args()
    curses.wrapper(main, args)

