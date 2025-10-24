import socket
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.bind(('', 31337))
print('listening 31337...')
while True:
    data, addr = sock.recvfrom(4096)
    if data[:2] == b'EQ' and data[2] == 1:
        bands = list(data[3:])
        print(len(bands), bands[:8], '...', bands[-8:], ' from', addr)

