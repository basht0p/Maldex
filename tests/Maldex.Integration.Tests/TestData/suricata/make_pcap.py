#!/usr/bin/env python3
"""Deterministically writes dev-test.pcap: one TCP session with an HTTP GET for Host maldex-test.invalid.

Usage: python3 make_pcap.py [output]   (stdlib only; fixed timestamps, so the output is byte-for-byte reproducible)
"""
import struct
import sys

CLIENT_MAC = bytes.fromhex("020000000002")
SERVER_MAC = bytes.fromhex("020000000001")
CLIENT_IP = bytes([10, 213, 0, 2])
SERVER_IP = bytes([203, 0, 113, 9])
CPORT, SPORT = 49152, 80


def checksum(data: bytes) -> int:
    if len(data) % 2:
        data += b"\0"
    s = sum(struct.unpack("!%dH" % (len(data) // 2), data))
    while s >> 16:
        s = (s & 0xFFFF) + (s >> 16)
    return ~s & 0xFFFF


def frame(from_client: bool, seq: int, ack: int, flags: int, payload: bytes, ident: int) -> bytes:
    src_ip, dst_ip = (CLIENT_IP, SERVER_IP) if from_client else (SERVER_IP, CLIENT_IP)
    sport, dport = (CPORT, SPORT) if from_client else (SPORT, CPORT)
    tcp = struct.pack("!HHIIBBHHH", sport, dport, seq, ack, 5 << 4, flags, 65535, 0, 0) + payload
    pseudo = src_ip + dst_ip + struct.pack("!BBH", 0, 6, len(tcp))
    tcp = tcp[:16] + struct.pack("!H", checksum(pseudo + tcp)) + tcp[18:]
    ip = struct.pack("!BBHHHBBH", 0x45, 0, 20 + len(tcp), ident, 0x4000, 64, 6, 0) + src_ip + dst_ip
    ip = ip[:10] + struct.pack("!H", checksum(ip)) + ip[12:]
    eth = (SERVER_MAC + CLIENT_MAC if from_client else CLIENT_MAC + SERVER_MAC) + b"\x08\x00"
    return eth + ip + tcp


SYN, ACK, PSH, FIN = 0x02, 0x10, 0x08, 0x01
request = b"GET /payload.bin HTTP/1.1\r\nHost: maldex-test.invalid\r\nUser-Agent: maldex-test\r\nConnection: close\r\n\r\n"
body = b"ok"
response = b"HTTP/1.1 200 OK\r\nContent-Length: %d\r\nConnection: close\r\n\r\n" % len(body) + body

c, s = 1000, 5000
frames = [
    frame(True, c, 0, SYN, b"", 1),
    frame(False, s, c + 1, SYN | ACK, b"", 2),
    frame(True, c + 1, s + 1, ACK, b"", 3),
    frame(True, c + 1, s + 1, PSH | ACK, request, 4),
    frame(False, s + 1, c + 1 + len(request), ACK, b"", 5),
    frame(False, s + 1, c + 1 + len(request), PSH | ACK, response, 6),
    frame(True, c + 1 + len(request), s + 1 + len(response), ACK, b"", 7),
    frame(False, s + 1 + len(response), c + 1 + len(request), FIN | ACK, b"", 8),
    frame(True, c + 1 + len(request), s + 2 + len(response), FIN | ACK, b"", 9),
    frame(False, s + 2 + len(response), c + 2 + len(request), ACK, b"", 10),
]

out = sys.argv[1] if len(sys.argv) > 1 else "dev-test.pcap"
with open(out, "wb") as f:
    f.write(struct.pack("<IHHiIII", 0xA1B2C3D4, 2, 4, 0, 0, 65535, 1))
    for i, fr in enumerate(frames):
        f.write(struct.pack("<IIII", 1760000000 + i // 5, (i % 5) * 1000, len(fr), len(fr)))
        f.write(fr)
