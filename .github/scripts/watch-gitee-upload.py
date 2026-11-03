# FilePath    : pbi-utils\.github\scripts\watch-gitee-upload.py
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 仅记录 Linux TCP 传输计数, 不读取命令行、环境、请求正文或凭据.
import json
import subprocess
import time

# ss -tin 的第二行只含 TCP 指标; 不记录包含远端地址的 socket 首行.
# 在工作流自身退出时由 runner 清理, 最长运行 420 秒; 无 ss 时诊断明确不可用.
for sample in range(85):
    result = subprocess.run(["ss", "-tin", "state", "established", "dport", "=", ":443"], capture_output=True, text=True, check=False)
    counters = [line.strip() for line in result.stdout.splitlines() if line.startswith("\t")]
    print(json.dumps({"elapsed_seconds": sample * 5, "exit_code": result.returncode, "tcp_counters": counters}), flush=True)
    time.sleep(5)
