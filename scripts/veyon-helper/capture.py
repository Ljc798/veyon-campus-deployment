#!/usr/bin/env python3
"""Capture exactly one Veyon Screenshot frame for a normalized target."""

import glob
import os
import re
import signal
import stat
import subprocess
import sys
import time


VEYON_CLI = "/usr/local/bin/veyon-cli"
EXPECTED_VEYON_VERSION = "4.11.4"
SCREENSHOT_DIRECTORY = "/run/veyon-helper/screenshots"
MAXIMUM_INPUT_BYTES = 32 * 1024 * 1024
MAXIMUM_INPUT_PIXELS = 50_000_000
COMMAND_TIMEOUT_SECONDS = 45
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
TARGET_PATTERN = re.compile(r"\A[a-zA-Z0-9.-]{1,253}\Z|\A[0-9a-fA-F:.]{2,45}\Z")


def fail(message, exit_code=1):
    sys.stderr.write(message[:600] + "\n")
    raise SystemExit(exit_code)


def file_fingerprint(path):
    info = os.lstat(path)
    return info.st_size, info.st_mtime_ns, info.st_ino


def matching_screenshots(file_target):
    pattern = os.path.join(
        SCREENSHOT_DIRECTORY,
        "*_" + glob.escape(file_target) + "_*.png",
    )
    return glob.glob(pattern)


def delete_if_unchanged(path, fingerprint):
    try:
        if file_fingerprint(path) == fingerprint:
            os.unlink(path)
    except FileNotFoundError:
        pass


def cleanup_unambiguous_new_screenshot(file_target, before, started_at):
    candidates = []
    for path in matching_screenshots(file_target):
        try:
            info = os.lstat(path)
        except FileNotFoundError:
            continue
        if path not in before and info.st_mtime >= started_at - 1:
            candidates.append((path, file_fingerprint(path)))
    if len(candidates) == 1:
        delete_if_unchanged(*candidates[0])


def validate_png(data):
    if len(data) < 24 or len(data) > MAXIMUM_INPUT_BYTES or not data.startswith(PNG_SIGNATURE):
        fail("Veyon 返回的截图不是有效 PNG 或超过大小限制。")
    if data[12:16] != b"IHDR":
        fail("Veyon 返回的 PNG 缺少图像头。")
    width = int.from_bytes(data[16:20], "big")
    height = int.from_bytes(data[20:24], "big")
    if width < 1 or height < 1 or width * height > MAXIMUM_INPUT_PIXELS:
        fail("Veyon 返回的截图尺寸超过限制。")


def main():
    if len(sys.argv) != 2:
        fail("helper 只接受一个课堂目标。", 2)
    target = sys.argv[1]
    if not TARGET_PATTERN.fullmatch(target):
        fail("课堂目标格式无效。", 2)

    version = subprocess.run(
        [VEYON_CLI, "--version"],
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=5,
    )
    if version.returncode != 0 or version.stdout.strip() != EXPECTED_VEYON_VERSION:
        fail("Veyon helper 版本不匹配；需要 " + EXPECTED_VEYON_VERSION + "。")

    os.makedirs(SCREENSHOT_DIRECTORY, mode=0o700, exist_ok=True)
    directory_info = os.lstat(SCREENSHOT_DIRECTORY)
    if not stat.S_ISDIR(directory_info.st_mode) or stat.S_ISLNK(directory_info.st_mode):
        fail("Veyon 临时截图目录不是安全的普通目录。")
    os.chmod(SCREENSHOT_DIRECTORY, 0o700)

    file_target = target.replace(":", "-")
    before = {}
    for path in matching_screenshots(file_target):
        try:
            before[path] = file_fingerprint(path)
        except FileNotFoundError:
            continue

    started_at = time.time()
    try:
        result = subprocess.run(
            [VEYON_CLI, "feature", "start", target, "Screenshot"],
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            timeout=COMMAND_TIMEOUT_SECONDS,
        )
    except subprocess.TimeoutExpired:
        cleanup_unambiguous_new_screenshot(file_target, before, started_at)
        fail("等待 Veyon 屏幕画面超时；已停止本次采集。")
    except OSError as error:
        fail("无法启动固定版本的 Veyon CLI：" + str(error))

    completed_at = time.time()
    if result.returncode != 0:
        cleanup_unambiguous_new_screenshot(file_target, before, started_at)
        detail = result.stderr.decode("utf-8", errors="replace").strip()
        fail("Veyon 未能取得该电脑的屏幕画面。" + (" " + detail[-240:] if detail else ""))

    candidates = []
    for path in matching_screenshots(file_target):
        try:
            info = os.lstat(path)
        except FileNotFoundError:
            continue
        if path not in before and started_at - 1 <= info.st_mtime <= completed_at + 2:
            candidates.append(path)
    if len(candidates) != 1:
        for path, fingerprint in before.items():
            try:
                if file_fingerprint(path) != fingerprint:
                    fail("Veyon 截图与已有文件冲突；为保护原文件，本次预览已取消。")
            except FileNotFoundError:
                fail("Veyon 截图与已有文件冲突；为保护原文件，本次预览已取消。")
        fail("无法唯一定位本次 Veyon 截图；没有读取或删除不确定的文件。")

    screenshot_path = candidates[0]
    fingerprint = None
    try:
        fingerprint = file_fingerprint(screenshot_path)
        info = os.lstat(screenshot_path)
        if not stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode):
            fail("Veyon 新截图不是普通文件。")
        if info.st_size < 1 or info.st_size > MAXIMUM_INPUT_BYTES:
            fail("Veyon 原始截图大小超过限制。")
        flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
        descriptor = os.open(screenshot_path, flags)
        try:
            with os.fdopen(descriptor, "rb", closefd=False) as screenshot:
                data = screenshot.read(MAXIMUM_INPUT_BYTES + 1)
        finally:
            os.close(descriptor)
        if file_fingerprint(screenshot_path) != fingerprint:
            fail("Veyon 截图在读取期间发生变化；本次预览已取消。")
        validate_png(data)
        sys.stdout.buffer.write(data)
        sys.stdout.buffer.flush()
    finally:
        if fingerprint is not None:
            delete_if_unchanged(screenshot_path, fingerprint)


if __name__ == "__main__":
    try:
        main()
    except subprocess.TimeoutExpired:
        fail("Veyon helper 操作超时。")
    except (OSError, ValueError) as error:
        fail("Veyon helper 操作失败：" + str(error))
