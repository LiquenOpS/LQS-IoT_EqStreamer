# eq_client_tui.py
import argparse, socket, sys, time
import numpy as np

def bar_row(vals, width=50):
    # vals: 0..1
    return " ".join("▁▂▃▄▅▆▇█"[min(7, int(v*8))] for v in vals)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=31337)
    ap.add_argument("--hz", type=int, default=30, help="更新頻率")
    args = ap.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((args.host, args.port))
    sock.setblocking(False)
    print(f"Listening UDP on {args.host}:{args.port} ...")

    bands = None
    cur = None
    decay = 0.9

    while True:
        # 吸乾所有封包，只保留最後一包
        latest = None
        while True:
            try:
                data, _ = sock.recvfrom(4096)
            except BlockingIOError:
                break
            if data[:2] == b"EQ" and data[2] == 1:
                latest = data[3:]

        if latest is not None:
            if bands is None:
                bands = len(latest)
                cur = np.zeros(bands, dtype=float)
            target = np.frombuffer(latest, dtype=np.uint8).astype(float)/255.0
            up = target > cur
            cur = np.where(up, cur*0.4 + target*0.6, cur*decay)

        if bands is not None:
            sys.stdout.write("\r" + bar_row(cur))
            sys.stdout.flush()

        time.sleep(1/args.hz)

if __name__ == "__main__":
    main()

